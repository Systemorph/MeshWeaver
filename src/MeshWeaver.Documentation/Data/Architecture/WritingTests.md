---
Name: Writing Tests in MeshWeaver
Category: Architecture
Description: How to write correct, non-flaky tests against a reactive distributed mesh. Test bodies are synchronous and assert on observables directly; covers the CQRS-correct read pattern, the deadlock-safe reactive assertion surface, and the canonical test-base setup.
Icon: Beaker
---

# Writing Tests in MeshWeaver

The platform is reactive end-to-end, and its tests are too. A MeshWeaver test method is **`void`**, has **no `async` / `await` / `.ToTask()` / `.FirstAsync()`**, and asserts on `IObservable<T>` **directly** through the `MeshWeaver.Reactive.Assertions` surface (`.Should().Within(t).Emit()`, `.Match(...)`, `.Be(...)`). The blocking wait that turns "a stream emitted X" into a pass/fail lives *inside* the assertion, never in the test body.

This is not a style preference. A test that reads the mesh the wrong way doesn't fail honestly: it returns stale content (CQRS lag), or it mixes a blocking assertion with an `await` and **deadlocks** the hub pump until the inactivity guard aborts the CI run. The rules below come out of chasing real incidents in this repo.

Before you write a test, read the invariants tests must preserve:

- [Asynchronous Calls](AsynchronousCalls) — why hub-reachable code is `IObservable<T>`, never `Task<T>`
- [Reactive Test Assertions](ReactiveTestAssertions) — the full assertion API, the §2a deadlock rule, and the catalogue of legitimately-async cases
- [CQRS — Queries vs. Content Access](CqrsAndContentAccess) — why a query is the wrong read after a write
- [Data Binding](xref:GUI/DataBinding) — layout areas declare, views subscribe, tests assert against the subscription path
- [Test State Isolation](TestStateIsolation) — required if your tests share a cluster fixture (Orleans, any `ICollectionFixture<>` that builds the mesh once)

## The golden rules

