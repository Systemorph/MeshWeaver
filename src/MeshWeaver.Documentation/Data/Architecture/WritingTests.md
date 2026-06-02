---
Name: Writing Tests in MeshWeaver
Category: Architecture
Description: How to write correct, non-flaky tests against a reactive distributed mesh. Test bodies are synchronous and assert on observables directly; covers the CQRS-correct read pattern, the deadlock-safe reactive assertion surface, and the canonical test-base setup.
Icon: Beaker
---

# Writing Tests in MeshWeaver

MeshWeaver is reactive end-to-end — and its tests are too. A well-written test method is **`void`**, contains **no `async` / `await` / `.ToTask()` / `.FirstAsync()`**, and asserts on `IObservable<T>` directly through the `MeshWeaver.Reactive.Assertions` surface. The blocking wait lives *inside* the assertion, never in the test body.

This isn't just a style convention. A test that reads the mesh the wrong way doesn't fail honestly: it either returns stale content due to CQRS lag, or it mixes a blocking assertion with an `await` and **deadlocks the hub pump** until the inactivity guard aborts the CI run. The rules below were each learned from a real incident in this codebase.

Before writing a test, review the invariants every test must respect:

| Document | What it covers |
|---|---|
| [Asynchronous Calls](AsynchronousCalls) | Why hub-reachable code is `IObservable<T>`, never `Task<T>` |
| [Reactive Test Assertions](ReactiveTestAssertions) | Full assertion API, the §2a deadlock rule, legitimately-async cases |
| [CQRS — Queries vs. Content Access](CqrsAndContentAccess) | Why a query is the wrong read immediately after a write |
| [Data Binding](xref:GUI/DataBinding) | Layout areas declare, views subscribe — tests assert against the subscription path |
| [Test State Isolation](TestStateIsolation) | Required when tests share a cluster fixture or `ICollectionFixture<>` |

