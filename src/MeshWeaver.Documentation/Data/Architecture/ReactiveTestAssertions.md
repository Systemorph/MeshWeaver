---
Name: Reactive Test Assertions
Category: Architecture
Description: The MeshWeaver.Reactive.Assertions surface — assert on observables directly, awaiting the terminal assertion instead of hand-rolling an Rx→Task bridge. Covers the full API, why the assertion subscribes on the thread pool, error assertions via Materialize, and the equivalency/JSON helpers.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M22 11.08V12a10 10 0 1 1-5.93-9.14"/><polyline points="22 4 12 14.01 9 11.01"/></svg>
---

# Reactive Test Assertions

MeshWeaver is reactive end-to-end: services, handlers, layout areas, and activities return `IObservable<T>` and never `await` (see [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls)). Its tests follow the same principle — **you assert on the stream directly, and the Rx→Task bridge lives inside the assertion instead of in your test body**.

All test assertions flow through `MeshWeaver.Reactive.Assertions`, an in-house library (it *replaced* FluentAssertions — that package is no longer referenced) wired in via a global `using` and project reference in `test/Directory.Build.props`. The names and chaining patterns (`.And` / `.Which`, trailing `because` args) are familiar. What is different is that the **observable assertions own the wait**: each terminal method returns a `Task` that the test body `await`s, so a test method is `async Task` and contains no hand-rolled `.FirstAsync().ToTask()`, `.Result`, `.Wait()` or `Task.Delay`.

> **🚨 The terminal assertions are `Task`-returning, not blocking.** `Emit()` / `Match()` return `Task<T>`; `Be()` / `Complete()` / `NotEmit()` return `Task<ObservableAssertions<T>>`. **Every one must be `await`ed.** An un-awaited `obs.Should().Emit();` statement compiles without a warning, subscribes, and then lets the test race on — the classic green-but-lying test. `ObservableAssertions.cs` says so in its own doc comment: the wait is *"never a thread-blocking `ManualResetEventSlim` + `Wait`"*.

For the surrounding test-writing rules, see [Writing Tests](/Doc/Architecture/WritingTests).