1. **Test bodies are `void` and reactive.** Assert on the observable: `obs.Should().Within(10.Seconds()).Match(x => predicate)`. The assertion subscribes, blocks up to the timeout for the emission you describe, and returns it. No `async Task`, no `await`, no `.FirstAsync().ToTask()` anywhere in the body.
2. **A blocking `.Should()` and an `await` cannot coexist in the same method.** xUnit runs an `async Task` test under a SynchronizationContext; a blocking reactive assertion holds that thread while the hub tries to deliver the emission on it → deadlock. So: the instant a method uses `.Should().Emit()/.Match()/.Be()`, it must be `void` with every `await` removed. (See [Reactive Test Assertions §2](ReactiveTestAssertions) for the few methods that legitimately stay `async` — and why they then must *not* use a blocking assertion.)
3. **Reads after writes use a stream, never a query.** A query goes through the lagged read-side index and returns stale content right after a write. Read a known node with `ReadNode(path).Should().Emit()` (test base) or `workspace.GetMeshNodeStream(path)` / `workspace.GetRemoteStream<MeshNode, MeshNodeReference>(addr, new MeshNodeReference())`.
4. **Queries are only for sets and existence.** Listing children, counting matches, "namespace is empty" — legitimate `ObserveQuery`. Reading a specific node's content is not.
5. **No mocking of `IMessageHub`, `IMeshService`, `IMeshStorage`.** Inherit `MonolithMeshTestBase` or `OrleansTestBase` and let the real services run. A mock that passes while production fails is worse than no test.
6. **Let failures propagate.** Timeouts, cancellations, and delivery failures are real test failures — the reactive assertions surface them for you. Never wrap a read in `try { … } catch { return null; }`; you'd turn a flaky bug into a green-but-lying test. To assert an *expected* error, use `.Materialize()` (rule below), not a swallowing catch.

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
    public void UpdateNode_SurfacesNewName()                       // ← void, not async Task
    {
        var orgId = $"Org_{Guid.NewGuid():N}"[..12];
        NodeFactory.CreateNode(new MeshNode(orgId)
            { Name = "Original", NodeType = "Markdown" }).Should().Emit();   // ← subscribe = do the write

        var updated = MeshNode.FromPath(orgId) with { Name = "Renamed", NodeType = "Markdown" };
        NodeFactory.UpdateNode(updated).Should().Emit();

        // ✅ Authoritative owner-hub read — never lagged.
        var node = ReadNode(orgId).Should().Emit();
        node!.Name.Should().Be("Renamed");
    }
}
```

`MonolithMeshTestBase`:
- Logs in an admin user (`TestUsers.DevLogin`) — your tests start authenticated.
- Exposes `NodeFactory` / `MeshQuery` (both → `IMeshService`) and `PathResolver`.
- Exposes `ReadNode(path)` — the canonical CQRS-correct single-node read, returning `IObservable<MeshNode?>`.
- Disposes the mesh cleanly on teardown.

`OrleansTestBase` gives the same shape for distributed tests on an Orleans TestCluster.

> **Cold observables: `Should()` is the subscribe.** `NodeFactory.CreateNode(...)`, `UpdateNode`, `DeleteNode`, and `hub.Observe(...)` are **cold** — the side effect (the write, the request) runs on subscribe, not on call. The blocking `.Should().Emit()` subscribes and so performs the work *and* waits for it to land. A bare `NodeFactory.CreateNode(node);` with no `.Should()` / `.Subscribe()` does nothing.

## `ReadNode` — the authoritative single-node read

```csharp
// On the test base (delegates to the IMeshService-backed owner-hub read):
var node = ReadNode(path).Should().Within(ReadNodeTimeout).Emit();
```

Under the hood it reads the owning per-node hub's reducer — no catalog/index lag, no stale content right after a write. Emission semantics:
- Node exists → emits the `MeshNode`.
- Node does not exist (routing says `NotFound`) → emits `null`.
- Anything else (timeout, delivery failure) → the stream errors, and `.Should().Emit()` fails the test with the underlying message.

```csharp
// ✅ "expect found" after create/update
ReadNode(orgId).Should().Match(n => n is { Name: "Renamed" });

// ✅ "expect not found" after delete — the NotFound null surfaces naturally
NodeFactory.DeleteNode(orgId).Should().Emit();
ReadNode(orgId).Should().Match(n => n is null);
```

## The reactive assertion surface

From `MeshWeaver.Reactive.Assertions` (global `using` in every test project). Full reference: [Reactive Test Assertions](ReactiveTestAssertions).

| Call | Meaning |
|---|---|
| `obs.Should().Emit()` | block ≤ timeout for the first emission; **return it** |
| `obs.Should().Match(x => pred)` | block for the first emission satisfying `pred`; return it |
| `obs.Should().Be(expected)` | first emission equals `expected` |
| `obs.Should().Complete()` | stream completes within the timeout |
| `obs.Should().NotEmit(within: 200.Milliseconds())` | nothing arrives — the **one** place a fixed wait is correct |
| `obs.Should().Within(t)....` | override the default 10 s timeout for this chain |

`.Emit()` / `.Match()` **return** the matched value, so they replace `var x = await obs....ToTask()` one-for-one: `var x = obs.Should().Within(t).Match(...)`.

**Fold the wait into the predicate.** Don't take the first emission and hope it's the right one — describe the state you're waiting for:

```csharp
// ❌ grabs whatever lands first — stale/partial on cold-start CI
var msgs = stream.Should().Emit();
msgs.Count.Should().Be(2);

// ✅ waits for the emission where the invariant actually holds
var msgs = stream.Should().Within(45.Seconds()).Match(m => m.Count == 2);
```

## Asserting an expected error — `.Materialize()`, not `ThrowAsync`

A reactive `.Should().Emit()/.Match()` wraps an OnError in an assertion exception (it does **not** rethrow the original type), so you can't catch the original with an `Action.Should().Throw<T>()`. To assert a stream *errors with a specific type*, materialize OnError into a value:

```csharp
var error = cache.GetStream(missingPath)
    .Where(n => n?.Content is not null)
    .Take(1)
    .Materialize()
    .Should().Within(5.Seconds()).Match(n => n.Kind == NotificationKind.OnError);