<svg viewBox="0 0 760 260" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="#90a4ae"/>
    </marker>
    <marker id="arr-green" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="#43a047"/>
    </marker>
    <marker id="arr-red" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="#e53935"/>
    </marker>
  </defs>
  <text x="190" y="22" font-family="sans-serif" font-size="11" font-weight="bold" fill="currentColor" fill-opacity="0.55" text-anchor="middle">CORRECT — reactive stream read</text>
  <rect x="10" y="34" width="120" height="40" rx="10" fill="#1e88e5"/>
  <text x="70" y="58" font-family="sans-serif" font-size="12" fill="#fff" text-anchor="middle" font-weight="bold">Write / Mutate</text>
  <text x="70" y="72" font-family="sans-serif" font-size="10" fill="#bbdefb" text-anchor="middle">.Should().Emit()</text>
  <line x1="130" y1="54" x2="158" y2="54" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="160" y="34" width="130" height="40" rx="10" fill="#5c6bc0"/>
  <text x="225" y="52" font-family="sans-serif" font-size="12" fill="#fff" text-anchor="middle" font-weight="bold">Owner-Hub Stream</text>
  <text x="225" y="67" font-family="sans-serif" font-size="10" fill="#c5cae9" text-anchor="middle">GetMeshNodeStream(path)</text>
  <line x1="290" y1="54" x2="318" y2="54" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="320" y="34" width="130" height="40" rx="10" fill="#43a047"/>
  <text x="385" y="52" font-family="sans-serif" font-size="12" fill="#fff" text-anchor="middle" font-weight="bold">Reactive Assert</text>
  <text x="385" y="67" font-family="sans-serif" font-size="10" fill="#c8e6c9" text-anchor="middle">.Should().Match(pred)</text>
  <line x1="450" y1="54" x2="478" y2="54" stroke="#43a047" stroke-width="1.5" marker-end="url(#arr-green)"/>
  <rect x="480" y="34" width="110" height="40" rx="10" fill="#26a69a"/>
  <text x="535" y="52" font-family="sans-serif" font-size="12" fill="#fff" text-anchor="middle" font-weight="bold">Test Passes</text>
  <text x="535" y="67" font-family="sans-serif" font-size="10" fill="#b2dfdb" text-anchor="middle">authoritative, live</text>
  <line x1="70" y1="74" x2="70" y2="110" stroke="currentColor" stroke-opacity="0.2" stroke-width="1" stroke-dasharray="4,3"/>
  <line x1="595" y1="74" x2="595" y2="110" stroke="currentColor" stroke-opacity="0.2" stroke-width="1" stroke-dasharray="4,3"/>
  <line x1="70" y1="110" x2="595" y2="110" stroke="currentColor" stroke-opacity="0.2" stroke-width="1" stroke-dasharray="4,3"/>
  <text x="190" y="142" font-family="sans-serif" font-size="11" font-weight="bold" fill="currentColor" fill-opacity="0.55" text-anchor="middle">WRONG — lagged query read</text>
  <rect x="10" y="154" width="120" height="40" rx="10" fill="#1e88e5"/>
  <text x="70" y="178" font-family="sans-serif" font-size="12" fill="#fff" text-anchor="middle" font-weight="bold">Write / Mutate</text>
  <text x="70" y="192" font-family="sans-serif" font-size="10" fill="#bbdefb" text-anchor="middle">.Should().Emit()</text>
  <line x1="130" y1="174" x2="158" y2="174" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="160" y="154" width="130" height="40" rx="10" fill="#b71c1c"/>
  <text x="225" y="172" font-family="sans-serif" font-size="12" fill="#fff" text-anchor="middle" font-weight="bold">QueryAsync / Index</text>
  <text x="225" y="187" font-family="sans-serif" font-size="10" fill="#ffcdd2" text-anchor="middle">eventually consistent</text>
  <line x1="290" y1="174" x2="318" y2="174" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="320" y="154" width="130" height="40" rx="10" fill="#e53935"/>
  <text x="385" y="172" font-family="sans-serif" font-size="12" fill="#fff" text-anchor="middle" font-weight="bold">Stale Emission</text>
  <text x="385" y="187" font-family="sans-serif" font-size="10" fill="#ffcdd2" text-anchor="middle">old value from index</text>
  <line x1="450" y1="174" x2="478" y2="174" stroke="#e53935" stroke-width="1.5" marker-end="url(#arr-red)"/>
  <rect x="480" y="154" width="110" height="40" rx="10" fill="#e53935"/>
  <text x="535" y="172" font-family="sans-serif" font-size="12" fill="#fff" text-anchor="middle" font-weight="bold">Flaky / False</text>
  <text x="535" y="187" font-family="sans-serif" font-size="10" fill="#ffcdd2" text-anchor="middle">test lies or races</text>
  <text x="380" y="240" font-family="sans-serif" font-size="11" fill="currentColor" fill-opacity="0.5" text-anchor="middle">Cold observable: the write and assertion both execute on .Should().Emit() / .Match() — never async/await</text>
</svg>

*Reactive test flow: writes subscribe via `.Should().Emit()`, reads assert on the authoritative owner-hub stream, never on the lagged query index.*

---

## The Golden Rules

> **Rule 1 — Test bodies are `void` and reactive.**
> Assert on the observable: `obs.Should().Within(10.Seconds()).Match(x => predicate)`. The assertion subscribes, blocks up to the timeout, and returns the matched emission. No `async Task`, no `await`, no `.FirstAsync().ToTask()` anywhere in the body.

> **Rule 2 — A blocking `.Should()` and an `await` cannot coexist in the same method.**
> xUnit runs an `async Task` test under a SynchronizationContext; a blocking reactive assertion holds that thread while the hub tries to deliver the emission on it — deadlock. The moment a method uses `.Should().Emit()/.Match()/.Be()`, it must be `void` with every `await` removed. (See [Reactive Test Assertions §2](ReactiveTestAssertions) for the small set of methods that legitimately stay `async` — and why they must *not* use a blocking assertion.)

> **Rule 3 — Reads after writes use a stream, never a query.**
> A query goes through the lagged read-side index and returns stale content immediately after a write. Read a known node with `ReadNode(path).Should().Emit()` (from the test base), or `workspace.GetMeshNodeStream(path)` / `workspace.GetRemoteStream<MeshNode, MeshNodeReference>(addr, new MeshNodeReference())`.

> **Rule 4 — Queries are only for sets and existence.**
> Listing children, counting matches, "namespace is empty" — all legitimate uses of `ObserveQuery`. Reading a specific node's *content* is not.

