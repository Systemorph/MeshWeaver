---
Name: Reactive Test Assertions
Category: Architecture
Description: The MeshWeaver.Reactive.Assertions surface — assert on observables directly from void test methods, with no await. Covers the full API, the deadlock rule that makes blocking assertions safe, legitimately-async cases, error assertions via Materialize, and the equivalency/JSON helpers.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M22 11.08V12a10 10 0 1 1-5.93-9.14"/><polyline points="22 4 12 14.01 9 11.01"/></svg>
---

# Reactive Test Assertions

MeshWeaver is reactive end-to-end: services, handlers, layout areas, and activities return `IObservable<T>` and never `await` (see [Asynchronous Calls](AsynchronousCalls)). Its tests follow the same principle — **you assert on the stream directly, with no `await`**.

All test assertions flow through `MeshWeaver.Reactive.Assertions`, a FluentAssertions-shaped library wired in via a global `using` and project reference in `test/Directory.Build.props`. The names and chaining patterns (`.And` / `.Which`, trailing `because` args) are familiar. What is different is that the **observable assertions block inside the assertion itself**, so test methods stay synchronous (`void`).

For the surrounding test-writing rules, see [Writing Tests](WritingTests).

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
  <text x="380" y="24" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="bold" fill="currentColor" fill-opacity=".85">Blocking Reactive Assertion — Safe vs. Deadlock</text>
  <rect x="10" y="44" width="740" height="118" rx="10" fill="none" stroke="#43a047" stroke-width="1.5" stroke-opacity=".45"/>
  <text x="20" y="62" font-family="sans-serif" font-size="11" fill="#43a047" fill-opacity=".9" font-weight="bold">✓  void test method (safe)</text>
  <rect x="30" y="72" width="130" height="38" rx="8" fill="#1e88e5"/>
  <text x="95" y="87" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff" font-weight="bold">void Test()</text>
  <text x="95" y="101" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#fff">xUnit thread pool</text>
  <line x1="160" y1="91" x2="208" y2="91" stroke="#43a047" stroke-width="1.5" marker-end="url(#arr-grn)"/>
  <rect x="210" y="72" width="160" height="38" rx="8" fill="#5c6bc0"/>
  <text x="290" y="87" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff" font-weight="bold">.Should().Match(…)</text>
  <text x="290" y="101" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#fff">Subscribe + ManualResetEvent</text>
  <line x1="370" y1="91" x2="418" y2="91" stroke="#43a047" stroke-width="1.5" marker-end="url(#arr-grn)"/>
  <rect x="420" y="72" width="150" height="38" rx="8" fill="#26a69a"/>
  <text x="495" y="87" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff" font-weight="bold">Hub delivers emission</text>
  <text x="495" y="101" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#fff">on its action block</text>
  <line x1="570" y1="91" x2="618" y2="91" stroke="#43a047" stroke-width="1.5" marker-end="url(#arr-grn)"/>
  <rect x="620" y="72" width="118" height="38" rx="8" fill="#43a047"/>
  <text x="679" y="87" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff" font-weight="bold">Event set →</text>
  <text x="679" y="101" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#fff">assertion passes</text>
  <rect x="210" y="118" width="160" height="22" rx="6" fill="none" stroke="#43a047" stroke-width="1" stroke-opacity=".5" stroke-dasharray="4,3"/>
  <text x="290" y="133" text-anchor="middle" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".65">blocks caller thread only</text>
  <rect x="10" y="176" width="740" height="118" rx="10" fill="none" stroke="#e53935" stroke-width="1.5" stroke-opacity=".45"/>
  <text x="20" y="194" font-family="sans-serif" font-size="11" fill="#e53935" fill-opacity=".9" font-weight="bold">✗  async Task method (deadlock)</text>
  <rect x="30" y="204" width="130" height="38" rx="8" fill="#e53935"/>
  <text x="95" y="219" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff" font-weight="bold">async Task Test()</text>
  <text x="95" y="233" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#fff">xUnit SynchronizationContext</text>
  <line x1="160" y1="223" x2="208" y2="223" stroke="#e53935" stroke-width="1.5" marker-end="url(#arr-red)"/>
  <rect x="210" y="204" width="160" height="38" rx="8" fill="#5c6bc0"/>
  <text x="290" y="219" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff" font-weight="bold">.Should().Match(…)</text>
  <text x="290" y="233" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#fff">blocks the SyncContext thread</text>
  <line x1="370" y1="223" x2="418" y2="223" stroke="#e53935" stroke-width="1.5" marker-end="url(#arr-red)"/>
  <rect x="420" y="204" width="150" height="38" rx="8" fill="#8e24aa"/>
  <text x="495" y="219" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff" font-weight="bold">Hub continuation</text>
  <text x="495" y="233" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#fff">queued on same context</text>
  <line x1="570" y1="223" x2="618" y2="223" stroke="#e53935" stroke-width="1.5" marker-end="url(#arr-red)"/>
  <rect x="620" y="204" width="118" height="38" rx="8" fill="#e53935"/>
  <text x="679" y="219" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff" font-weight="bold">DEADLOCK →</text>
  <text x="679" y="233" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#fff">timeout</text>
  <rect x="210" y="249" width="160" height="22" rx="6" fill="none" stroke="#e53935" stroke-width="1" stroke-opacity=".5" stroke-dasharray="4,3"/>
  <text x="290" y="264" text-anchor="middle" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".65">context blocked, can't deliver</text>
  <path d="M570 223 Q600 286 210 270 Q185 270 185 249" fill="none" stroke="#e53935" stroke-width="1.5" stroke-dasharray="5,3" stroke-opacity=".6" marker-end="url(#arr-red)"/>
  <text x="390" y="302" text-anchor="middle" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".5" font-style="italic">hub queues behind the blocked context — emission never arrives</text>
