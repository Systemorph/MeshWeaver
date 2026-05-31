---
Name: Reactive Test Assertions
Category: Architecture
Description: The MeshWeaver.Reactive.Assertions surface — test bodies assert on observables directly (void methods, no await). Covers the API, the deadlock rule that makes blocking assertions safe, the catalogue of legitimately-async cases, error assertions via Materialize, and the equivalency/JSON helpers.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M22 11.08V12a10 10 0 1 1-5.93-9.14"/><polyline points="22 4 12 14.01 9 11.01"/></svg>
---

# Reactive Test Assertions

MeshWeaver runs reactive end-to-end: services, handlers, layout areas, and activities return `IObservable<T>` and never `await` (see [Asynchronous Calls](AsynchronousCalls)). Its tests are the role model — they **assert on the stream directly, with no `await`**. Every test asserts through `MeshWeaver.Reactive.Assertions`, a self-contained FluentAssertions-shaped surface wired in as a **global `using` + project reference** in `test/Directory.Build.props`. The names and chaining (`.And` / `.Which`, trailing `because` args) are familiar; what's different is that the **observable assertions block *inside* the assertion**, so test bodies stay synchronous (`void`).

For the surrounding test-writing rules see [Writing Tests](WritingTests).

## 1. The observable surface

`obs.Should()` returns an `ObservableAssertions<T>`. Each terminal method subscribes, waits up to the timeout (default 10 s) for the emission you describe, asserts, and **returns the matched value**.

| Member | Behaviour |
|---|---|
| `obs.Should()` / `obs.Should(timeout)` | Begin an assertion (default timeout 10 s). |
| `.Within(timeout)` | Override the wait for the rest of the chain. |
| `.Emit(because?)` | Block for the first emission; return it. Fails on timeout or empty completion. |
| `.Match(x => pred, because?)` | Block for the first emission satisfying `pred`; return it. **The workhorse** — fold the assertion into the predicate. |
| `.Be(expected, because?)` | First emission equals `expected`. |
| `.Complete(because?)` | Stream completes within the timeout (no value required). |
| `.NotEmit(within: t, because?)` | Nothing arrives in `t` — the **one** place a fixed wait is correct. Keep `t` short. |

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

`.Emit()` / `.Match()` **return** the matched value, so `var x = obs.Should().Match(...)` replaces `var x = await obs....ToTask()` one-for-one.

> **The blocking wait cannot deadlock by itself.** Internally it is a synchronous `Subscribe` + `ManualResetEventSlim` — it never captures a SynchronizationContext. A synchronous-replay source delivers during `Subscribe` and the wait returns instantly; an async source blocks until the emission or the timeout. The wait is the single concession to synchronicity, and it lives in the assertion, never the test body.

## 2. The rule that makes it safe: blocking assertion ⇒ `void` method

A reactive `.Should().Emit()/.Match()/.Be()` **blocks the calling thread**. If that thread is xUnit's async SynchronizationContext (because the method is `async Task`), the hub continuation that would deliver the emission is queued behind the block on that same context → **deadlock**, and the assertion times out.

> **A test method that uses a blocking reactive assertion is `void`, with every `await` removed.**

Removing the awaits means:
- stream waits → `obs.Should().Match(...)`;
- observable-returning calls (`IMeshService.CreateNode/UpdateNode/DeleteNode`, `hub.Observe(...)`, `ReadNode`) → `....Should().Emit()` — the subscribe *is* the work, since these are **cold**;
- request/response polling loops → `Observable.Interval(...).SelectMany(...).Should().Match(...)`;
- `var ct = …CancellationToken…` declarations → deleted (the assertion's `.Within(t)` is the deadline).

`hub.Observe(...)` and the `IMeshService` write methods return `IObservable<T>`, **not** `Task<T>` — `await`-ing one looks like a Task await but isn't (Rx's awaiter yields the last value). These count as awaits to remove.

A hand-rolled `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` in a test body is the same smell from the other direction — it can deadlock and bypasses the safe bridge. Always go through `.Should()`.