> **Rule 5 — No mocking of core services.**
> Never mock `IMessageHub`, `IMeshService`, or `IMeshStorage`. Inherit `MonolithMeshTestBase` or `OrleansTestBase` and run the real services. A mock that passes while production is broken is worse than no test.

> **Rule 6 — Let failures propagate.**
> Timeouts, cancellations, and delivery failures are real test failures — the reactive assertion surface exposes them for you. Never wrap a read in `try { … } catch { return null; }`; that silently turns a flaky bug into a green-but-lying test. To assert an *expected* error, use `.Materialize()` (see below) rather than a swallowing `catch`.

---

## The Canonical Test Base

Every monolith test inherits `MonolithMeshTestBase`. The shape is always the same:

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

`MonolithMeshTestBase` provides:

- **Authentication** — logs in an admin user (`TestUsers.DevLogin`) so tests start authenticated.
- **`NodeFactory` / `MeshQuery`** — both backed by `IMeshService`.
- **`ReadNode(path)`** — the CQRS-correct single-node read, returning `IObservable<MeshNode?>`.
- **Clean teardown** — the mesh is disposed after every test.

`OrleansTestBase` offers the same shape for distributed tests on an Orleans TestCluster.

> **Cold observables: `.Should()` is the subscribe.**
> `NodeFactory.CreateNode(...)`, `UpdateNode`, `DeleteNode`, and `hub.Observe(...)` are **cold** — the side effect (the write, the request dispatch) runs on subscribe, not on call. The blocking `.Should().Emit()` subscribes, performs the work, and waits for it to land. A bare `NodeFactory.CreateNode(node);` with no `.Should()` / `.Subscribe()` does nothing at all.

---

## `ReadNode` — the Authoritative Single-Node Read

```csharp
// Delegates to the IMeshService-backed owner-hub read, with no catalog/index lag:
var node = ReadNode(path).Should().Within(ReadNodeTimeout).Emit();
```

This reads the owning per-node hub's reducer directly — no stale content, no index lag after a write. The emission semantics are:

| Situation | Emission |
|---|---|
| Node exists | emits the `MeshNode` |
| Node does not exist (routing says `NotFound`) | emits `null` |
| Timeout or delivery failure | stream errors → `.Should().Emit()` fails the test with the underlying message |

```csharp
// ✅ "expect found" after create or update
ReadNode(orgId).Should().Match(n => n is { Name: "Renamed" });

// ✅ "expect not found" after delete — the NotFound null surfaces naturally
NodeFactory.DeleteNode(orgId).Should().Emit();
ReadNode(orgId).Should().Match(n => n is null);
```

---

## The Reactive Assertion Surface

From `MeshWeaver.Reactive.Assertions` (globally imported in every test project). Full reference: [Reactive Test Assertions](ReactiveTestAssertions).

| Call | Meaning |
|---|---|
| `obs.Should().Emit()` | Block ≤ timeout for the first emission; **return it** |
| `obs.Should().Match(x => pred)` | Block for the first emission satisfying `pred`; return it |
| `obs.Should().Be(expected)` | First emission equals `expected` |
| `obs.Should().Complete()` | Stream completes within the timeout |
| `obs.Should().NotEmit(within: 200.Milliseconds())` | Nothing arrives — the **one** place a fixed wait is correct |
| `obs.Should().Within(t)....` | Override the default 10 s timeout for this chain |

`.Emit()` and `.Match()` **return** the matched value, replacing the `var x = await obs....ToTask()` pattern one-for-one: `var x = obs.Should().Within(t).Match(...)`.

**Fold the wait into the predicate.** Don't grab the first emission and hope it's the right one — describe the state you are waiting for:

```csharp
// ❌ grabs whatever lands first — stale or partial on cold-start CI
var msgs = stream.Should().Emit();
msgs.Count.Should().Be(2);

// ✅ waits for the emission where the invariant actually holds
var msgs = stream.Should().Within(45.Seconds()).Match(m => m.Count == 2);
```

---

## Asserting an Expected Error — `.Materialize()`, not `ThrowAsync`