</svg>

*Blocking assertions are safe only in `void` test methods: the hub delivers on its own action block while the test thread waits on `ManualResetEventSlim`. An `async Task` method captures xUnit's `SynchronizationContext` — the hub continuation queues behind the blocked context, causing a deadlock.*

---

## 1. The Observable Assertion Surface

`obs.Should()` returns an `ObservableAssertions<T>`. Each terminal method subscribes, waits up to the configured timeout (default 10 s) for the emission you describe, asserts the result, and **returns the matched value** so you can chain further assertions.

| Member | Behaviour |
|---|---|
| `obs.Should()` / `obs.Should(timeout)` | Begin an assertion chain (default timeout 10 s). |
| `.Within(timeout)` | Override the wait deadline for the rest of the chain. |
| `.Emit(because?)` | Block for the first emission; return it. Fails on timeout or empty completion. |
| `.Match(x => pred, because?)` | Block for the first emission satisfying `pred`; return it. **The workhorse** — fold the assertion into the predicate. |
| `.Be(expected, because?)` | First emission must equal `expected`. |
| `.Complete(because?)` | Stream must complete within the timeout (no value required). |
| `.NotEmit(within: t, because?)` | Nothing must arrive within `t` — the **one** place a fixed wait is correct. Keep `t` short. |

```csharp
[Fact]
public void ObserveQuery_EmitsInitialResults()                  // ← void, no Task, no await
{
    var p = P();
    // Cold observables: the assertion's Subscribe IS the write.
    NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project1") with { NodeType = "Markdown" }).Should().Emit();
    NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project2") with { NodeType = "Markdown" }).Should().Emit();

    // Fold the assertion into the predicate: wait for the snapshot that has both items.
    var changes = ObserveAccumulated($"path:{p} nodeType:Markdown scope:descendants")
        .Should().Within(WaitTimeout).Match(acc => acc.Count >= 1 && acc[0].Items.Count >= 2);
    changes[0].ChangeType.Should().Be(QueryChangeType.Initial);
}
```

`.Emit()` and `.Match()` **return** the matched value, so `var x = obs.Should().Match(...)` replaces `var x = await obs....ToTask()` one-for-one.

> **The blocking wait cannot deadlock by itself.** Internally it is a synchronous `Subscribe` combined with a `ManualResetEventSlim` — it never captures a `SynchronizationContext`. A synchronous-replay source delivers its value during `Subscribe` and the wait returns instantly. An async source blocks until the emission or the timeout fires. The wait is the single concession to synchronicity, and it lives in the assertion, never the test body.

---

## 2. The Rule That Makes It Safe: Blocking Assertion ⇒ `void` Method

A reactive `.Should().Emit()` / `.Match()` / `.Be()` **blocks the calling thread**. If that thread is xUnit's async `SynchronizationContext` (because the method is `async Task`), any hub continuation that would deliver the awaited emission is queued behind the block on that same context — which produces an inescapable **deadlock**, causing the assertion to time out.

> **A test method that uses a blocking reactive assertion must be `void`, with every `await` removed.**

In practice, removing awaits means the following substitutions:

