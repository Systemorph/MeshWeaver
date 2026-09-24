# A bounded fan-out must not recurse: `MergeBounded`, never a bare `Merge(n)`

Rx's `Merge(maxConcurrent)` is the platform's bounded fan-out: an export's per-node lookups, a
recursive delete's pre-validation, a static-repo import's upserts, a GitHub sync's blob reads. It
keeps the inners beyond the bound in a queue — and when an inner **completes**, it subscribes the
next queued inner **inline**, inside that inner's `OnCompleted`, on the same stack.

That is harmless while inners complete later, on another turn. It is **unbounded recursion** the
moment queued inners complete *synchronously during `Subscribe`*: each completion subscribes the
next, whose completion subscribes the next, so the stack depth is proportional to the length of the
queue. Measured in `BoundedMergeStackDepthTest`: about **58 frames per queued leg** for the export's
lookup shape — 1,363 frames behind a queue of 20, 8,903 behind a queue of 150. A thread-pool thread
runs out of stack at a few thousand queued legs, and a `StackOverflowException` cannot be caught:
the process dies.

## Inners complete synchronously more often than the happy path suggests

- **A request from a hub that is shutting down.** `MessageHub.GetOrAddResponseSubject` hands out an
  `AsyncSubject` that is *already faulted* once the hub has reached `ShutDown` — so a
  `hub.Observe(…)` issued during a roll terminates the moment it is subscribed.
- **A classifying `.Catch(… => Observable.Return(fallback))`** turns that fault into an immediate
  value and completion — which is exactly the tolerant shape a per-item lookup should have.
- **A warm cache** answers during `Subscribe`.

## The production crash (#5649)

memex-cloud, pod `…-v4txp`, 06:56:01Z on 2026-09-24, during a roll (the sibling pod `…-5hflx` died
the same way at 06:49:48Z). `createdump` reported `Unwind: exception type
System.StackOverflowException`. The runtime's stderr trace, read with a `Logs` action filtered to
`stream="stderr"`, shows one `MessageHub.HandleCallbacks` at the bottom (under
`MessageService.DrainLoop`) and above it a block that repeats until the stack ran out:

```
Merge.ObservablesMaxConcurrency.Inner  →  Select (ValueTuple)  →  CombineLatest
  →  Catch  →  Select  →  Timeout  →  Take  →  Select  →  Defer / Finally / Do
  →  MessageHub.WrapWithCancelOnDispose  →  AsyncSubject (already terminated)
  →  Timeout  →  Catch  →  ReturnImmediate  →  CombineLatest.SecondObserver  →  Merge.Inner  → …
```

That is, frame for frame, `MeshOperations.GetNodeCollectionConfigs` — two `ReadHub.Observe(new
GetDataRequest(…))` legs, each `.Take(1).Timeout(…).Select(…).Catch(→ Return)`, combined by
`CombineLatest` and projected to `(NodePath, Configs)` — running under
`Merge(NodeCopyHelper.DefaultBatchSize)` for an MCP `export` of `Ops/Logs` (~2,200 nodes) — the
MCP call dropped seconds before the replica died. The first batch of lookups was answered normally
(the `HandleCallbacks`); by then the replica was shutting down, every queued lookup's response subject was already faulted, and the
dequeue chain recursed through the rest of the queue.

The `…-5hflx` trace (06:49:47Z) carries the same repeating `MessageHub.WrapWithCancelOnDispose`
frame on stderr; on `…-v4txp` a read of the `Merge.ObservablesMaxConcurrency` frames alone was cut at
its limit of 50, so the recursion was at least that many levels deep. What this does NOT establish:
the exact depth, or that the export was the only bounded fan-out recursing at the time. The
`MergeBounded` fix covers every bounded fan-out in `src/` and `memex/`, so it does not depend on
which one it was.

## The fix: `MergeBounded`

`MeshWeaver.Messaging.BoundedMergeExtensions.MergeBounded(n)` is `Merge(n)` with each inner
subscribed through `Scheduler.CurrentThread` — the trampoline. If a trampoline is already running
on the thread (an inner completing synchronously inside another inner's subscription is exactly that
case), the next subscription is queued on it and runs after the current frame unwinds; otherwise the
trampoline starts and runs it immediately. The next inner is therefore always subscribed from the
trampoline's loop, never from inside the previous inner's `OnCompleted`, so the depth no longer
depends on the queue. The bound, the start order and every value, error and completion are
unchanged, nothing hops to another thread, and the subscription also leaves `Merge`'s internal gate,
which is where the inline dequeue used to run it.

```csharp
// ❌ recurses once per queued inner when inners complete synchronously
nodes.Select(n => Lookup(n)).ToObservable().Merge(NodeCopyHelper.DefaultBatchSize)

// ✅ same bound, flat stack
nodes.Select(n => Lookup(n)).MergeBounded(NodeCopyHelper.DefaultBatchSize)
```

Rx's other fan-in operators are not affected: both `Concat` overloads already drain through a
trampoline, and an unbounded `Merge()` / `SelectMany` keeps no queue.

## Enforcement

- `BoundedMergeStackDepthTest` (MeshWeaver.Messaging.Hub.Test) measures the stack depth at which the
  last queued leg is subscribed, for a short and a long queue, with the export's leg shape (a
  pre-faulted response subject). With `MergeBounded` the two are equal; with the bare operator
  (negative control) the long queue is ~7,500 frames deeper. The long queue is kept well below the
  overflow point, because a `StackOverflowException` would kill the test host instead of failing
  the test.
- `MergeBoundedRatchetGuard` (MeshWeaver.Documentation.Test) holds `src/` and `memex/` at zero bare
  `Merge(n)` / `Observable.Merge(xs, n)` calls whose bound is a literal or a bound-named identifier.
  It reads text, so a bound held in a variable with an unrelated name is not recognised — a floor,
  not a proof.

Satellite repositories (MeshWeaver.Plugins and the node repos) carry their own `Merge(n)` sites and
are not covered by the guard; they can adopt `MergeBounded` once a sealed platform set carries it.

## Related

- [Debugging Native Crashes](../DebuggingNativeCrashes) — reading a crash's exit code and dump
  before naming a cause.
- [The Query Fan-In's Stall Terminal](../QueryFanInStallTerminal) — the other process-killing
  crash class read the same morning: a terminal that reached a subscriber with no error arm.
- [Removing Hand-Woven Concurrency Gates](../RemovingHandWovenGates) — why the bound is Rx's
  operator and never a semaphore.