A reactive `.Should().Emit()/.Match()` wraps an `OnError` in an assertion exception — it does **not** rethrow the original type — so `Action.Should().Throw<T>()` won't catch the original. To assert that a stream errors with a specific type, materialize the `OnError` into a value:

```csharp
var error = cache.GetStream(missingPath)
    .Where(n => n?.Content is not null)
    .Take(1)
    .Materialize()
    .Should().Within(5.Seconds()).Match(n => n.Kind == NotificationKind.OnError);
error.Exception.Should().BeOfType<DeliveryFailureException>();
```

`using System.Reactive;` provides `NotificationKind`. This replaces the old `await act.Should().ThrowAsync<T>()` shape.

---

## Waiting for State to Change Over Time

`GetMeshNodeStream` and `GetRemoteStream` are **live** — they replay current state and keep emitting. Submit first, then assert on the live stream; the assertion catches the settled state whenever it arrives:

```csharp
client.SubmitMessage(threadPath, "Hi", contextPath: "TestUser");

var idle = workspace.GetMeshNodeStream(threadPath)
    .Select(node => node?.Content as MeshThread)
    .Should().Within(45.Seconds()).Match(t => t is { Status: ThreadExecutionStatus.Idle });
```

When the source is **request/response with no stream surface** (a `GetDataRequest`, a query snapshot), poll reactively — the interval sets the cadence, `.Match` defines the condition, and `.Within` is the hard deadline:

```csharp
var match = Observable.Interval(50.Milliseconds()).StartWith(0L)
    .SelectMany(_ => meshService.QueryAsync<MeshNode>("nodeType:Story").ToObservable().ToList())
    .Should().Within(15.Seconds()).Match(list => list.Count >= 3);
```

For a synced query, prefer `MeshService.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(q))` and filter on `c.ChangeType == QueryChangeType.Initial` — its first emission is the full snapshot that the old `QueryAsync().ToListAsync()` used to return.

---

## What NOT to Do

### ❌ A query to read a just-written node

```csharp
NodeFactory.UpdateNode(updated).Should().Emit();
var found = meshService.QueryAsync<MeshNode>($"path:{orgId}").ToObservable()
    .Should().Emit();                       // flaky: index may still hold "Original"
```

The read-side index is eventually consistent. Use `ReadNode(orgId)`.

---

### ❌ Asserting "exactly N change events"

A change feed (pg_notify, any synced query) can deliver follow-up events for a row that already existed when the subscription wired up. Filter on the emission **shape**, not the count:

```csharp
var initial = meshService.ObserveQuery<MeshNode>(req)
    .Should().Within(10.Seconds()).Match(c => c.ChangeType == QueryChangeType.Initial);
initial.Items.Should().HaveCount(1);
```

---

### ❌ `Task.Delay` / `Thread.Sleep` to "wait for propagation"

Fold the wait into `.Should().Match(...)` on the real stream. The only sanctioned fixed waits are `.Should().NotEmit(within)` ("confirm nothing happens") and forcing distinct sort timestamps in ordering tests.

---

### ❌ Mocking core services

```csharp
var mock = new Mock<IMeshService>();          // mock says writes succeed; prod is broken
```

Use the real service via the test base. If it feels "too slow", the contract is wrong — fix the contract.

---

### ❌ Redundant init pings before a layout-area read

A `client.Observe(new PingRequest()).Should().Within(<big>).Emit()` placed immediately before a `GetRemoteStream(addr)` read is usually pure redundancy — the **stream subscription self-activates the hub** and triggers the same cold compile. Drop the ping and give the follow-up read a cold-compile-tolerant `.Within(60.Seconds())`.

**Some pings are load-bearing — keep these:**

- Pings before a `GetDataRequest` (content-collection data source).
- Pings that trigger a transient-node auto-confirm (e.g. a `TransientTodo` edit flow).
- Pings that *are* the test (PingPong, a `GetHostedHub(..., Never)` existence check).

Todo-*instance* hubs do **not** self-activate from a layout-area subscription the way project-level hubs do, so their pings stay. When uncertain: remove it, run the test; if it times out, restore it and document why.

---

## Hot vs. Replayed Signals — `ReplaySubject` When the Producer Can Fire First

