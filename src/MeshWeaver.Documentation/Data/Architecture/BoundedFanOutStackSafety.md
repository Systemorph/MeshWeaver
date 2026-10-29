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

The public instance, pod `…-v4txp`, 06:56:01Z on 2026-09-24, during a roll (the sibling pod `…-5hflx` died
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

`MeshWeaver.Messaging.BoundedMergeExtensions.MergeBounded(n)` is `Merge(n)` with the recursion
replaced by a **drain loop** — the work-in-progress loop Rx's own `Concat` uses. Starting the next
inner is one loop per subscription, entered by whichever signal made a slot or an inner available
(an outer `OnNext`, an inner's completion, the outer's completion). A signal that arrives while that
loop is already running — an inner completing synchronously inside the subscription the loop is
making is exactly that case — only records that there is more to do and returns; the running loop
takes it on its next iteration. The next inner is therefore always subscribed from the loop's frame,
never from inside the previous inner's `OnCompleted`, so the depth no longer depends on the queue.

Everything else is `Merge`'s: at most `n` inners are subscribed at once, inners start in arrival
order, an inner that fits the bound is subscribed **inline** on the signal that delivered it (no
scheduler, no thread hop — so an outer that emits an inner and then faults in the same turn still has
that inner's synchronous values delivered before the fault), values are forwarded serialised, the
first error terminates everything, and the sequence completes when the outer and every inner have.

A first version subscribed each inner through `Scheduler.CurrentThread` instead. Review caught what
that costs: inside a running trampoline every inner subscription is DEFERRED, so an outer that emits
an inner and faults in the same turn drops that inner's values, and a long-running outer on the
trampoline starves the inners. `AnInnerEmittedBeforeAnOuterFault_DeliversItsValuesFirst` pins it.

The `IEnumerable<IObservable<T>>` overload also enumerates inline. Converting an enumerable with
Rx's `ToObservable()` can defer the enumeration when called from an already-running current-thread
trampoline; a caller that subscribes and disposes in that turn would then drop the whole fan-out.
The enumerable overload feeds each item into the same drain loop directly, preserving the
synchronous start behavior of `Merge(IEnumerable, n)` without reintroducing recursive queued
subscriptions. `TheEnumerableOverload_EnumeratesInlineInsideARunningTrampoline` pins this contract.

```csharp
// ❌ recurses once per queued inner when inners complete synchronously
nodes.Select(n => Lookup(n)).ToObservable().Merge(NodeCopyHelper.DefaultBatchSize)

// ✅ same bound, flat stack
nodes.Select(n => Lookup(n)).MergeBounded(NodeCopyHelper.DefaultBatchSize)
```

Rx's other fan-in operators are not affected: both `Concat` overloads already drain through a loop
or a trampoline, and an unbounded `Merge()` / `SelectMany` keeps no queue.

## A bound over legs that already posted bounds nothing

`MergeBounded(n)` limits how many inners are **subscribed** at once. It limits how many requests
are **in flight** only when each inner does its work on Subscribe. `IMessageHub.Observe(request)`
does not: it registers the response subject and **posts at the call**. So a fan-out built as
`items.Select(x => hub.Observe(new XRequest(x))).ToObservable().MergeBounded(n)` (or with a
`.ToArray()`) posts EVERY request the moment the legs are built — and the operator enumerates its
source inline, so "built" means "as soon as the merge starts". The bound then limits only how many
ANSWERS are awaited.

That is the shape behind [MeshWeaver#7464](https://github.com/Systemorph/MeshWeaver/issues/7464):
the content indexer's part writer (`MeshDocumentSink.WriteParts`, MeshWeaver.Plugins) put one
extracted zip's ~9,400 part creates on the node-operations hub at once, and 7,875 of them reached no
verdict within their 30 s deadline, all at stage `write`. The sink was fixed in
MeshWeaver.Plugins#4566 by building each leg from the cold `IMeshService.CreateOrUpdateNode`.
`NodeCopyHelper` carried the same shape for every level of a copied subtree (a document's parts
all sit on one level); each leg is now wrapped in `Observable.Defer`.

**The rule:** a leg handed to a bounded merge must be **cold** — `IMeshService.CreateNode` /
`CreateOrUpdateNode` (already `Observable.Defer`), or `Observable.Defer(() => hub.Observe(…))`.
Never call `hub.Observe(request)` while building the legs. The bound is right; raising or lowering
it changes nothing while the posts escape it.

`CopyWritesAreBoundedTest` (MeshWeaver.Graph.Test) pins the copy: a creation validator parks every
create under the copy's target, the peak must reach the bound, must not exceed it over a negative
window, and every node must land once released. On the unfixed helper it fails with *"5 of 24
sibling creates were in flight together"* at a bound of 4.

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

## Production verification (2026-09-27)

`Ops/Status/<public-instance>`, sampled at 10:57:54Z, reports `notScraped: false`, one generation,
`converged: true`, and three of three replicas ready on `3.0.0-ci.9443`, core
`311108bf16a70eee39a6618b36456cf82c58a85f`. GitHub's compare of the #5651 merge
`af2d799301` to that commit reports ahead 253, behind zero: every sampled replica carries the fix.
The replicas started at 06:15:30Z, 06:16:52Z and 06:18:14Z.

One MCP `export @Ops/Logs` against the public instance completed at 11:04:07Z. Its ZIP contains
`manifest.json` with **18,428 nodes**, root `Ops/Logs`, and no content-collection files; the ZIP
CRC check reports no bad entry. This exercises the large export path on the fixed image, at a
larger node count than the original approximately 2,200-node trigger. It was not deliberately
overlapped with a shutdown; the deterministic pre-faulted-subject test above covers that edge.

Read-only Logs actions on the control instance provide the runtime check:

- `Ops/Actions/verify-5649-public-20260927-unwind`: the previous 1,440 minutes,
  `Unwind: exception type|Stack overflow|Gathering state for process`, **zero** records,
  `Done`, no truncation and no landing failures.
- `Ops/Actions/verify-5649-public-20260927-startup`: the same window's positive control,
  `\[PlatformStartup\]`, **121 read and 121 persisted**, zero failures, no truncation.
- `Ops/Actions/verify-5649-public-20260927-postexport-unwind`: the same unwind predicate over
  ten minutes, completed at 11:05:14Z, **zero** records, no truncation and no landing failures.

This verifies #5649's core crash fix after convergence and a large production export. It does not
establish that a satellite's remaining bare bounded merges are safe; those require their own
conversion and guard.

## Related

- [Debugging Native Crashes](../DebuggingNativeCrashes) — reading a crash's exit code and dump
  before naming a cause.
- [The Query Fan-In's Stall Terminal](../QueryFanInStallTerminal) — the other process-killing
  crash class read the same morning: a terminal that reached a subscriber with no error arm.
- [Removing Hand-Woven Concurrency Gates](../RemovingHandWovenGates) — why the bound is Rx's
  operator and never a semaphore.