- Stream waits → `obs.Should().Match(...)`
- Cold observable-returning calls (`IMeshService.CreateNode/UpdateNode/DeleteNode`, `hub.Observe(...)`, `ReadNode`) → `.Should().Emit()` — the subscribe *is* the work
- Request/response polling loops → `Observable.Interval(...).SelectMany(...).Should().Match(...)`
- `CancellationToken` declarations → deleted (the assertion's `.Within(t)` is the deadline)

`hub.Observe(...)` and the `IMeshService` write methods return `IObservable<T>`, **not** `Task<T>`. Awaiting one looks like a normal `Task` await but isn't — Rx's awaiter yields the last value. These count as awaits to remove.

A hand-rolled `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` in a test body is the same smell from the other direction — it can deadlock and bypasses the safe bridge. Always go through `.Should()`.

---

## 3. Methods That Legitimately Stay `async`

Not everything converts. A method stays `async Task` **only when it does not use a blocking reactive assertion** and one of these cases applies:

- **Stream mocks.** A fake `IChatClient` (`GetStreamingResponseAsync` / `GetResponseAsync` with `await Task.Delay` / `Task.Yield` between chunks) or a fake `IAsyncEnumerable` (`await Task.CompletedTask; yield break;`). These implement async interfaces — leave them async; they are infrastructure, not the test body.
- **Async system-under-test.** The test drives a genuinely async API: an `await foreach` over the SUT's `IAsyncEnumerable`, a parser's `ParseAsync`, an ASP.NET middleware's `InvokeAsync(context)`, a controller's `ExchangeToken(...)`. The async *is* what is being verified.
- **Genuine file / network / process I/O.** `File.ReadAllTextAsync`, `StreamReader.ReadToEndAsync`, `Process.WaitForExitAsync`, an HTTP handler.
- **Concurrency as the SUT.** `Task.WhenAll` / `WaitAsync` where the in-flight concurrency or a deadlock reproduction is the thing under test.
- **Pump-starvation cases.** A wait whose predicate calls `.Result` on an async method (e.g. `GetOrderedAgentsAsync().Result`) that needs the hub pump to progress. A blocking assertion would starve the pump; the `await` yields so it can run. If a blocking conversion times out for this reason, leave it async and note it.

**Bridging a genuine `Task<T>` / `ValueTask<T>` SDK boundary into the reactive surface** (so the body stays `void`):

```csharp
call(...).AsTask().ToObservable().Should().Within(t).Emit()
// requires: using System.Reactive.Threading.Tasks;
```

Use this only for a *real* async boundary such as `AIFunction.InvokeAsync` — not for MeshWeaver's own observable-returning methods, which are already observables.

---

## 4. Asserting an Expected Error — `.Materialize()`

`.Should().Emit()` and `.Match()` wrap an `OnError` inside a `MeshWeaverAssertionException` — they do **not** rethrow the original exception type. To assert that a stream errors with a specific type, fold `OnError` into a value using `.Materialize()`:

```csharp
var error = source.Take(1).Materialize()
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
stream.Scan(ImmutableList<T>.Empty, (acc, change) => acc.AddRange(change.Items))
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

The builder is FluentAssertions-shaped (`Excluding`, `Including`, `WithStrictOrdering()` — collections compare order-insensitively without it) plus JSON-flavoured extensions: `ExcludeTypeDiscriminator()`, `ExcludeProperty<TDecl,TProp>(...)`, `UsingJson(...)`. `NotBeEquivalentTo` accepts the same shape.

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

`AssertionScope` collects failures and throws on dispose. All failures throw `MeshWeaverAssertionException`.

---

## 9. Test as If You Were Inside an Activity

Production work runs on an **activity hub** — its own sandbox with its own `AccessContext`, single-threaded action block, and `Status` / `RequestedStatus` lifecycle (see [Activity Control Plane](ActivityControlPlane)). A test that calls an internal method directly on the test thread skips that context and can pass while production fails — the recurring `AccessContext`-propagation bug follows exactly this pattern.

Drive the work the way production does: **set the control property, observe the result reactively**.

```csharp
workspace.GetMeshNodeStream(activityPath)
    .Update(node => node with { Content = ((ActivityLog)node.Content) with { RequestedStatus = ActivityStatus.Running } })
    .Should().Emit();

workspace.GetMeshNodeStream(activityPath)
    .Select(n => (ActivityLog)n.Content)
    .Should().Match(a => a.Status == ActivityStatus.Succeeded);
```

This exercises the real control plane: the owning hub's watcher reacts to `RequestedStatus`, runs the work under the activity's identity, and writes `Status` back — exactly the path production takes.

---

## 10. Extending the Library

The library lives in two projects:

- `src/MeshWeaver.Reactive.Assertions` — core surface; `System.Reactive` only
- `src/MeshWeaver.Reactive.Assertions.Json` — equivalency and JSON helpers

If a genuinely missing assertion is blocking a test, **add it to the library with a unit test in `test/MeshWeaver.Reactive.Assertions.Test` that exercises both the pass and the fail path** — do not work around it with `await` in the test body.

---

## See Also

- [Writing Tests](WritingTests) — surrounding rules: void bodies, CQRS-correct reads, the init-ping nuance, Orleans clients
- [Asynchronous Calls](AsynchronousCalls) — why nothing in hub-reachable code is `async`
- [Activity Control Plane](ActivityControlPlane) — operations as content patches on an activity node
- `src/MeshWeaver.Reactive.Assertions/ObservableAssertions.cs` — the `Emit / Match / Be / Complete / NotEmit / Within` implementation
