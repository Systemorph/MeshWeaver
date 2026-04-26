---
Name: Writing Tests in MeshWeaver
Category: Architecture
Description: How to write correct, non-flaky tests against a reactive distributed mesh. Covers the CQRS-correct read pattern, deadlock-safe assertions, and the canonical test-base setup.
Icon: Beaker
---

# Writing Tests in MeshWeaver

Tests are the one place `await` on hub work is allowed — but that privilege is narrow. A test that reads the mesh the wrong way doesn't fail honestly: it returns stale content (CQRS lag), deadlocks on a hub pump, or hangs until the inactivity timer aborts the whole CI run. The rules below come out of chasing actual incidents in this repo.

Before you write a test, read these architecture docs — they define the invariants tests have to preserve, not just the ones production code has to follow:

- [Asynchronous Calls](AsynchronousCalls) — what you can and can't `await`
- [CQRS — Queries vs. Content Access](CqrsAndContentAccess) — why `QueryAsync` is the wrong read after a write
- [Data Binding](xref:GUI/DataBinding) — layout areas declare, views subscribe, tests assert against the subscription path
- [Test State Isolation](TestStateIsolation) — required reading if your tests share a cluster fixture (Orleans, any `ICollectionFixture<>` that builds the mesh once)

## The golden rules

1. **Test code MAY `await`. Everything it calls into MAY NOT.** Your test is allowed to `await myService.DoWork(...).FirstAsync()` at the xunit boundary. But the service you're testing must still return `IObservable<T>` — you're just converting at the test edge, not at the service edge. Never add an `async`/`Task` return type to production code "just to make the test work".
2. **Reads after writes use a stream, never a query.** `await MeshQuery.QueryAsync<MeshNode>($"path:{X}").FirstOrDefaultAsync()` is lagged — it goes through the read-side index and returns stale content right after a write. Use `ReadNodeAsync(path)` (below) or `workspace.GetRemoteStream<MeshNode, MeshNodeReference>(addr, new MeshNodeReference())`.
3. **Queries are only for sets and existence.** Listing children, counting matches, asserting "namespace is empty" — those are legitimate `QueryAsync` / `ObserveQuery` uses. Reading a specific node's content is not.
4. **No mocking of `IMessageHub`, `IMeshService`, `IMeshStorage`.** Tests that need infrastructure inherit from `MonolithMeshTestBase` or `OrleansTestBase`. If a service mocks wrong, the real behaviour is different in CI — the mock passes, production fails.
5. **Propagate exceptions.** Timeouts, cancellations, and delivery failures are real test failures. Don't wrap them in `try { ... } catch { return null; }` to make a red test green — you'll make a flaky test green instead.

## The canonical test base

```csharp
public class MyFeatureTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph()
            .AddSampleUsers()
            .ConfigureHub(hub => hub.AddMyFeature());

    [Fact]
    public async Task UpdateNode_SurfacesNewName()
    {
        var orgId = $"Org_{Guid.NewGuid():N}"[..12];
        await NodeFactory.CreateNode(new MeshNode(orgId) { Name = "Original", NodeType = "Markdown" });

        var updated = MeshNode.FromPath(orgId) with { Name = "Renamed", NodeType = "Markdown" };
        await NodeFactory.UpdateNode(updated);

        // ✅ Stream read — authoritative, never lagged.
        var node = await ReadNodeAsync(orgId);
        node!.Name.Should().Be("Renamed");
    }
}
```

`MonolithMeshTestBase`:
- Logs in an admin user (`TestUsers.DevLogin`) in `InitializeAsync` — your tests start authenticated.
- Exposes `NodeFactory` / `MeshQuery` (both → `IMeshService`) and `PathResolver`.
- Exposes `ReadNodeAsync(path)` — the canonical CQRS-correct single-node read.
- Disposes the mesh cleanly on teardown.

`OrleansTestBase` gives you the same shape for distributed tests using an Orleans TestCluster. Same `ReadNodeAsync` shape via `MeshNodeTestReadExtensions` on `IMessageHub`.

## `ReadNodeAsync` — the one you want

```csharp
// Shared helper in MeshWeaver.Mesh.Testing (extension on IMessageHub)
var node = await hub.ReadNodeAsync(path, ct);

// Same thing, delegating from MonolithMeshTestBase
var node = await ReadNodeAsync(path, ct);
```

Under the hood it posts `GetDataRequest(new MeshNodeReference())` to the per-node hub. The owning hub's reducer is authoritative — no catalog / index lag, no stale content right after a write.

**Return semantics:**
- Node exists → returns the `MeshNode`.
- Node does not exist (routing says `ErrorType.NotFound`) → returns `null`.
- Anything else (timeout, cancellation, delivery failure with a different error) → propagates the exception.

Use the `null` branch only for "node was deleted / never existed" assertions. Don't catch timeouts yourself — a timeout means something is hung, which is a real test bug.

```csharp
// ✅ "expect found" after create/update — bare stream read
var created = await ReadNodeAsync(orgId);
created.Should().NotBeNull();

// ✅ "expect not found" after delete — the NotFound null surfaces naturally
await NodeFactory.DeleteNode(orgId);
var deleted = await ReadNodeAsync(orgId);
deleted.Should().BeNull();
```