error.Exception.Should().BeOfType<DeliveryFailureException>();
```

`using System.Reactive;` provides `NotificationKind`. This replaces the old `await act.Should().ThrowAsync<T>()` shape.

## Waiting for a state to change over time

`GetMeshNodeStream` / `GetRemoteStream` are **live** — they replay current state and keep emitting. Submit first, then assert on the live stream; the assertion catches the settled state whenever it lands:

```csharp
client.SubmitMessage(threadPath, "Hi", contextPath: "TestUser");

var idle = workspace.GetMeshNodeStream(threadPath)
    .Select(node => node?.Content as MeshThread)
    .Should().Within(45.Seconds()).Match(t => t is { Status: ThreadExecutionStatus.Idle });
```

When the source is **request/response with no stream surface** (a `GetDataRequest`, a query snapshot), poll reactively — the interval is the cadence, the `.Match` is the condition, `.Within` is the deadline:

```csharp
var match = Observable.Interval(50.Milliseconds()).StartWith(0L)
    .SelectMany(_ => meshService.QueryAsync<MeshNode>("nodeType:Story").ToObservable().ToList())
    .Should().Within(15.Seconds()).Match(list => list.Count >= 3);
```

…or, for a synced query, prefer `MeshService.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(q))` and match on `c.ChangeType == QueryChangeType.Initial` (its first emission is the full snapshot the old `QueryAsync().ToListAsync()` returned).

## What NOT to do

### ❌ A query to read a just-written node
```csharp
NodeFactory.UpdateNode(updated).Should().Emit();
var found = meshService.QueryAsync<MeshNode>($"path:{orgId}").ToObservable()
    .Should().Emit();                       // flaky: index may still hold "Original"
```
The read-side index is eventually consistent. Use `ReadNode(orgId)`.

### ❌ Asserting "exactly N change events"
A change feed (pg_notify, any synced query) can deliver follow-ups for a write the subscription wired up *after* the row existed. Filter on the emission **shape**, not the count:
```csharp
var initial = meshService.ObserveQuery<MeshNode>(req)
    .Should().Within(10.Seconds()).Match(c => c.ChangeType == QueryChangeType.Initial);
initial.Items.Should().HaveCount(1);
```

### ❌ `Task.Delay` / `Thread.Sleep` to "wait for propagation"
Fold the wait into `.Should().Match(...)` on the real stream. The only sanctioned fixed waits are `.Should().NotEmit(within)` ("confirm nothing happens") and forcing distinct sort timestamps.

### ❌ Mocking core services
```csharp
var mock = new Mock<IMeshService>();          // mock says writes succeed; prod is broken
```
Use the real service via the test base. If it's "too slow", the contract is wrong — fix the contract.

### ❌ Redundant init pings before a layout-area read
A `client.Observe(new PingRequest()).Should().Within(<big>).Emit()` placed right before a `GetRemoteStream(addr)` read is usually pure redundancy — the **stream subscription self-activates the hub** and triggers the same cold compile. Drop the ping; give the follow-up read a cold-compile-tolerant `.Within(60.Seconds())`.

**But some pings are load-bearing — keep these:** pings before a `GetDataRequest` (content-collection data source), pings that trigger a transient-node auto-confirm (e.g. a `TransientTodo` Edit flow), and pings that *are* the test (PingPong, a `GetHostedHub(..., Never)` existence check). Todo-*instance* hubs do **not** self-activate from a layout-area subscription the way project-level hubs do, so their pings stay. When unsure: remove it, run the test; if it times out, restore it and note why.

## Hot vs. replayed signals — `ReplaySubject` when the producer can fire first

A plain `Subject<T>` is **hot**: emissions made before a subscriber attaches are lost. If a handler can `OnNext` before the test's blocking assertion subscribes, use a `ReplaySubject<T>(1)` so the later subscribe still sees it:

```csharp
var seen = new ReplaySubject<string?>(1);
stream.Update(_ => { seen.OnNext(accessService.Context?.ObjectId); return null; }, _ => { });
seen.Should().Within(5.Seconds()).Match(id => id == "alice");
```

Or invert: subscribe (start the blocking assertion on another thread) first, fire the producer second. Either reads fine; pick the clearer one.

## Orleans tests — clients must be mesh nodes

> **A client that posts mesh requests must itself be a registered MeshNode**, or routing won't recognise it as a participant — responses targeted back at the client address can't route, type-registry lookups for its deliveries are missing, and assertions time out with no clear cause.

When you build an Orleans test client, register its address as a MeshNode on the silo and register the data-layer types it sends/receives:

```csharp
hostBuilder.AddMeshNodes(new MeshNode("client", "delegation")
    { Name = "Test Client", NodeType = "User" });

