---
Name: Reactive Test Assertions
Category: Documentation
Description: Assert on IObservable<T> without await — the no-await, reactive role-model pattern for MeshWeaver tests.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M22 11.08V12a10 10 0 1 1-5.93-9.14"/><polyline points="22 4 12 14.01 9 11.01"/></svg>
---

# Tests are reactive, end-to-end — no `await`

MeshWeaver runs reactive end-to-end: services, handlers, layout areas, and activities return
`IObservable<T>` and never `await` (see [Asynchronous Calls](AsynchronousCalls.md)). A test that
bridges every stream to a `Task` and `await`s it is exercising a shape the platform never uses — and
every `await … .ToTask(ct)` is a place a test can deadlock or race the very condition it means to
observe. **The platform's own tests are the role model: assert on the stream directly, no `await`.**

## The pattern

`MeshWeaver.Reactive.Assertions` gives every `IObservable<T>` a `.Should()` that subscribes, blocks
(up to a timeout) for the relevant emission, and asserts — all without `await`. The blocking wait is
encapsulated in the assertion (it never captures a synchronization context, so it cannot deadlock);
the test body stays declarative and synchronous.

```csharp
using MeshWeaver.Reactive.Assertions;

[Fact]
public void ObserveQuery_EmitsInitialResults()   // ← not async, no Task, no await
{
    var p = P();
    // A reactive create is driven by the assertion's Subscribe — no await needed.
    NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project1") with { NodeType = "Markdown" }).Should().Emit();
    NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project2") with { NodeType = "Markdown" }).Should().Emit();

    // Fold the assertion into the predicate: wait for the snapshot that has both items.
    var changes = ObserveAccumulated($"path:{p} nodeType:Markdown scope:descendants")
        .Should(WaitTimeout)
        .Match(acc => acc.Count >= 1 && acc[0].Items.Count >= 2);

    changes[0].ChangeType.Should().Be(QueryChangeType.Initial);
}
```

Compare the shape it replaces — a method that is `async`, bridges the stream to a `Task`, and
`await`s it, with a separate "wait then assert" window:

```csharp
public async Task ObserveQuery_EmitsInitialResults()
{
    await NodeFactory.CreateNode(...);                       // await on a cold observable
    var changes = await ObserveAccumulated(...)
        .Where(acc => acc.Count >= 1 && acc[0].Items.Count >= 2)
        .FirstAsync().Timeout(WaitTimeout).ToTask(ct);       // bridge + await
    changes.Should().HaveCount(1);
}
```

## API

| Member | Behaviour |
|---|---|
| `obs.Should()` / `obs.Should(timeout)` | Begin an assertion (default timeout 10 s). |
| `.Within(timeout)` | Override the wait for the rest of the chain. |
| `.Emit()` | Block for the first emission; return it for further inspection. |
| `.Match(predicate)` | Block for the first emission satisfying the predicate; return it. **The workhorse** — fold the assertion into the predicate. |
| `.Be(expected)` | Assert the first emission equals `expected`. |
| `.Complete()` | Assert the stream completes within the timeout. |
| `.NotEmit(within)` | Assert nothing arrives within a (short) window. |

## Rules

1. **No `await` in test bodies.** Replace `await x.FirstAsync().ToTask(ct)` with
   `x.Should().Match(…)` / `.Be(…)` / `.Emit()`. A reactive create/update (`CreateNode`,
   `stream.Update(…)`) is driven by the assertion's Subscribe — `CreateNode(…).Should().Emit()`.
   The `[Fact]` method becomes a plain `void`.
2. **Fold the assertion into the predicate.** `.Match(items => items.Count == 2)` waits for the
   *right* state, so there is no separate "wait, then assert" race window — the classic source of
   "passes locally, flakes in CI".
3. **`.NotEmit(within)` is the only fixed wait.** A "nothing should happen" test has no positive
   signal to await; keep the window short. Everything else waits on a real condition.
4. **The wait lives in the assertion, never the test.** A hand-rolled `.Result` / `.Wait()` /
   `.GetAwaiter().GetResult()` in a test body can deadlock and reads as a smell. Always go through
   `.Should()` — its bridge is deadlock-safe by construction.

## Test as if you were inside an activity

Production work runs on an **activity hub** — its own execution sandbox with its own
`AccessContext`, single-threaded action block, and `Status`/`RequestedStatus` lifecycle (see
[Activity Control Plane](ActivityControlPlane.md)). A test that calls APIs directly on the test
thread skips that context, so it can pass while the same code fails in production (the recurring
AccessContext-propagation class of bug).

To be a faithful role model, drive the work the way production does — **set the control property and
observe the result reactively**, rather than invoking an internal method directly:

```csharp
// Drive the operation through its activity the way the UI / an agent would:
workspace.GetMeshNodeStream(activityPath)
    .Update(node => node with { Content = ((ActivityLog)node.Content) with { RequestedStatus = ActivityStatus.Running } })
    .Should().Emit();

// Then assert on the activity's own stream — the same source the GUI databinds to:
workspace.GetMeshNodeStream(activityPath)
    .Select(n => (ActivityLog)n.Content)
    .Should().Match(a => a.Status == ActivityStatus.Succeeded);
```

This exercises the real control plane: the owning hub's watcher reacts to `RequestedStatus`, runs
the work under the activity's identity, and writes `Status` back — exactly the path production takes.

## See also

- [Asynchronous Calls](AsynchronousCalls.md) — why nothing in hub-reachable code is `async`.
- [Activity Control Plane](ActivityControlPlane.md) — operations as content patches on an activity.
- [Writing Tests](WritingTests.md) — the broader testing standards.
