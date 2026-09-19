---
Name: Writing Tests in MeshWeaver
Category: Architecture
Description: How to write correct, non-flaky tests against a reactive distributed mesh. Test bodies await assertions made directly on observables; covers the CQRS-correct read pattern, the reactive assertion surface, and the canonical test-base setup.
Icon: Beaker
---

# Writing Tests in MeshWeaver

MeshWeaver is reactive end-to-end — and its tests are too. A well-written test method is **`async Task`**, contains **no `.FirstAsync().ToTask()` / `.Result` / `.Wait()` / `Task.Delay`** — none at all, anywhere — and asserts on `IObservable<T>` directly through the `MeshWeaver.Reactive.Assertions` surface. Each terminal assertion (`Emit` / `Match` / `Be` / `Complete` / `NotEmit`) **returns a `Task` you `await`** — the wait lives *inside* the assertion, never in the test body, and it is **not** Rx's `.ToTask()` bridge: the assertion settles its own `TaskCompletionSource` with `RunContinuationsAsynchronously`, so the test never resumes on the mesh thread that signalled.

> **🚨 `.ToTask()` is forbidden here too** (maintainer ruling, 2026-08-30: *"no totask ever"*; the old "tests are the one sanctioned place" carve-out is RETRACTED). Rx's bridge resumes the awaiter inline on the signalling thread, which in a test is the thread that then runs the rest of the test, its mesh teardown, and — under xUnit — the runner starting the next class. `await source.FirstAsync()` is the same defect in fewer characters: Rx's awaiter is an `AsyncSubject<T>` that completes its continuation from inside `OnCompleted`. Assert through `.Should()`; where an `async Task` signature genuinely must take a value out of a stream, use `MeshWeaver.Messaging.ReactiveCompletion.ObserveCompletion(reportLateFault, ct)`.

> **🚨 `await` the assertion — it is not a blocking call.** `Emit()` and `Match()` return `Task<T>`; `Be()` / `Complete()` / `NotEmit()` return `Task<ObservableAssertions<T>>`. Dropping the `await` on a statement such as `NodeFactory.CreateNode(node).Should().Emit();` compiles with **no warning** and the test races on ahead without ever waiting for the write — the single easiest way to write a green-but-lying test here. (This reverses an earlier design in which the assertions blocked and test bodies were `void`; see `ObservableAssertions.cs`, which is explicit that it is *"never a thread-blocking `ManualResetEventSlim` + `Wait`"*.)

This isn't just a style convention. A test that reads the mesh the wrong way doesn't fail honestly: it returns stale content due to CQRS lag, or it never actually waits and passes on a race. The rules below were each learned from a real incident in this codebase.

Before writing a test, review the invariants every test must respect:

| Document | What it covers |
|---|---|
| [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls) | Why hub-reachable code is `IObservable<T>`, never `Task<T>` |
| [Reactive Test Assertions](/Doc/Architecture/ReactiveTestAssertions) | Full assertion API, why the assertion subscribes on the thread pool, genuinely-async cases |
| [CQRS — Queries vs. Content Access](/Doc/Architecture/CqrsAndContentAccess) | Why a query is the wrong read immediately after a write |
| [Data Binding](/Doc/GUI/DataBinding) | Layout areas declare, views subscribe — tests assert against the subscription path |
| [Test State Isolation](/Doc/Architecture/TestStateIsolation) | Required when tests share a cluster fixture or `ICollectionFixture<>` |

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
  <text x="380" y="240" font-family="sans-serif" font-size="11" fill="currentColor" fill-opacity="0.5" text-anchor="middle">Cold observable: the write and assertion both execute on await .Should().Emit() / .Match()</text>
</svg>

*Reactive test flow: writes subscribe via `await ….Should().Emit()`, reads assert on the authoritative owner-hub stream, never on the lagged query index.*

---

## The Golden Rules

> **Rule 1 — Test bodies are `async Task` and reactive.**
> Assert on the observable: `await obs.Should().Within(10.Seconds()).Match(x => predicate)`. The assertion subscribes (off xUnit's sync context, so the mesh's continuations land on the pool), waits up to the timeout, and returns the matched emission. No `.FirstAsync().ToTask()` — the bridge is forbidden outright, not just "hand-rolled" — no `.Result` / `.Wait()`, no bare `await someObservable`, no `Task.Delay` anywhere in the body.