config.TypeRegistry.AddAITypes();
config.TypeRegistry.WithType(typeof(MeshNodeReference), nameof(MeshNodeReference));
return config.AddLayoutClient();   // GetDataRequest/Response + sub/unsub
```

The shared `OrleansTestBase` exposes a synchronous `GetClient(...)` that wires this up — there is **no** async client-acquisition; the test calls `GetClient()` directly. Symptom of getting the registration wrong: `client.Observe(GetDataRequest(...)).Should().Emit()` never emits and the assertion times out.

## CI-only failure ≠ flake — it's a real timing or shared-state bug

When a test fails on CI but passes locally, **don't label it a flake and skip it.** Every CI-only failure investigated in this repo has been a real bug: an eventually-consistent index read too eagerly; a hot `Subject` that should have been a `ReplaySubject`; an `AccessContext` lost across the post-pipeline boundary; an init ping removed from a hub that doesn't self-activate. Skipping hides the bug; running it on CI surfaced it. Fix the bug. (Re-running a hung test "to see" hides the race — see [Debugging Message Flow](DebuggingMessageFlow) for the trace tags to grep instead.)

## Comprehensive coverage

[`Coder.md`](xref:Agent/Coder) sets the bar for NodeTypes and data models: **a test per invariant, per branch, per boundary, per degenerate input** — plus a serialization round-trip — not "at least one test per feature". A NodeType with a single happy-path test is demoed, not tested.

## Running tests

Always `run_in_background: true` — tests take minutes.

```bash
dotnet test test/MeshWeaver.NodeOperations.Test --no-build --no-restore
dotnet test test/MeshWeaver.Acme.Test --no-build --no-restore --filter "FullyQualifiedName~TodoDataChangeWorkflowTest"
```

The xUnit v3 adapter matches `FullyQualifiedName~`, **not** `ClassName~`. Don't use `--verbosity minimal` when a failure is possible — it hides stack traces. **Run → read → fix → run once more**; don't rerun a hung test 2–3 times "to see" — grep the `MESSAGE_FLOW:` trace instead.

## Test-project layout

- Framework code in `src/`, tests in `test/`, one `*.Test` project per production project where it makes sense.
- Shared fixtures in `src/MeshWeaver.Hosting.Monolith.TestBase` / the Orleans test base.
- NodeType tests live in the NodeType's `Test/` folder (`Namespace/Type/Test/TypeTest.cs`).

## References

- [Reactive Test Assertions](ReactiveTestAssertions) — assertion API, §2a deadlock rule, the legitimately-async catalogue
- [Asynchronous Calls](AsynchronousCalls) — why hub-reachable code is `IObservable<T>`, never `Task<T>`
- [CQRS — Queries vs. Content Access](CqrsAndContentAccess) — why `ReadNode` is the right read
- [Debugging Message Flow](DebuggingMessageFlow) — reading the framework's own trace when a test hangs
- `src/MeshWeaver.Hosting.Monolith.TestBase/MonolithMeshTestBase.cs` — the base every monolith test inherits