<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 760 310" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity=".6"/>
    </marker>
    <marker id="arr-red" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="#e53935"/>
    </marker>
    <marker id="arr-grn" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="#43a047"/>
    </marker>
  </defs>
  <text x="380" y="24" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="bold" fill="currentColor" fill-opacity=".85">Awaited Reactive Assertion — Pooled Subscribe vs. Sync-Context Starvation</text>
  <rect x="10" y="44" width="740" height="118" rx="10" fill="none" stroke="#43a047" stroke-width="1.5" stroke-opacity=".45"/>
  <text x="20" y="62" font-family="sans-serif" font-size="11" fill="#43a047" fill-opacity=".9" font-weight="bold">✓  await the assertion (correct)</text>
  <rect x="30" y="72" width="130" height="38" rx="8" fill="#1e88e5"/>
  <text x="95" y="87" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff" font-weight="bold">async Task Test()</text>
  <text x="95" y="101" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#fff">xUnit sync context</text>
  <line x1="160" y1="91" x2="208" y2="91" stroke="#43a047" stroke-width="1.5" marker-end="url(#arr-grn)"/>
  <rect x="210" y="72" width="160" height="38" rx="8" fill="#5c6bc0"/>
  <text x="290" y="87" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff" font-weight="bold">await .Should().Match(…)</text>
  <text x="290" y="101" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#fff">SubscribeOn(TaskPool) + ToTask</text>
  <line x1="370" y1="91" x2="418" y2="91" stroke="#43a047" stroke-width="1.5" marker-end="url(#arr-grn)"/>
  <rect x="420" y="72" width="150" height="38" rx="8" fill="#26a69a"/>
  <text x="495" y="87" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff" font-weight="bold">Hub delivers emission</text>
  <text x="495" y="101" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#fff">on the thread pool</text>
  <line x1="570" y1="91" x2="618" y2="91" stroke="#43a047" stroke-width="1.5" marker-end="url(#arr-grn)"/>
  <rect x="620" y="72" width="118" height="38" rx="8" fill="#43a047"/>
  <text x="679" y="87" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff" font-weight="bold">Task completes →</text>
  <text x="679" y="101" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#fff">assertion passes</text>
  <rect x="210" y="118" width="160" height="22" rx="6" fill="none" stroke="#43a047" stroke-width="1" stroke-opacity=".5" stroke-dasharray="4,3"/>
  <text x="290" y="133" text-anchor="middle" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".65">yields the context while waiting</text>
  <rect x="10" y="176" width="740" height="118" rx="10" fill="none" stroke="#e53935" stroke-width="1.5" stroke-opacity=".45"/>
  <text x="20" y="194" font-family="sans-serif" font-size="11" fill="#e53935" fill-opacity=".9" font-weight="bold">✗  hand-rolled blocking bridge (starves)</text>
  <rect x="30" y="204" width="130" height="38" rx="8" fill="#e53935"/>
  <text x="95" y="219" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff" font-weight="bold">.Result / .Wait()</text>
  <text x="95" y="233" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#fff">on the sync-context thread</text>
  <line x1="160" y1="223" x2="208" y2="223" stroke="#e53935" stroke-width="1.5" marker-end="url(#arr-red)"/>
  <rect x="210" y="204" width="160" height="38" rx="8" fill="#5c6bc0"/>
  <text x="290" y="219" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff" font-weight="bold">Subscribe on that thread</text>
  <text x="290" y="233" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#fff">no SubscribeOn hop</text>
  <line x1="370" y1="223" x2="418" y2="223" stroke="#e53935" stroke-width="1.5" marker-end="url(#arr-red)"/>
  <rect x="420" y="204" width="150" height="38" rx="8" fill="#8e24aa"/>
  <text x="495" y="219" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff" font-weight="bold">Mesh continuations</text>
  <text x="495" y="233" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#fff">funnelled back to one thread</text>
  <line x1="570" y1="223" x2="618" y2="223" stroke="#e53935" stroke-width="1.5" marker-end="url(#arr-red)"/>
  <rect x="620" y="204" width="118" height="38" rx="8" fill="#e53935"/>
  <text x="679" y="219" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff" font-weight="bold">STARVES →</text>
  <text x="679" y="233" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#fff">8 s vs. 3 ms</text>
  <rect x="210" y="249" width="160" height="22" rx="6" fill="none" stroke="#e53935" stroke-width="1" stroke-opacity=".5" stroke-dasharray="4,3"/>
  <text x="290" y="264" text-anchor="middle" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".65">context held, can't run continuations</text>
  <path d="M570 223 Q600 286 210 270 Q185 270 185 249" fill="none" stroke="#e53935" stroke-width="1.5" stroke-dasharray="5,3" stroke-opacity=".6" marker-end="url(#arr-red)"/>
  <text x="390" y="302" text-anchor="middle" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".5" font-style="italic">maxParallelThreads: 1 — everything serialises behind the held thread</text>
</svg>

*The assertion `SubscribeOn`s the source onto `TaskPoolScheduler` and bridges with `.ToTask()`, so every mesh round-trip runs on the thread pool and only the awaited result hops back to the test. Blocking that thread yourself (`.Result` / `.Wait()`) funnels those continuations back onto xUnit's single-threaded `MaxConcurrencySyncContext` — measured at 8 s versus 3 ms on `AddressResolutionTest` (2026-06-15). Do not remove the `SubscribeOn`.*

---

## 1. The Observable Assertion Surface

`obs.Should()` returns an `ObservableAssertions<T>`. Each terminal method subscribes, waits up to the configured timeout (default 10 s) for the emission you describe, asserts the result, and **returns a `Task` carrying the matched value** so you can chain further assertions on the awaited result.

| Member | Returns | Behaviour |
|---|---|---|
| `obs.Should()` / `obs.Should(timeout)` | `ObservableAssertions<T>` | Begin an assertion chain (default timeout 10 s). Synchronous. |
| `.Within(timeout)` | `ObservableAssertions<T>` | Override the wait deadline for the rest of the chain. Synchronous. |
| `.Emit(because?)` | `Task<T>` | Await the first emission; return it. Fails on timeout or empty completion. |
| `.Match(x => pred, because?)` | `Task<T>` | Await the first emission satisfying `pred`; return it. **The workhorse** — fold the assertion into the predicate. |
| `.Be(expected, because?)` | `Task<ObservableAssertions<T>>` | First emission must equal `expected`. |
| `.Complete(because?)` | `Task<ObservableAssertions<T>>` | Stream must complete within the timeout (no value required). |
| `.NotEmit(within: t, because?)` | `Task<ObservableAssertions<T>>` | Nothing must arrive within `t` — the **one** place a fixed wait is correct. Keep `t` short. |