A plain `Subject<T>` is **hot**: emissions made before a subscriber attaches are lost. If a handler can call `OnNext` before the test's blocking assertion subscribes, use a `ReplaySubject<T>(1)` so the late subscriber still sees it:

```csharp
var seen = new ReplaySubject<string?>(1);
stream.Update(_ => { seen.OnNext(accessService.Context?.ObjectId); return null; }, _ => { });
seen.Should().Within(5.Seconds()).Match(id => id == "alice");
```

Alternatively, subscribe (start the blocking assertion on a background thread) first, then fire the producer second. Either works; pick whichever reads more clearly.

---

## Orleans Tests — Clients Must Be Mesh Nodes

> **A client that posts mesh requests must itself be a registered MeshNode.** Without registration, routing cannot recognise it as a participant — responses targeted back at the client address cannot route, type-registry lookups for its deliveries are missing, and assertions time out with no clear cause.

When building an Orleans test client, register its address as a MeshNode on the silo and register the data-layer types it sends and receives:

```csharp
hostBuilder.AddMeshNodes(new MeshNode("client", "delegation")
    { Name = "Test Client", NodeType = "User" });

config.TypeRegistry.AddAITypes();
config.TypeRegistry.WithType(typeof(MeshNodeReference), nameof(MeshNodeReference));
return config.AddLayoutClient();   // GetDataRequest/Response + sub/unsub
```

The shared `OrleansTestBase` exposes a synchronous `GetClient(...)` that wires this up — there is **no** async client-acquisition; the test calls `GetClient()` directly. Symptom of a missing registration: `client.Observe(GetDataRequest(...)).Should().Emit()` never emits and the assertion times out.

---

## CI-Only Failure ≠ Flake — It's a Real Bug

When a test fails on CI but passes locally, **don't label it a flake and skip it.** Every CI-only failure investigated in this repo traced to a real bug: an eventually-consistent index read too eagerly; a hot `Subject` that should have been a `ReplaySubject`; an `AccessContext` lost across the post-pipeline boundary; an init ping removed from a hub that doesn't self-activate. Skipping hides the bug; running it on CI is exactly what surfaced it.

Fix the bug. Re-running a hung test "to see if it was a flake" hides the race — see [Debugging Message Flow](DebuggingMessageFlow) for the trace tags to grep instead.

---

## Coverage Expectations

[`Coder.md`](xref:Agent/Coder) sets the bar for NodeTypes and data models: **a test per invariant, per branch, per boundary, per degenerate input** — plus a serialization round-trip. A NodeType with a single happy-path test is demoed, not tested.

---

## Running Tests

Always run tests in the background — they take minutes.

```bash
dotnet test test/MeshWeaver.NodeOperations.Test --no-build --no-restore
dotnet test test/MeshWeaver.Acme.Test --no-build --no-restore --filter "FullyQualifiedName~TodoDataChangeWorkflowTest"
```

The xUnit v3 adapter matches `FullyQualifiedName~`, **not** `ClassName~`. Never use `--verbosity minimal` when a failure is possible — it hides stack traces.

**Workflow: run → read → fix → run once more.** Do not re-run a hung test two or three times "to see what happens" — grep the `MESSAGE_FLOW:` trace in [Debugging Message Flow](DebuggingMessageFlow) instead.

---

## Test Project Layout

- Framework code lives in `src/`, tests in `test/`, with one `*.Test` project per production project where it makes sense.
- Shared fixtures live in `src/MeshWeaver.Hosting.Monolith.TestBase` and the Orleans test base.
- NodeType tests live in the NodeType's `Test/` folder (`Namespace/Type/Test/TypeTest.cs`).

---

## References

- [Reactive Test Assertions](ReactiveTestAssertions) — assertion API, §2a deadlock rule, legitimately-async catalogue
- [Asynchronous Calls](AsynchronousCalls) — why hub-reachable code is `IObservable<T>`, never `Task<T>`
- [CQRS — Queries vs. Content Access](CqrsAndContentAccess) — why `ReadNode` is the right read after a write
- [Debugging Message Flow](DebuggingMessageFlow) — reading the framework's own trace when a test hangs
- `src/MeshWeaver.Hosting.Monolith.TestBase/MonolithMeshTestBase.cs` — the base every monolith test inherits