> **Rule 2 — Every terminal assertion must be `await`ed.**
> `.Emit()` / `.Match()` / `.Be()` / `.Complete()` / `.NotEmit()` return a `Task`. An un-awaited one is a fire-and-forget that the compiler will not flag in most positions, so the test proceeds before the write lands. `.Within(t)` and `.Should(t)` are the only synchronous links in the chain — they just configure the deadline. (See [Reactive Test Assertions §2](/Doc/Architecture/ReactiveTestAssertions) for the mechanics, including why the assertion subscribes on `TaskPoolScheduler` rather than xUnit's single-threaded sync context.)

> **Rule 2a — A test with a `Timeout` hands `TestContext.Current.CancellationToken` to the wait it parks in.**
> xunit.v3 4.x cancels that token when the timeout fires (and when the run is stopped), and its analyzer makes a `Timeout` that never consumes it a build error (`xUnit1069` — under `-warnaserror`, a red build; it is NOT in `NoWarn` and must not go there). A wait that ignores the token outlives the verdict: the failure is recorded, the body stays parked, and the runner carries the blocked body into the next test. So every terminal wait takes the token — `.Await(TestContext.Current.CancellationToken)`, and the trailing `cancellationToken` parameter on `.Emit(…)` / `.Match(…)` / `.Be(…)` / `.Complete(…)` / `.NotEmit(…)` (`Emit(cancellationToken: TestContext.Current.CancellationToken)` when there is no `because`). A private helper the test awaits either takes a `CancellationToken cancellationToken` and hands it to ITS wait, or reads `TestContext.Current.CancellationToken` at the wait itself — both reach the SAME token (`TestContext.Current` is the running test's, flowed by `AsyncLocal`), and the second is the documented default of this repository's own test-base primitives (`MonolithMeshTestBase.AwaitResponseAsync`: *"Default cancellation = `TestContext.Current`'s `CancellationToken` — never pass `default`"*). Prefer the parameter when the helper already has one or a caller must be able to override it; prefer the direct read when threading a parameter would fan out through call sites that add nothing. What the analyzer needs is different and narrower: the reference must be lexically inside the TEST method, so the test's own first wait — never a helper — is what satisfies `xUnit1069`. A cancelled wait surfaces as `OperationCanceledException` — it is never folded into "did not emit", and `NotEmit` never reads it as a pass. A synchronous test with a `Timeout` has nothing to hand the token to; it observes it at its first statement (`TestContext.Current.CancellationToken.ThrowIfCancellationRequested();`), which is the analyzer's documented form for that shape and is what a stopped run sees.
>
> 🚨 **Satisfying the analyzer is NOT the same as the body stopping, and the difference is the whole point of the rule.** `xUnit1069` is satisfied by ONE reference anywhere in the test method — so a test that hands the token to its first wait and then parks in a helper that polls on a bare `Task.Delay(300)` is green to the analyzer and still outlives its verdict. When you give a test the token, follow it to *every* wait the body can be sitting in: the helper's `Task.Delay`, its `.Await()`, its `WaitAsync(timeout)`, its `.Should()…Emit(…)`.
>
> **The helper-level population is swept, and its residue is named rather than implied** (#4378). Measured over `test/` on 2026-09-15 by scanning every `.Await()`, `Task.Delay`, `WaitAsync`, `ReactiveWait.First`, `ObserveCompletion` and `.Should()…Emit/Match/Complete/NotEmit` call and attributing it to the member that lexically owns it: **2,847 wait sites**, of which **194 in 110 files sat in a helper and took no token**. 185 of those now take one; the **9** that remain are deliberate, and each says so in a comment where it sits:
>
> | left untokened | why |
> |---|---|
> | `PortalFixture.WaitForPortalAsync` | fixture construction — it runs before any test owns a token |
> | `CollectibleUnloadDrain.WaitUntilCollectedAsync` | a teardown drain: teardown lets work finish, it is never forced |
> | `QuiescingHubRefusesNewWorkTest.ReleaseAndDispose` | releases the held victim so the fixture can tear down cleanly |
> | `RxFanOutInversionHarness.BothGatedHandlersComplete` | the `Task.Delay` **is** the deadlock bound; a cancelled bound would read as "deadlock detected" |
> | `PodHubTransportTest.Settled` | the same shape, in the direction that is worse: `Settled` is consumed as `(await Settled(…)).Should().BeFalse(…)`, so a cancelled delay wins the `WhenAny` race and a timed-out test **passes** a negative assertion it never finished |
> | `OrleansIdleReactivationNoWedgeTest.CreateNode` | the `.WaitAsync(45s)` wraps an `.Await(token)` that already cancels; a second token there is noise, not cover |
> | `MeshTestRunnerTests`' `Sample` cases | they ARE the in-mesh runner's subject, including one that parks on `Task.Delay(Timeout.InfiniteTimeSpan)` so the runner's own timeout can be observed |
>
> Plus the two that Rule 2a already named and that live in test bodies rather than helpers: a wait the test PARKS on purpose (`IoPoolTest`'s in-flight leaf, a backlog of deferred requests).
>
> 🚨 **What is NOT swept, stated so nobody reads the above as "every wait now stops": 1,292 wait sites inside `[Fact]`/`[Theory]`/`[HubFact]` bodies still take no token.** They are analyzer-compliant, because the method references the token somewhere else, and each is bounded by its own `.Timeout(…)` or `Within(…)`, so none can hang a run — but a test with several waits can still be parked in its second or fifth one when the verdict is written. That is the same defect at a larger population and wants the same treatment; it is not fixed here.

> **Rule 3 — Reads after writes use a stream, never a query.**
> A query goes through the lagged read-side index and returns stale content immediately after a write. Read a known node with `await ReadNode(path).Should().Emit()` (from the test base), or `workspace.GetMeshNodeStream(path)`.

> **Rule 4 — Queries are only for sets and existence.**
> Listing children, counting matches, "namespace is empty" — all legitimate uses of `Query`. Reading a specific node's *content* is not.

> **Rule 5 — No mocking of core services.**
> Never mock `IMessageHub`, `IMeshService`, or `IMeshStorage`. Inherit `MonolithMeshTestBase` or `OrleansMeshTestBase` and run the real services. A mock that passes while production is broken is worse than no test.

> **Rule 6 — Let failures propagate.**
> Timeouts, cancellations, and delivery failures are real test failures — the reactive assertion surface exposes them for you. Never wrap a read in `try { … } catch { return null; }`; that silently turns a flaky bug into a green-but-lying test. To assert an *expected* error, use `.Materialize()` (see below) rather than a swallowing `catch`.

---

## In-mesh tests and the build process (node repos)

A NodeType in a node repo ships its tests as `<Type>/Test/*.cs` — **static classes whose public
static parameterless methods throw on failure** — and a `Tests` layout area that lists them. Since
2026-08-30 those run through the container the platform build produced, not through xUnit:

```
mw-plugin-test build <repo-root> [<package>... | all]
```

**Build means compile and run tests**, per package, as a dependency cascade: a package observes
the result streams of the packages it requires and starts itself when the last one is green; on
red the dependents are blocked by name, on green they continue; independent packages build in
parallel; every package reports its timings. Sources come from the checkout on disk and compile
against the image's `/app` plus the assemblies the dependency packages just emitted — no mesh
import, no `$(MeshWeaverRoot)` source checkout, no `MeshWeaver.Fixture`. Cases that need a host
are counted as `needs-mesh` and run by the gate, seeded from the build's output. See
`tools/MeshWeaver.PluginTester/README.md` (`build`).

`MeshWeaver.Fixture` and the two TestBase assemblies are this repo's OWN test support: they live
under `test/` and are never packed or published.

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
    public async Task UpdateNode_SurfacesNewName()                 // ← async Task; every assertion is awaited
    {
        var orgId = $"Org_{Guid.NewGuid():N}"[..12];
        await NodeFactory.CreateNode(new MeshNode(orgId)
            { Name = "Original", NodeType = "Markdown" }).Should().Emit();   // ← subscribe = do the write

        var updated = MeshNode.FromPath(orgId) with { Name = "Renamed", NodeType = "Markdown" };
        await NodeFactory.UpdateNode(updated).Should().Emit();

        // ✅ Authoritative owner-hub read — never lagged.
        var node = await ReadNode(orgId).Should().Emit();
        node!.Name.Should().Be("Renamed");
    }
}
```

`MonolithMeshTestBase` provides:

- **Authentication** — logs in an admin user via `TestUsers.DevLogin(Mesh)` (the identity is `TestUsers.Admin`) so tests start authenticated.
- **`NodeFactory` / `MeshQuery`** — both resolve `IMeshService` from the mesh's service provider.
- **`ReadNode(path)`** — the CQRS-correct single-node read, returning `IObservable<MeshNode?>`.
- **Clean teardown** — the mesh is disposed after every test.

`OrleansMeshTestBase` (in `test/MeshWeaver.Hosting.Orleans.TestBase`) offers the same shape for distributed tests on an Orleans TestCluster. It is the ONE Orleans base: which cluster a suite gets is a DECLARATION on the class (`protected override IMeshBootstrap Bootstrap => MeshBootstrap.Orleans(o => o.WithSilos(2));` and `SiloConfiguratorType`), not a choice of base class. The retired `OrleansTestBase<T>` / `OrleansSharedTestBase` survive only as a cross-repo bridge; do not derive from them.

> **Cold observables: `.Should()` is the subscribe.**
> `NodeFactory.CreateNode(...)`, `UpdateNode`, `DeleteNode`, and `hub.Observe(...)` are **cold** — the side effect (the write, the request dispatch) runs on subscribe, not on call. `await ….Should().Emit()` subscribes, performs the work, and waits for it to land. A bare `NodeFactory.CreateNode(node);` with no `.Should()` / `.Subscribe()` does nothing at all — and an **un-awaited** `.Should().Emit()` subscribes but does not wait, which is just as wrong.

---

## `ReadNode` — the Authoritative Single-Node Read

```csharp
// Delegates to the owner-hub read, with no catalog/index lag:
var node = await ReadNode(path).Should().Within(ReadNodeTimeout).Emit();
```

This reads the owning per-node hub's reducer directly — no stale content, no index lag after a write. The emission semantics are:

| Situation | Emission |
|---|---|
| Node exists | emits the `MeshNode` |
| Node does not exist (routing says `NotFound`) | emits `null` |
| Timeout or delivery failure | stream errors → `await ….Should().Emit()` fails the test with the underlying message |

```csharp
// ✅ "expect found" after create or update
await ReadNode(orgId).Should().Match(n => n is { Name: "Renamed" });

// ✅ "expect not found" after delete — the NotFound null surfaces naturally
await NodeFactory.DeleteNode(orgId).Should().Emit();
await ReadNode(orgId).Should().Match(n => n is null);
```

---

## The Reactive Assertion Surface

From `MeshWeaver.Reactive.Assertions` (globally imported in every test project). Full reference: [Reactive Test Assertions](/Doc/Architecture/ReactiveTestAssertions).

| Call | Meaning |
|---|---|
| `await obs.Should().Emit()` | Wait ≤ timeout for the first emission; **return it** |
| `await obs.Should().Match(x => pred)` | Wait for the first emission satisfying `pred`; return it |
| `await obs.Should().Be(expected)` | First emission equals `expected` |
| `await obs.Should().Complete()` | Stream completes within the timeout |
| `await obs.Should().NotEmit(within: 200.Milliseconds())` | Nothing arrives — the **one** place a fixed wait is correct |
| `obs.Should().Within(t)....` / `obs.Should(t)....` | Override the default 10 s timeout for this chain (synchronous — still `await` the terminal call) |

`.Emit()` and `.Match()` **return** the matched value, replacing the forbidden `var x = await obs.FirstAsync().ToTask()` pattern one-for-one: `var x = await obs.Should().Within(t).Match(...)`.

**Fold the wait into the predicate.** Don't grab the first emission and hope it's the right one — describe the state you are waiting for:

```csharp
// ❌ grabs whatever lands first — stale or partial on cold-start CI
var msgs = await stream.Should().Emit();
msgs.Count.Should().Be(2);

// ✅ waits for the emission where the invariant actually holds
var msgs = await stream.Should().Within(45.Seconds()).Match(m => m.Count == 2);
```

---

## Asserting an Expected Error — `.Materialize()`, not `ThrowAsync`

A reactive `.Should().Emit()/.Match()` wraps an `OnError` in an `ObservableAssertionException` — it does **not** rethrow the original type — so `Action.Should().Throw<T>()` won't catch the original. To assert that a stream errors with a specific type, materialize the `OnError` into a value:

```csharp
var error = await cache.GetStream(missingPath, options)
    .Where(n => n?.Content is not null)
    .Take(1)
    .Materialize()
    .Should().Within(5.Seconds()).Match(n => n.Kind == NotificationKind.OnError);
error.Exception.Should().BeOfType<DeliveryFailureException>();
```

`using System.Reactive;` provides `NotificationKind`. This is the reactive replacement for `await act.Should().ThrowAsync<T>()` on a *stream*; for a genuinely throwing synchronous or `Task`-returning call, `Throw<T>()` / `ThrowAsync<T>()` still apply.

---

## Waiting for State to Change Over Time

`GetMeshNodeStream` and `GetRemoteStream` are **live** — they replay current state and keep emitting. Submit first, then assert on the live stream; the assertion catches the settled state whenever it arrives:

```csharp
client.SubmitMessage(threadPath, "Hi", contextPath: "TestUser");

var idle = await workspace.GetMeshNodeStream(threadPath)
    .Select(node => node?.Content as MeshThread)
    .Should().Within(45.Seconds()).Match(t => t is { Status: ThreadExecutionStatus.Idle });
```

When the source is **request/response with no stream surface** (a `GetDataRequest`, a query snapshot), poll reactively — the interval sets the cadence, `.Match` defines the condition, and `.Within` is the hard deadline:

```csharp
var match = await Observable.Interval(50.Milliseconds()).StartWith(0L)
    .SelectMany(_ => meshService.QueryAsync<MeshNode>("nodeType:Story").ToObservable().ToList())
    .Should().Within(15.Seconds()).Match(list => list.Count >= 3);
```

For a synced query, prefer `MeshService.Query<MeshNode>(MeshQueryRequest.FromQuery(q))` and filter on `c.ChangeType == QueryChangeType.Initial` — its first emission is the full snapshot that the old `QueryAsync().ToListAsync()` used to return.

---

## What NOT to Do

### ❌ A query to read a just-written node

```csharp
await NodeFactory.UpdateNode(updated).Should().Emit();
var found = await meshService.QueryAsync<MeshNode>($"path:{orgId}").ToObservable()
    .Should().Emit();                       // flaky: index may still hold "Original"
```

The read-side index is eventually consistent. Use `ReadNode(orgId)`.

---

### ❌ Asserting "exactly N change events"

A change feed (pg_notify, any synced query) can deliver follow-up events for a row that already existed when the subscription wired up. Filter on the emission **shape**, not the count:

```csharp
var initial = await meshService.Query<MeshNode>(req)
    .Should().Within(10.Seconds()).Match(c => c.ChangeType == QueryChangeType.Initial);
initial.Items.Should().HaveCount(1);
```

---

### ❌ `Task.Delay` / `Thread.Sleep` to "wait for propagation"

Fold the wait into `await ….Should().Match(...)` on the real stream. The only sanctioned fixed waits are `await ….Should().NotEmit(within)` ("confirm nothing happens") and forcing distinct sort timestamps in ordering tests.

---

### ❌ A reachability assertion that collects before teardown has finished

```csharp
var weak = new WeakReference(hub);
owner.Dispose();
GC.Collect(); GC.WaitForPendingFinalizers();
weak.IsAlive.Should().BeFalse();              // ❌ green in the suite, red alone
```

🚨 **`Dispose()` only STARTS a hub's teardown.** It freezes hosted-hub creation, posts
`ShutdownRequest(Quiescing)` and returns — every phase after that is a fresh message on the action
block ([Hub Disposal Model](/Doc/Architecture/HubDisposalModel)). So the instant `Dispose()` returns,
the hub is still rooted by its own in-flight shutdown: its action block, its scheduler, its registry
entry. Collecting there measures the teardown's *speed*, not the reference graph.

**The failure mode is the dangerous one, because the test goes GREEN.** Measured on
`StreamReleasesItsHubTest` (#3321): the first version of that assertion **passed inside the
485-test suite** — where the surrounding tests happened to give the teardown time — and **failed the
moment it ran alone, on the same binary**. Suite-green / alone-red with nothing rebuilt is the
signature; if you see it, suspect an assertion that is waiting on wall-clock luck rather than on a
condition.

Join the completion signal first, then collect:

```csharp
// captured BEFORE Dispose, inside a non-inlined helper — DisposalCompleted wraps a subject FIELD,
// and a subject does not reference the object that owns it, so holding it cannot root the hub
await disposalCompleted
    .Catch<Unit, Exception>(_ => Observable.Return(Unit.Default))
    .FirstOrDefaultAsync()
    .Timeout(TestTimeouts.Convergence)
    .Await(TestContext.Current.CancellationToken);

GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
GC.WaitForPendingFinalizers();
weak.IsAlive.Should().BeFalse();              // ✅ after this, only a kept reference can hold it
```

That is not a bigger wait — it is a *different kind* of wait, and it is what turns the probe from a
sample into a proof. [Negative Controls](/Doc/Architecture/NegativeControls) is right that a
`WeakReference` whose truth depends on *when* it is evaluated pins nothing; gating it on
`DisposalCompleted` is precisely what removes the *when*.

Two details that decide whether the test can fail for the right reason:

- **Take the weak reference, and drop the strong one, inside a `[MethodImpl(NoInlining)]` helper.**
  A live local on the test's own frame roots the object and turns a red into a green.
- **Return the completion observable, never the object.** Returning the hub — or any closure over
  it — defeats the whole assertion.

---

### ❌ Mocking core services

```csharp
var mock = new Mock<IMeshService>();          // mock says writes succeed; prod is broken
```

Use the real service via the test base. If it feels "too slow", the contract is wrong — fix the contract.

---

### ❌ Redundant init pings before a layout-area read

An `await client.Observe(new PingRequest()).Should().Within(<big>).Emit()` placed immediately before a `GetRemoteStream(addr)` read is usually pure redundancy — the **stream subscription self-activates the hub** and triggers the same cold compile. Drop the ping and give the follow-up read a cold-compile-tolerant `.Within(60.Seconds())`.

**Some pings are load-bearing — keep these:**

- Pings before a `GetDataRequest` (content-collection data source).
- Pings that trigger a transient-node auto-confirm (e.g. a `TransientTodo` edit flow).
- Pings that *are* the test (PingPong, a `GetHostedHub(..., Never)` existence check).

Todo-*instance* hubs do **not** self-activate from a layout-area subscription the way project-level hubs do, so their pings stay. When uncertain: remove it, run the test; if it times out, restore it and document why.

---

## Hot vs. Replayed Signals — `ReplaySubject` When the Producer Can Fire First

A plain `Subject<T>` is **hot**: emissions made before a subscriber attaches are lost. If a handler can call `OnNext` before the test's assertion subscribes, use a `ReplaySubject<T>(1)` so the late subscriber still sees it:

```csharp
var seen = new ReplaySubject<string?>(1);
stream.Update(_ => { seen.OnNext(accessService.Context?.ObjectId); return null; }, _ => { });
await seen.Should().Within(5.Seconds()).Match(id => id == "alice");
```

Alternatively, start the assertion first without awaiting it (`var assertion = obs.Should().Match(...);`), fire the producer, then `await assertion`. Either works; pick whichever reads more clearly.

---

## An Overlap the Test ASSUMES Is an Overlap It Does Not Have

**When the assertion is about two things COEXISTING — two subscribers sharing one connection, a
leaf queued behind another, two writers contending — the coexistence is something the test must
ESTABLISH. Subscribing back to back and hoping they coincide is a race, and the direction it fails
in is the one that reads like a real defect.**

`DeckSlidesCacheTest.GetOrderedSlides_ConcurrentSubscribers_ShareOneQuerySubscription` asserts that
two overlapping subscribers share ONE underlying query. Both subscribers reduce with `FirstAsync()`,
so each lets go the instant it has its value:

```csharp
// ❌ the overlap is a hope. If the snapshot reaches the first subscriber before the next line
//    runs, Replay(1).RefCount() drops to zero subscribers, DISCONNECTS, and the second
//    subscriber reconnects — the count is 2 and the test is red for the one thing it does not test.
var first  = cache.GetOrderedSlides("DeckA").FirstAsync().Timeout(30.Seconds()).Await(ct);
var second = cache.GetOrderedSlides("DeckA").FirstAsync().Timeout(30.Seconds()).Await(ct);
```

```csharp
// ✅ the source cannot emit until both are attached. Await subscribes SYNCHRONOUSLY, so by the
//    time the gate opens the overlap is a fact. What is asserted is unchanged.
var bothAttached = new AsyncSubject<Unit>();
var mesh = MakeCountingMesh(bothAttached);          // gates the SUBSCRIPTION, not the values
var first  = cache.GetOrderedSlides("DeckA").FirstAsync().Timeout(30.Seconds()).Await(ct);
var second = cache.GetOrderedSlides("DeckA").FirstAsync().Timeout(30.Seconds()).Await(ct);
first.IsCompleted.Should().BeFalse("the query is still closed, so no value can have arrived");
bothAttached.OnNext(Unit.Default);
bothAttached.OnCompleted();
```

Two details carry the fix:

- **Gate the SUBSCRIPTION, never the values.** A `SkipUntil` would DROP a change feed's one
  `Initial` snapshot if it arrived before the gate opened, and the test would hang instead of
  racing — a worse failure wearing a better colour. Compose the gate ahead of the source
  (`gate.Take(1).SelectMany(_ => inner.Query<T>(request))`) so the source is not subscribed at all
  until the test says so.
- **Assert the not-yet.** `first.IsCompleted.Should().BeFalse(...)` is the assertion the racy
  arrangement had no way to make: it states, at the moment it matters, that neither subscriber can
  have let go.

The same rule produced `IoPoolQueueWaitTest`'s shape from the other direction: a leaf must be proven
to have REACHED the gate (`SpinWait.SpinUntil(() => pool.CurrentlyWaiting == 1, …)`) before the test
holds the slot, because a fixed hold lets a slow ThreadPool hand the leaf its slot without it ever
having waited — and the contended assertion then passes on scheduling luck.

🚨 **Prove it with the widener, not with a green run.** Both arrangements pass on an idle machine,
so a green run says nothing. Widen the window in place — a `Task.Delay` between the two
subscriptions, deleted before committing — and require the OLD arrangement to fail every run with
the CI's exact message, the NEW one to pass with the widener still in, and to pass again once it is
removed. Measured 2026-09-16: the old shape failed with *"Expected value to be 1 … but found 2"*,
byte for byte what CI reported on a pull request whose diff could not reach this code.

---

## 🚨 Never `await` an Observable the Code Under Test Signals

**`await someObservable` resumes its continuation INLINE on whatever thread called `OnNext`.** If
that thread belongs to the code under test — a hub's action-block thread, a grain turn — then
*everything after the await* runs there: the remaining assertions, the `finally`, the method return,
and the framework's teardown.

If the test is also waiting on that same component (disposing it, draining it, parking it), it
**wedges in total silence**. No assertion fails and no `.Timeout()` fires, because the thread that
would carry the test forward is the one being waited on.

### The fingerprint: absent teardown lines

```
=== TEST START: … ===
[+0.2s]  [Warning] … LATE_NACK_REENQUEUE … corr=…      ← the awaited line WAS written
[+5.2s]  [Warning] … Dropping UnsubscribeRequest …
                                                        ← 84 seconds of nothing
[+89.8s] [FAIL] Test execution timed out after 90000 milliseconds
```

**No `=== TEST END ===` and no `[DISPOSE]` lines.** Those are present on every ordinary failure, so
their absence means the method never returned — which separates "an inner wait lost" from "the
thread is gone". Read that before theorising about which bound expired.

### Why it presents as a flake

A capture that replays a buffer and then concats a live subject has **two paths that resume on
different threads**:

| path taken | continuation resumes on | observed |
|---|---|---|
| the **replay** of already-buffered values | the test's own thread | 663 ms, green |
| the **live subject** | the component's own thread | 90 s, killed |

So it passes locally and on the PR build, and fails in the merge queue. **Re-running proves
nothing** — local runs may take the replay path every time. Measured on #3477/PR #3532: five green
local runs did not discriminate, because the *un-fixed* code also passed locally.

### The control that names it in one step

Its sibling test — identical park/dispose choreography, differing only in that it asserted on
durable storage rather than on a log line — **passed in the same shard of the same run**. When two
tests of one mechanism disagree on a single host, diff what they *observe*, not what they *do*.

### What to write instead

**Poll a buffer from `Observable.Interval`**, so the continuation resumes on the scheduler's thread:

```csharp
// ✅ the continuation lands on the scheduler's thread, never on the hub's
return Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
    .Select(_ => lines.ToImmutableArray().Select(l => re.Match(l)).FirstOrDefault(m => m.Success))
    .Where(m => m is not null).Select(m => m!)
    .FirstAsync().Timeout(within);
```

Delete the subject rather than leaving it unused — keeping one invites the await back. And **prefer
asserting ground truth (what landed in storage) over a log line**: a log line buys determinism only
if *observing* it cannot perturb what produced it.

🚨 **Avoiding `.ToTask()` does not avoid this.** The ban on `.ToTask()` exists because *"a Task
completed inside an Rx pipeline resumes its awaiter inline on the signalling thread"* — and that
reasoning applies verbatim to awaiting the observable directly. In the incident above, the offending
method's own comment cited that rule to justify returning `IObservable`, and then the caller awaited
it. The hazard is the **inline resume**, not the `Task`.

## "One Emission Carrying Everything" — Batched or Late? The First Snapshot Decides

A test that asserts progress **streams** (several distinct snapshots, not one lump) fails with a
single emission for two opposite reasons, and they demand opposite fixes:

* **BATCHED** — the producer really did coalesce or stall, and the emissions were released together.
  A product defect.
* **LATE** — the subscription attached *after* the work finished, so the owner's first snapshot
  already contains the final state. Correct behaviour; a racy test.

🚨 **The discriminator is whether the FIRST emission has content.** `GetMeshNodeStream(path)` emits
the owner's snapshot **at subscribe time**, so a healthy run starts empty and grows:

```
healthy:  0@44ms, 1@86ms, 3@247ms, 4@335ms     ← first snapshot EMPTY
late:     [4@931ms]                            ← first snapshot already terminal
```

Both fail the same `>= 3 snapshots` assertion, and the failure message looks identical. On
2026-08-26 a single `Observed: [4@914ms], but found 1` was argued at length as a batching defect
before measurement showed it was a late subscribe (#2421) — the production path was correct all
along. **Print the first emission's count in the failure message, and read it before theorising.**

**Fix a late subscribe by ordering, never by tolerance.** Do not widen the deadline or lower the
snapshot count — that hides the real thing the test checks. Make the work wait for the subscriber:
gate the producer on a node the test flips *after* it has observed its own (empty) first snapshot,
and **assert that snapshot is empty** before releasing. `ProgressGate` in `MeshWeaver.AI.Test` is
the worked example.

🚨 **Also check your stimulus against the publisher's coalescing window.** `ActivityLogLogger`
coalesces at **100 ms**, so a script emitting four messages at 80 ms intervals is *entitled* to
deliver them as two snapshots — the test would be under-specified, not the product wrong. Space the
stimulus wider than the window rather than loosening the assertion.

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

The shared `OrleansMeshTestBase` exposes a synchronous `GetClient(clientId?, userId)` that wires this up (it calls `routingService.RegisterStream(client.Address, client.DeliverMessage)`) — there is **no** async client-acquisition; the test calls `GetClient()` directly. Symptom of a missing registration: `await client.Observe(GetDataRequest(...)).Should().Emit()` never emits and the assertion times out.

---

## The Mesh Pool — Leased Running Clusters, Not Per-Class Boots

**Direction of record (maintainer, 2026-09-01):** *"why do we start so many silos? we should
integrate into the mesh"* … *"we can have a pool of running meshes and then parallelize over this
pool"* … *"if we have static node repo, we can just recycle."*

The per-class model booted ~90 Orleans silos per run (~300–500 ms each) and needed a background
disposal drain to keep 90 teardowns from wedging the runner — while `test/xunit.runner.json` runs
ONE class at a time, so all that isolation paid for parallelism that never happened. Measured on
the Orleans suite the day this landed: **3 clusters booted instead of one per class**, wall
22s → 19s locally, and the CI shape (slow cores, disposal pile-ups) is where the multiple lands.

**How it works:**

* `OrleansMeshPool` is an xUnit **assembly fixture**: 🚨 the `[assembly: AssemblyFixture(…)]`
  attribute must be declared **in the test assembly itself** (see `AssemblyFixtures.cs`) — an
  attribute in a referenced base library registers nothing, silently: the pool then never
  engages and every class quietly boots its own cluster again. The pool prints a one-line
  receipt (`OrleansMeshPool: N cluster(s) booted`) so an inactive pool is visible, not inferred.
* `OrleansMeshTestBase` **leases** a running cluster (take-or-create, never wait — a waiting
  gate would be the hand-woven primitive `test/` forbids; the runner's own class-parallelism cap
  bounds the pool). The lease is exclusive; on class teardown the cluster returns to the pool.
* **Isolation is by construction, not by process**: the seeded node repo is static and
  read-only; what a test creates lives under its own paths and addresses; client hubs are
  cleaned per class (`CleanupClientAsync`). Self-healing disruptions (forced deactivations,
  dead-subscriber eviction) hand back a healthy cluster by the platform's own guarantees — they
  pool like everyone else.
* A class that genuinely wrecks CLUSTER-WIDE state (kills silos, asserts global counters) opts
  out with `protected override bool UsePooledMesh => false` and keeps a dedicated cluster — or
  builds its own host, as the silo-kill suites already do.
* **Porting the shape to another repo's Orleans suite** is two steps: reference the TestBase and
  add the one-line `AssemblyFixtures.cs` registration. Derived fixtures pool per fixture TYPE,
  so a repo with its own silo configurator (the AI rig) gets its own pooled instances.

If cross-class bleed ever surfaces, the fix is a **recycle step on the lease** (delete the
test-created nodes against the static baseline) — a designed fixture stage, never a `Clear()`
on shared state.

## CI-Only Failure ≠ Flake — It's a Real Bug

When a test fails on CI but passes locally, **don't label it a flake and skip it.** Every CI-only failure investigated in this repo traced to a real bug: an eventually-consistent index read too eagerly; a hot `Subject` that should have been a `ReplaySubject`; an `AccessContext` lost across the post-pipeline boundary; an init ping removed from a hub that doesn't self-activate. Skipping hides the bug; running it on CI is exactly what surfaced it.

Fix the bug. Re-running a hung test "to see if it was a flake" hides the race — see [Debugging Message Flow](/Doc/Architecture/DebuggingMessageFlow) for the trace tags to grep instead.

### 🚨 The other axis: green in the SUITE, red ALONE, same binary

CI-vs-local is not the only comparison that carries information, and the second one is easier to
miss because it needs nothing rebuilt. **A test that passes inside its suite and fails when run
alone is asserting something it never observed** — the suite was supplying, by accident, a wait the
test does not contain.

It is the sharper signal of the two: CI-vs-local can be blamed on the machine, whereas suite-vs-alone
holds the binary, the machine and the code fixed and varies only *what else was running*. So the
difference is the test, always.

Measured instance: `StreamReleasesItsHubTest.ADisposedStream_ReleasesItsHub_SoTheHubBecomesCollectable`
(#3321) — **485 passed** in the full project, **1 failed** with a `--filter` down to that one test,
same `.dll`. The assertion collected immediately after `Dispose()`, which only *starts* a hub's
teardown; the surrounding tests had been paying for the wait. See
[❌ A reachability assertion that collects before teardown has finished](#-a-reachability-assertion-that-collects-before-teardown-has-finished).

🚨 The reflex to resist is running the isolated case again and calling the green one real. **The
PASSING run is the control**, not the weaker failure: it is the one that tells you which ambient
condition the test is silently depending on.

---

## Reading a CI Failure — What the Run Actually Carries

A red test on CI hands you three artifacts, and knowing which one answers which question is the
difference between attributing a failure and arguing about it.

**1. The `.trx`, per project.** The shards run each project's NATIVE xUnit v3 host
(`dotnet <Name>.dll -trx …`), and that writer puts captured output in
`<Output><TextMessages><Message>` — **not** in `<StdOut>`, which the vstest writer uses. `<StdOut>`
is populated only when a test writes to the process console (`MeshWeaver.Portal.E2E.Test` and
`MeshWeaver.PluginImage.Test` do; nothing else does). Looking for `StdOut` and finding it empty
therefore says nothing about the project — issue #2495 was filed on exactly that reading.

The trx also carries `startTime` / `endTime` per result, which is how you compute **what else was
running**. That matters in a project that opts into intra-project parallelism: in
`MeshWeaver.Hosting.Orleans.Test` each test class boots its own Orleans cluster and
`maxParallelThreads: 4`, so a failing test can have six other classes — and their silos — live in
the same process.

**2. `collected-logs/_meshweaver-test-trace.log`, one file per shard.** This is the only test log CI
keeps, and it is the only evidence that survives a host killed at the wall-clock cap, which writes
no trx at all. Two kinds of line, both carrying `pid=` because every project in a shard appends to
this one file:

- **Window markers**, written by `AutoTestLoggingAttribute` for every `TestBase` subclass in every
  project: `TEST_START <method>` and `TEST_END elapsed=<n>ms <method> outcome=<…>` (plus the
  exception type and message on a failure). `elapsed` is measured between the two hooks — the trx
  `duration` is larger because it includes fixture construction.
- **Fault records** — `[FAULT] [<level>] [<category>]` plus the exception's type and stack, written
  by both xUnit loggers for any record at `Warning` or worse that carries an exception, whether or
  not a test output helper is active.

Joining them is the point: `grep pid=<n>` for the window brackets, and every `[FAULT]` timestamp
between a `TEST_START` and its `TEST_END` belongs to that window. A `TEST_START` with **no**
matching `TEST_END` names the test a killed host was stuck in. In a parallel project the join
narrows a fault to the handful of windows open at that instant rather than to one test — say so
when you use it.

Records are rate-bounded (`FaultRecordBudget`: 100 per 10 s) and every suppressed stretch announces
itself, so `grep FAULT-BUDGET` answers "is this log complete?".

**3. The `[CI] <name> exit=<n>` markers in `test/test-results.log`.** The host's own exit code,
classified (`TESTFAIL` / `TIMEOUT` / `SIGNAL` / `MASKED`).

### 🚨 A crashed host is a FAILED test result, not a silence

A host that streams green results and then dies leaves a trx that says "N passed, 0 failed", and
every reporter that parses it repeats that over a dead process. `MeshWeaver.Content.Test` did this
with `exit=139`. Pass/fail evidence and liveness evidence were two channels and only the first was
read.

They are one channel now: for any exit the trx cannot explain, the shard runs
`.github/scripts/record-host-crash.py`, which writes a `<project>.HOST_CRASHED` failure **into the
trx** — creating the file when the host wrote none. So the shard summary, the per-shard check and
the consolidated check all name the crash, and a reporter added later inherits the behaviour
instead of the blind spot. `CrashedHostIsNeverAPassGuard` runs the real script against both shapes
and is pinned by its own negative controls.

**Known gap, so you do not read it as evidence:** the window markers come from an attribute applied
to `TestBase`, so a test class that does not derive from it writes none. In
`MeshWeaver.Hosting.Orleans.Test` that is 25 classes / 86 of 208 tests (`RoutingGrain*`,
`OrleansCrossSilo*`, `TwoSiloRecycleConvergenceTest`, …). Their faults still reach the file; only
the brackets are missing.

---

## Coverage Expectations

The [/code skill](/Skill/code) sets the bar for NodeTypes and data models: **a test per invariant, per branch, per boundary, per degenerate input** — plus a serialization round-trip. A NodeType with a single happy-path test is demoed, not tested.

---

## 🚨 The Outer Bound Must Lose to the Inner Wait

Two numbers bound a test. The **inner** one is the budget it waits on — `TestTimeouts.Convergence`
and friends — and it fails with a sentence: *"the owner-side sub-hub must actually be gone before a
click can miss it"*. The **outer** one is xUnit's kill, and it fails with
*"Test execution timed out after N milliseconds"* and nothing else.

**If the outer bound does not strictly exceed the inner one, only the useless message can ever be
printed.** `TestTimeouts.TestMilliseconds` has said so for a while, for tests that write
`[Fact(Timeout = …)]`. The ~5,200 that write a plain `[Fact]` had no such rule, and both of their
outer bounds were a literal **30 000**:

| | was | now |
|---|---|---|
| `test/xunit.runner.json` → `methodTimeout` | 30 000 | **316 000** |
| `test/MeshWeaver.Hosting.Orleans.Test/xunit.runner.json` | 30 000 | **316 000** |
| `HubFactAttribute.Timeout` (Release) | 30 000, restated | **`TestTimeouts.DefaultOuterBoundMilliseconds`** |

Against inner budgets of 36 s local / **108 s** on CI (`Convergence`) and up to **216 s**
(`CrossSilo`). So every plain `[Fact]` whose wait elapsed was killed roughly a minute and a half
before it could speak, and the failure named none of the three things that could have gone wrong.

**`HubFactAttribute` is the half that makes this worth reading.** It carried its own
`Timeout = 30000` under a comment saying *"30s matches the runner-wide `methodTimeout`"* — a number
kept in step by a sentence. Raising only the config files would have left every `[HubFact]` exactly
as it was, which is a fix that looks complete and changes nothing for the test that prompted it.

**The rule is universal: the cap dominates EVERY budget in `TestTimeouts`, with no exception.**
Two narrower answers were tried and both were wrong, which is why it is worth stating that way:

- `TestMilliseconds` is numerically **equal** to `CrossSilo` (both `Convergence × 2`), and a cap
  equal to a budget kills the wait at the instant it expires — the same anonymous failure one level
  up.
- Clearing `CrossSilo` alone still left **six** plain `[Fact]` tests that await `WriteConvergence`
  (280 s on CI) under a 252 s cap — `DeletePreflightNamesTheSilentDescendantTest` and five others —
  because the assumption that *"a `WriteConvergence` test carries its own literal"* is simply false,
  and nothing in that budget's documentation said otherwise.

**A rule with an exception is a rule somebody has to know about**, and this one's exception was
invisible from the place you would look.

The margin is additive for the reason `LocalConvergence` gives: what it covers is one terminal
*propagating* to the assertion, and that cost does not scale with the bound. A ratio would put the
cap past fifteen minutes on CI and every wedged test would pay it.

**The cost, stated:** a genuinely wedged test now fails in ~5 min rather than 30 s. That is the
trade — the 30 s cap was not detecting wedges that the inner budgets do not detect, it was only
deleting their diagnosis.

`WriteTestMilliseconds` remains what a `WriteConvergence` test uses when it wants an explicit,
tighter bound of its own; it is no longer the only thing standing between such a test and an
anonymous kill.

`TestTimeoutsTest.EveryRunnerConfigBoundsAWaitThatCanActuallyReport` sweeps **every**
`xunit.runner.json` under `test/` — a project shipping its own takes `methodTimeout` from that copy,
so a guard reading only the shared default would pass while an opted-in project stayed at 30 s — and
fails if it finds none, rather than passing having checked nothing.

## Intra-Project Parallelism — How a Project Opts In

The suite runs single-threaded by default: `test/xunit.runner.json` sets `parallelizeTestCollections: false`, `maxParallelThreads: 1`. Parallel safety is a property of the tests, not of the runner, so a project opts in **individually** by shipping its own `xunit.runner.json` next to its `.csproj`:

```json
{
  "parallelizeAssembly": false,
  "parallelizeTestCollections": true,
  "maxParallelThreads": 4,
  "methodTimeout": 316000
}
```

🚨 **`methodTimeout` is not a free number — it is the outer bound for every test that declares
none, and it has one job: to lose to the wait inside the test.** See the section above.

`test/Directory.Build.props` picks the project-local file over the shared default on an `Exists()` condition, and `VerifyXunitRunnerConfigCopied` **fails the build** if neither branch lands a config in `$(TargetDir)` — because with no config at all xUnit falls back to *its* defaults, which is unbounded parallelism nobody asked for. Live opt-ins today: `MeshWeaver.Content.Test`, `MeshWeaver.Hosting.Orleans.Test`, `MeshWeaver.AI.Test`.

### Classes that need the machine go in a serial collection

Some tests deliberately saturate the box and then judge the result on a wall clock. Four of those timesharing one 4-vCPU runner blow bounds that hold with room to spare when each has the box. Put them in one `DisableParallelization` collection — `MeshWeaver.AI.Test/ConcurrencyStressCollection.cs` is the worked example:

```csharp
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConcurrencyStressCollection
{
    public const string Name = "AI concurrency stress";
}

// …and on each such class:
[Collection(ConcurrencyStressCollection.Name)]
public class CrossHubPatchAtomicityTest(ITestOutputHelper output) : AITestBase(output)
```

**Membership is structural, not "whatever failed last time."** A class belongs there only when BOTH hold:

1. it **creates concurrency of its own** — N operations deliberately in flight at once, or a dedicated pump thread; **and**
2. its verdict is a **wall-clock bound on that burst** — a deadlock or lost-write detector, not a functional comparison.

(1) alone is just a slow test, which is what parallelism is for. (2) alone is a generous budget on sequential work, which survives sharing a box.

🚨 **Never widen those bounds to make a starved run pass.** They are deadlock detectors; a detector with a padded budget detects nothing. Scheduling is the right lever precisely because the tests are correct and the contention is the artefact.

🚨 **A green local run does not prove a project is parallel-safe.** `DOTNET_PROCESSOR_COUNT=4` sizes the thread pool and GC as though the machine had four cores but **does not take the other cores away**, so a test that spawns its own concurrency still gets real parallelism on a dev box. An 18-core box produced five consecutive green runs of an opt-in that CI then failed on three concurrency-stress tests. Measure the opt-in on CI.

---

## Running Tests

Always run tests in the background — they take minutes.

🚨 **Build the project first, and demand a positive summary line.** `--no-build` / `--no-restore`
against a project the current worktree has never built exits **0 having run nothing** — it looks
exactly like a clean pass. A fresh worktree has no `bin/`, so this is its default state, and two
"passing" runs were banked on it before anyone noticed. The signal to read is the last block:

```text
Test run summary: Passed!
  total: 112
  failed: 0
```

A `total:` of 0 — or no summary block at all — is a run that proved nothing, whatever the exit code.

```bash
dotnet build test/MeshWeaver.NodeOperations.Test/MeshWeaver.NodeOperations.Test.csproj
dotnet test test/MeshWeaver.NodeOperations.Test --no-build
dotnet test test/MeshWeaver.Acme.Test --no-build --filter "FullyQualifiedName~TodoDataChangeWorkflowTest"
```

🚨 **`dotnet test` here is Microsoft.Testing.Platform, not VSTest, and a local run leaves NO `.trx`
behind unless you ask for one.** xunit.v3 4.x ships MTP v2, which refuses the VSTest target on the
.NET 10 SDK outright, so the repository opts in through the root `global.json`
(`{"test": {"runner": "Microsoft.Testing.Platform"}}`). Consequences worth knowing before you go
looking for a file that is not there:

- The old always-on `<VSTestLogger>trx</VSTestLogger>` in `test/Directory.Build.props` is inert and
  has been removed. A run that needs a parseable result file asks for it:
  `dotnet test <proj> --no-build --report-xunit-trx --report-xunit-trx-filename run.trx --results-directory <dir>`.
- `-l:trx`, `--logger`, `--blame-hang-*` and `-warnaserror` are VSTest-era arguments. MTP rejects
  them, and `-warnaserror` in particular fails in the way that matters least visibly: **`Zero tests
  ran`, exit 5** — a run that executed nothing while looking like a tooling hiccup.
- `--filter "FullyQualifiedName~…"`, `-c`, `--no-build`, `--no-restore` and `--results-directory`
  are unchanged.
- CI's shards never call `dotnet test` at all — they launch each project's native xunit v3 host
  (`dotnet <Name>.dll -trx …`), so CI's trx and its exit markers are unaffected by any of this.

There is no `timeout` (or `gtimeout`) on the macOS dev host, so `timeout 20m dotnet test …` runs
nothing at all — cap a local run by backgrounding it and polling `date -u` instead.

Use `FullyQualifiedName~` — it is the only `--filter` property this repo uses (`.github/workflows/flake-repro.yml`), and it matches both a class name and a `Class.Method` pair. `ClassName~` appears nowhere in the build; prefer `FullyQualifiedName~` rather than assuming the adapter honours it. Never use `--verbosity minimal` when a failure is possible — it hides stack traces.

**Workflow: run → read → fix → run once more.** Do not re-run a hung test two or three times "to see what happens" — grep the `MESSAGE_FLOW:` trace in [Debugging Message Flow](/Doc/Architecture/DebuggingMessageFlow) instead.

---

## Test Project Layout

- Framework code lives in `src/`, tests in `test/`, with one `*.Test` project per production project where it makes sense.
- Shared fixtures live in `test/MeshWeaver.Hosting.Monolith.TestBase` and the Orleans test base.
- NodeType tests live in the NodeType's `Test/` namespace (`Namespace/Type/Test/TypeTest.cs`).

---

## References

- [Negative Controls](/Doc/Architecture/NegativeControls) — a pin is only a pin if it fails against the defect; the procedure, and three tests that passed while proving nothing
- [AsyncLocal Across Scheduler Hops](/Doc/Architecture/AsyncLocalAcrossHops) — why a diagnostic can be dead while `IsEnabled(Debug)` reads true, and why silo-side `ILogger` is unreliable in Orleans tests
- [Reactive Test Assertions](/Doc/Architecture/ReactiveTestAssertions) — assertion API, §2 "await the assertion, never block", §3 genuinely-async catalogue
- [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls) — why hub-reachable code is `IObservable<T>`, never `Task<T>`
- [CQRS — Queries vs. Content Access](/Doc/Architecture/CqrsAndContentAccess) — why `ReadNode` is the right read after a write
- [Debugging Message Flow](/Doc/Architecture/DebuggingMessageFlow) — reading the framework's own trace when a test hangs
- `test/MeshWeaver.Hosting.Monolith.TestBase/MonolithMeshTestBase.cs` — the base every monolith test inherits