```csharp
[Fact]
public async Task ObserveQuery_EmitsInitialResults()            // ← async Task; every terminal call awaited
{
    var p = P();
    // Cold observables: the assertion's Subscribe IS the write.
    await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project1") with { NodeType = "Markdown" }).Should().Emit();
    await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project2") with { NodeType = "Markdown" }).Should().Emit();

    // Fold the assertion into the predicate: wait for the snapshot that has both items.
    var changes = await ObserveAccumulated($"path:{p} nodeType:Markdown scope:descendants")
        .Should(WaitTimeout).Match(acc => acc.Count >= 1 && acc[0].Items.Count >= 2);
    changes[0].ChangeType.Should().Be(QueryChangeType.Initial);
}
```

(That is `ObservableQueryTests.ObserveQuery_EmitsInitialResults` in `test/MeshWeaver.Query.Test` — the role model for this shape.)

`.Emit()` and `.Match()` **return** the matched value, so `var x = await obs.Should().Match(...)` replaces a hand-rolled `var x = await obs.FirstAsync().ToTask()` one-for-one.

> **The wait is the sanctioned test-edge Rx→Task bridge.** The source is `SubscribeOn`'d onto `TaskPoolScheduler`, filtered, `Take(1)`'d, bounded with `Timeout` (throwing a private sentinel so the assertion's own timeout stays distinguishable from a `TimeoutException` raised by the *source*), and bridged via `.ToTask()`. Nothing blocks a thread. A timed-out assertion reports what the stream actually emitted — "emitted nothing at all" versus "last of N emissions was …" — because those two failures have opposite fixes.

---

## 2. The Rule That Makes It Safe: Await the Assertion, Never Block

The assertion never blocks a thread — it `SubscribeOn`s the source onto `TaskPoolScheduler` and hands you a `Task`. That `SubscribeOn` is **load-bearing, not cosmetic**: xUnit runs `async Task` tests under a single-threaded `MaxConcurrencySyncContext` (`maxParallelThreads: 1` in `test/xunit.runner.json`). Subscribe a cold mesh observable directly on that thread and every mesh continuation is funnelled back onto the one sync-context thread and serialises.

> **A test method that uses a reactive assertion is `async Task`, and every terminal call is `await`ed.**

In practice that means the following substitutions:

- Stream waits → `await obs.Should().Match(...)`
- Cold observable-returning calls (`IMeshService.CreateNode/UpdateNode/DeleteNode`, `hub.Observe(...)`, `ReadNode`) → `await ….Should().Emit()` — the subscribe *is* the work
- Request/response polling loops → `await Observable.Interval(...).SelectMany(...).Should().Match(...)`
- `CancellationToken` declarations → deleted (the assertion's `.Within(t)` is the deadline)

`hub.Observe(...)` and the `IMeshService` write methods return `IObservable<T>`, **not** `Task<T>`. Awaiting the observable *directly* looks like a normal `Task` await but isn't — Rx's awaiter yields the *last* value, so it silently waits for completion rather than the first matching emission. Go through `.Should()` instead.

A hand-rolled `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` in a test body is the failure mode from the other direction — it holds the sync-context thread and bypasses the pooled subscribe. Always go through `.Should()`.

**The mirror-image mistake is dropping the `await`.** `obs.Should().Emit();` as a bare statement is a discarded `Task<T>`: it subscribes and returns immediately, so the test asserts nothing and proceeds on a race. In an expression position the compiler usually catches it (`Task<MeshNode?>` will not bind to `MeshNode?`), but as a statement it is silent.

---

## 3. Genuinely-Async Constructs Alongside the Assertions

Test bodies are `async Task` throughout, so nothing has to be "converted away" from async any more. What still deserves a deliberate decision is *which* async construct to reach for:

- **Stream mocks.** A fake `IChatClient` (`GetStreamingResponseAsync` / `GetResponseAsync` with `await Task.Delay` / `Task.Yield` between chunks) or a fake `IAsyncEnumerable` (`await Task.CompletedTask; yield break;`). These implement async interfaces — leave them async; they are infrastructure, not the test body.
- **Async system-under-test.** The test drives a genuinely async API: an `await foreach` over the SUT's `IAsyncEnumerable`, a parser's `ParseAsync`, an ASP.NET middleware's `InvokeAsync(context)`, a controller's `ExchangeToken(...)`. The async *is* what is being verified.
- **Genuine file / network / process I/O.** `File.ReadAllTextAsync`, `StreamReader.ReadToEndAsync`, `Process.WaitForExitAsync`, an HTTP handler.
- **Concurrency as the SUT.** `Task.WhenAll` / `WaitAsync` where the in-flight concurrency or a deadlock reproduction is the thing under test.

What is **never** acceptable in a test body: `.Result`, `.Wait()`, `.GetAwaiter().GetResult()`, and `Task.Delay` used as a propagation wait (see §5 for the only sanctioned fixed wait).

**Bridging a genuine `Task<T>` / `ValueTask<T>` SDK boundary into the reactive surface:**

```csharp
await call(...).AsTask().ToObservable().Should().Within(t).Emit()
// requires: using System.Reactive.Threading.Tasks;
```

Use this only for a *real* async boundary such as `AIFunction.InvokeAsync` — not for MeshWeaver's own observable-returning methods, which are already observables.

---

## 4. Asserting an Expected Error — `.Materialize()`

`.Should().Emit()` and `.Match()` wrap an `OnError` inside an `ObservableAssertionException` — they do **not** rethrow the original exception type. To assert that a stream errors with a specific type, fold `OnError` into a value using `.Materialize()`:

```csharp
var error = await source.Take(1).Materialize()
    .Should().Within(5.Seconds()).Match(n => n.Kind == NotificationKind.OnError);
error.Exception.Should().BeOfType<DeliveryFailureException>();
```

`using System.Reactive;` provides `NotificationKind`. This is the reactive replacement for `await act.Should().ThrowAsync<T>()`.

For a **synchronous** throwing call, the FluentAssertions-shaped `((Action)(() => …)).Should().Throw<T>().WithMessage("*…*")` still applies.

---

## 5. Fold the Assertion Into the Predicate

`.Match(items => items.Count == 2)` waits for the *right* state, eliminating the classic "wait, then assert" race that passes locally but flakes under CI load.

**Never take the first emission unconditionally.** A synced or multi-query stream's first emission may carry only one upstream's partial result. If items trickle in (`Added` events after a short `Initial`), accumulate first, then assert:

```csharp
await stream.Scan(ImmutableList<T>.Empty, (acc, change) => acc.AddRange(change.Items))
    .Should().Match(acc => acc.Count == N);
```

`.NotEmit(within)` is the only place a fixed wait is intentional — a "nothing should happen" test has no positive signal to wait for. Keep the window short.

---

## 6. `BeEquivalentTo` — Pass the Hub's `JsonSerializerOptions`

Equivalence serializes both sides with `System.Text.Json`, so the polymorphic `$type` discriminators must line up. The options **must come from the owning hub** — this deliberately routes equivalence through the real serializer:

```csharp
actual.Should().BeEquivalentTo(expected, hub.JsonSerializerOptions);
actual.Should().BeEquivalentTo(expected, hub.JsonSerializerOptions, o => o.Excluding(x => x.Message));
```

Use whichever hub the test has in scope: `hub.JsonSerializerOptions`, `Mesh.JsonSerializerOptions`, `GetClient().JsonSerializerOptions`.

For a **plain DTO with no hub in scope** (a parser-result record, no polymorphism), use `JsonSerializerOptions.Default` — do not `new()` a fresh instance.

The builder is FluentAssertions-shaped (`Excluding`, `Including`, `WithStrictOrdering()` — collections compare order-insensitively without it) plus JSON-flavoured extensions: `ExcludeTypeDiscriminator()`, `IncludingTypeDiscriminator()`, `ExcludeProperty<TDecl,TProp>(...)`, `UsingJson(...)`. There is **no** `NotBeEquivalentTo` in this library — assert the negative some other way (e.g. compare a specific member, or `.Should().NotBe(...)`).

---

## 7. JSON-Element Assertions

```csharp
var root = serialized.Should().NotBeNull().And.BeValidJson().Which;   // string -> JsonNode
root.Should().HaveElement("message").Which
    .Should().HaveElement("$type").Which
    .Should().HaveValue(typeof(SubscribeRequest).FullName);
```

The provided helpers are: `BeValidJson()` (on `string`), `HaveElement(name)` / `HaveValue(text)` (on `JsonNode`), and `.As<T>()`.

---

## 8. The FluentAssertions-Shaped Value Surface

The familiar names, chaining, and `because` arguments carry over unchanged:

- **Object:** `Be / NotBe / BeNull / NotBeNull / BeSameAs / NotBeSameAs / BeOfType<T>() / BeAssignableTo<T>`
- **Boolean:** `BeTrue / BeFalse`
- **String:** `Contain / NotContain / StartWith / EndWith / Match / MatchRegex / BeEmpty / NotBeNullOrEmpty / NotBeNullOrWhiteSpace / HaveLength`
- **Comparable:** `BeGreaterThan(OrEqualTo) / BeLessThan(OrEqualTo) / BeInRange / BePositive / BeAfter / BeBefore`
- **Collection:** `HaveCount(GreaterThan…) / BeEmpty / NotBeEmpty / Contain / ContainSingle / OnlyContain / AllSatisfy / Equal / BeSubsetOf / OnlyHaveUniqueItems / BeInAscendingOrder`
- **Dictionary:** `ContainKey / ContainValue`
- **Enum:** `HaveFlag / NotHaveFlag`
- **Action / async:** `Throw<T>().WithMessage(...) / NotThrow / ThrowAsync<T>() / NotThrowAsync`
- **Time helpers:** `10.Seconds()`, `200.Milliseconds()`, `1.5.Minutes()`

`AssertionScope` collects failures and throws on dispose. All failures throw `AssertionException` (stream expectations throw the derived `ObservableAssertionException`).

---

## 9. Test as If You Were Inside an Activity

Production work runs on an **activity hub** — its own sandbox with its own `AccessContext`, single-threaded action block, and `Status` / `RequestedStatus` lifecycle (see [Activity Control Plane](/Doc/Architecture/ActivityControlPlane)). A test that calls an internal method directly on the test thread skips that context and can pass while production fails — the recurring `AccessContext`-propagation bug follows exactly this pattern.

Drive the work the way production does: **set the control property, observe the result reactively**.

```csharp
await workspace.GetMeshNodeStream(activityPath)
    .Update(node => node with { Content = ((ActivityLog)node.Content) with { RequestedStatus = ActivityStatus.Running } })
    .Should().Emit();

await workspace.GetMeshNodeStream(activityPath)
    .Select(n => (ActivityLog)n.Content)
    .Should().Match(a => a.Status == ActivityStatus.Succeeded);
```

This exercises the real control plane: the owning hub's watcher reacts to `RequestedStatus`, runs the work under the activity's identity, and writes `Status` back — exactly the path production takes.

---

## 10. Extending the Library

The library lives in `src/MeshWeaver.Reactive.Assertions` (`System.Reactive` only) — the observable surface in `ObservableAssertions.cs`, the value surface in `ObjectAssertions.cs` / `CollectionAssertions.cs` / `MoreAssertions.cs`, and the equivalency + JSON helpers in `Equivalency.cs` / `JsonAssertions.cs`.

If a genuinely missing assertion is blocking a test, **add it to the library with a unit test in `test/MeshWeaver.Reactive.Assertions.Test` that exercises both the pass and the fail path** — do not work around it with a hand-rolled `.FirstAsync().ToTask()` in the test body.

---

## See Also

- [Writing Tests](/Doc/Architecture/WritingTests) — surrounding rules: CQRS-correct reads, the init-ping nuance, Orleans clients
- [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls) — why nothing in hub-reachable code is `async`
- [Activity Control Plane](/Doc/Architecture/ActivityControlPlane) — operations as content patches on an activity node
- `src/MeshWeaver.Reactive.Assertions/ObservableAssertions.cs` — the `Emit / Match / Be / Complete / NotEmit / Within` implementation