## 3. Methods that legitimately stay `async`

Not everything converts. A method stays `async Task` **only when it does not use a blocking reactive assertion** and one of these holds:

- **Stream mocks.** A fake `IChatClient` (`GetStreamingResponseAsync`/`GetResponseAsync` with `await Task.Delay`/`Task.Yield` between chunks), a fake `IAsyncEnumerable` (`await Task.CompletedTask; yield break;`). They implement async interfaces — leave them async; they're infrastructure, not the test body.
- **Async system-under-test.** The test drives a genuinely async API: an `await foreach` over the SUT's `IAsyncEnumerable`, a parser's `ParseAsync`, an ASP.NET `middleware.InvokeAsync(context)`, a `controller.ExchangeToken(...)`. The async *is* what's verified.
- **Genuine file / network / process I/O.** `File.ReadAllTextAsync`, `StreamReader.ReadToEndAsync`, `Process.WaitForExitAsync`, an HTTP handler.
- **Concurrency as the SUT.** `Task.WhenAll` / `WaitAsync` where the in-flight concurrency or a deadlock-repro is the thing under test.
- **Pump-starvation cases.** A wait whose predicate calls `.Result` on an async method (e.g. `GetOrderedAgentsAsync().Result`) that needs the hub pump to progress: a blocking assertion would starve the pump; the `await` yields so it runs. If a blocking conversion times out for this reason, leave it async and note it.

**Bridging a genuine `Task<T>`/`ValueTask<T>` SDK boundary into the reactive surface** (so the body stays `void`): `call(...).AsTask().ToObservable().Should().Within(t).Emit()` (`using System.Reactive.Threading.Tasks;`). This is for a *real* async boundary (e.g. `AIFunction.InvokeAsync`) — not for the mesh's own observable-returning methods, which already are observables.

## 4. Asserting an expected error — `.Materialize()`

`.Should().Emit()/.Match()` wrap an OnError in a `MeshWeaverAssertionException` — they do **not** rethrow the original type. To assert a stream *errors with a specific type*, fold OnError into a value with `.Materialize()`:

```csharp
var error = source.Take(1).Materialize()
    .Should().Within(5.Seconds()).Match(n => n.Kind == NotificationKind.OnError);
error.Exception.Should().BeOfType<DeliveryFailureException>();
```

`using System.Reactive;` provides `NotificationKind`. This is the reactive replacement for `await act.Should().ThrowAsync<T>()`. For a **synchronous** throwing call, the FA-shaped `((Action)(() => …)).Should().Throw<T>().WithMessage("*…*")` still applies.

## 5. Fold the assertion into the predicate (no "wait, then assert")

`.Match(items => items.Count == 2)` waits for the *right* state, so there is no separate "wait, then assert" window — the classic source of "passes locally, flakes in CI". Never take the first emission unconditionally; a synced/multi-query stream's first emission may carry only one upstream's partial result. If items trickle in (`Added` after a short `Initial`), accumulate: `.Scan(...)` then `.Should().Match(acc => acc.Count == N)`.

`.NotEmit(within)` is the only fixed wait — a "nothing should happen" test has no positive signal to wait for; keep the window short.

## 6. `BeEquivalentTo` — pass the owning hub's `JsonSerializerOptions`

Equivalence serializes both sides with System.Text.Json, so the polymorphic `$type` discriminators must line up — the options **must come from the owning hub** (this deliberately routes equivalence through the real serializer):

```csharp
actual.Should().BeEquivalentTo(expected, hub.JsonSerializerOptions);
actual.Should().BeEquivalentTo(expected, hub.JsonSerializerOptions, o => o.Excluding(x => x.Message));
```

Find the options on whichever hub the test has: `hub.JsonSerializerOptions`, `Mesh.JsonSerializerOptions`, `GetClient().JsonSerializerOptions`. For a **plain DTO with no hub in scope** (a parser-result record, no polymorphism) use the BCL `JsonSerializerOptions.Default` — don't `new()` one up. The builder is FA-shaped (`Excluding`, `Including`, `WithStrictOrdering()` — collections compare order-*insensitively* without it) plus JSON-flavoured `ExcludeTypeDiscriminator()`, `ExcludeProperty<TDecl,TProp>(...)`, `UsingJson(...)`. `NotBeEquivalentTo` takes the same shape.