## What NOT to do in tests

### ❌ `QueryAsync` to read a just-written node

```csharp
// WRONG — read-side index is eventually consistent. Returns stale content.
await NodeFactory.UpdateNode(updated);
var found = await MeshQuery
    .QueryAsync<MeshNode>($"path:{orgId}")
    .FirstOrDefaultAsync();
found!.Name.Should().Be("Renamed");  // flaky: index may still have "Original"
```

The failure mode is the worst kind: the assertion *sometimes* passes under low load and *sometimes* fails in CI. Switch to `ReadNodeAsync(orgId)`.

### ❌ Swallowing timeouts and cancellations

```csharp
// WRONG — a hung read is a real bug. Silently mapping it to null hides the hang
// and turns "deadlock in handler X" into "test says node was null".
try { return await stream.Take(1).Timeout(TimeSpan.FromSeconds(10)).ToTask(ct); }
catch (TimeoutException) { return null; }
catch (OperationCanceledException) { return null; }
```

Let exceptions propagate. If a test method times out, xunit's `[Fact(Timeout = ...)]` or the overall inactivity guard surfaces the failure with a stack trace — that's what lets you find the deadlock.

### ❌ Mocking core services

```csharp
// WRONG — mock says writes succeed; production migration is broken.
var mock = new Mock<IMeshService>();
mock.Setup(s => s.CreateNode(It.IsAny<MeshNode>())).Returns(Observable.Return(mockNode));
```

The test passes. The migration ships. Prod breaks. Use `MonolithMeshTestBase` and let the real service do the work; if that's too slow, the service's contract is wrong — fix the contract, not the test.

### ❌ Polling loops around `QueryAsync`

```csharp
// WRONG — even with a sleep, the index may still lag. Wastes time, still flaky.
for (var i = 0; i < 20; i++)
{
    var n = await MeshQuery.QueryAsync<MeshNode>($"path:{p}").FirstOrDefaultAsync();
    if (n is { State: MeshNodeState.Active }) break;
    await Task.Delay(100);
}
```

If you need to wait for a state change, subscribe to the stream and filter:

```csharp
var workspace = Mesh.GetWorkspace();
await workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
        new Address(p), new MeshNodeReference())
    .Where(c => c.Value is { State: MeshNodeState.Active })
    .Take(1)
    .Timeout(TimeSpan.FromSeconds(10))
    .ToTask(ct);
```

### ❌ `async ctx =>` click actions in test-only setup code

Even in test fixtures, stay consistent with the production rule: click actions are sync, use `Subscribe` + `RegisterCallback`. A fixture that spawns a browser session to click a real UI button is the one exception, and it should be clearly labelled.

## Comprehensive coverage per the Coder agent rules

[`Coder.md`](xref:Agent/Coder) sets the testing bar for new NodeTypes and data models: **comprehensive unit tests per invariant + branch + boundary + degenerate-input**, not "at least one test per feature". Applies equally to hand-written code. A NodeType with a single happy-path test is demoed, not tested. Bring the suite up before you merge.

## Running tests

Always run in the background with `run_in_background: true` — tests can take minutes and you don't want to block the conversation. Run from the repo root with sub-paths:

```bash
dotnet test test/MeshWeaver.NodeOperations.Test --no-build --no-restore
dotnet test test/MeshWeaver.Acme.Test --no-build --no-restore --filter "FullyQualifiedName~TodoDataChangeWorkflowTest"
```

Don't use `--verbosity minimal` when a failure is expected — minimal hides stack traces and forces a rerun with higher verbosity. Default verbosity surfaces errors the first time.

**Run → read → fix → run once more.** Do not rerun a hung or failing test 2-3 times "to see" — the framework already emits a structured `MESSAGE_FLOW:` trace at Trace level (see [DebuggingMessageFlow](DebuggingMessageFlow)). Run once, grep the trace, fix the root cause.

## Test-project layout

- Framework code in `src/`, tests in `test/`.
- One `*.Test` project per production project where it makes sense.
- Shared fixtures go in `src/MeshWeaver.Hosting.Monolith.TestBase` / `src/MeshWeaver.Hosting.Orleans.Test.Base`.
- NodeType-specific tests live inside the NodeType's folder (`Namespace/Type/Test/TypeTest.cs`) — see the SocialMedia canonical example.

## References

- [Asynchronous Calls](AsynchronousCalls) — hub handler rules (no `await` in production; tests are the exception)
- [CQRS — Queries vs. Content Access](CqrsAndContentAccess) — why `ReadNodeAsync` is the right read
- [Data Binding](xref:GUI/DataBinding) — the GUI pattern tests assert against
- [Debugging Message Flow](DebuggingMessageFlow) — how to read the framework's own trace when a test hangs
- `src/MeshWeaver.Mesh.Contract/Testing/MeshNodeTestReadExtensions.cs` — source of `ReadNodeAsync`
- `src/MeshWeaver.Hosting.Monolith.TestBase/MonolithMeshTestBase.cs` — the test base every monolith test inherits