## 7. JSON-element asserts

```csharp
var root = serialized.Should().NotBeNull().And.BeValidJson().Which;   // string -> JsonNode
root.Should().HaveElement("message").Which
    .Should().HaveElement("$type").Which
    .Should().HaveValue(typeof(SubscribeRequest).FullName);
```

`BeValidJson()` (string), `HaveElement(name)` / `HaveValue(text)` (`JsonNode`), and `.As<T>()` are provided.

## 8. The FA-shaped value surface (unchanged)

Same names, chaining, and `because` args you already know:

- **Object:** `Be / NotBe / BeNull / NotBeNull / BeSameAs / NotBeSameAs / BeOfType<T>() / BeAssignableTo<T>`
- **Boolean:** `BeTrue / BeFalse`
- **String:** `Contain / NotContain / StartWith / EndWith / Match / MatchRegex / BeEmpty / NotBeNullOrEmpty / NotBeNullOrWhiteSpace / HaveLength`
- **Comparable:** `BeGreaterThan(OrEqualTo) / BeLessThan(OrEqualTo) / BeInRange / BePositive / BeAfter / BeBefore`
- **Collection:** `HaveCount(GreaterThan…) / BeEmpty / NotBeEmpty / Contain / ContainSingle / OnlyContain / AllSatisfy / Equal / BeSubsetOf / OnlyHaveUniqueItems / BeInAscendingOrder`
- **Dictionary:** `ContainKey / ContainValue` · **Enum:** `HaveFlag / NotHaveFlag`
- **Action / async:** `Throw<T>().WithMessage(...) / NotThrow / ThrowAsync<T>() / NotThrowAsync`
- **Time helpers:** `10.Seconds()`, `200.Milliseconds()`, `1.5.Minutes()`

`AssertionScope` collects failures and throws on dispose; all failures throw `MeshWeaverAssertionException`.

## 9. Test as if you were inside an activity

Production work runs on an **activity hub** — its own sandbox with its own `AccessContext`, single-threaded action block, and `Status`/`RequestedStatus` lifecycle (see [Activity Control Plane](ActivityControlPlane)). A test that calls an internal method directly on the test thread skips that context and can pass while production fails (the recurring AccessContext-propagation bug). Drive the work the way production does — **set the control property, observe the result reactively**:

```csharp
workspace.GetMeshNodeStream(activityPath)
    .Update(node => node with { Content = ((ActivityLog)node.Content) with { RequestedStatus = ActivityStatus.Running } })
    .Should().Emit();

workspace.GetMeshNodeStream(activityPath)
    .Select(n => (ActivityLog)n.Content)
    .Should().Match(a => a.Status == ActivityStatus.Succeeded);
```

This exercises the real control plane: the owning hub's watcher reacts to `RequestedStatus`, runs the work under the activity's identity, and writes `Status` back — exactly the path production takes.

## 10. Extending the library

The library lives in `src/MeshWeaver.Reactive.Assertions` (core; System.Reactive only) and `src/MeshWeaver.Reactive.Assertions.Json` (equivalency + JSON). If a genuinely-missing assertion blocks a test, **add it to the library with a unit test in `test/MeshWeaver.Reactive.Assertions.Test` that exercises both the pass and the fail path** — don't work around it with `await` in the test body.

## See also

- [Writing Tests](WritingTests) — the surrounding rules (void bodies, CQRS-correct reads, the init-ping nuance, Orleans clients)
- [Asynchronous Calls](AsynchronousCalls) — why nothing in hub-reachable code is `async`
- [Activity Control Plane](ActivityControlPlane) — operations as content patches on an activity
- `src/MeshWeaver.Reactive.Assertions/ObservableAssertions.cs` — the `Emit/Match/Be/Complete/NotEmit/Within` implementation
