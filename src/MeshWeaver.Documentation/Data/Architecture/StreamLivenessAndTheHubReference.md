---
Name: Stream Liveness and the Hub Reference
Category: Architecture
Description: A synchronization stream outlives the hub it holds, and ISynchronizationStream.Hub is declared non-nullable — so a corpse answers it and 47 call sites dereference it. Why the obvious fix (null the field) is the one that already caused a production NRE, and the three-step order that does not repeat it.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M22 12h-4l-3 9L9 3l-3 9H2"/></svg>
---

# Stream Liveness and the Hub Reference

**A `SynchronizationStream` holds `IMessageHub Hub`, the interface declares it NON-NULLABLE, and a
stream whose owner has been torn down keeps answering it. So the corpse is indistinguishable from a
live stream at every one of the 47 sites that dereference it — and the reference keeps the dead
hub's whole resolved graph alive.**

That is two defects wearing one shape: a **retention** leak, and a **contract** that lies. They have
to be fixed in that order backwards — the contract first — and this page exists because the obvious
order was already tried in production.

## What the field costs while it is held

Closing a DI scope does not null the fields on an object that outlives it. The retained graph is
`stream → dead hub → its own resolved state` (`TypeRegistry`, and whatever else was resolved onto the
hub at construction). `MessageHub` deliberately does not dispose its own `ServiceProvider` —
`HostedHubsCollection.Add` closes the scope instead, being *"the only place that both knows the scope
exists and can act strictly after this hub is terminally down"* — and it does its job: 1 495 of 1 496
hubs were removed. The bytes stay reachable anyway, because reachability is about references, not
scopes. The only way to reclaim them is to **drop the reference**.

`Dispose()` sets `isDisposed`, completes the store, clears `sharedReduceCache` and disposes
`streamDisposables` — but it never clears `Hub`. So even the streams that *are* properly disposed
keep their hub reachable.

## Why "just null the field" is not available

It was already done, and it is recorded in `SynchronizationStream`'s own constructor refusal path.
The predecessor built a DEAD stream — `isDisposed`, store completed, **`Hub = null!`** — and
documented that *"every code path that touches Hub goes through `TryGetActiveHub`"*.

**No consumer honoured that, and none could.** `TryGetActiveHub` was private to that one file, while
~96 sites dereferenced `ISynchronizationStream.Hub`, which the interface declares non-nullable. A
consumer could not even detect the corpse: the contract exposed no liveness member at all.

The result was an NRE inside `LayoutAreaHost`'s constructor (`Stream.Hub.ServiceProvider…`) during
the overlay self-heal recycle window, which escaped to the subscriber as a **terminal
`DeliveryFailure`**. A page that subscribed mid-recycle was told *"this failed forever"* instead of
*"ask again"*.

So the in-file discipline is not what failed. The **contract** failed: it promised non-null to
callers who had no way to ask.

## Detection was never the missing piece

`StreamLiveness.IsUsable` already computes the whole answer, and has since #1455 / #2387:

- it treats `current.Hub is not { } hub` as dead;
- it checks `RunLevel > Started` and `MessageHub { IsDisposing: true }`, because hub shutdown is
  asynchronous and RunLevel still reads `Started` immediately after `Dispose()`;
- it walks the **reduce chain** through `IStreamLivenessSource.Source`, because a reduced stream is
  its parent's SIBLING — `WorkspaceStreams.CreateReducedStream` hosts its `sync/{id}` sub-hub under
  the parent's `Host` and only registers it for disposal ON the parent — so a child happily mirrors
  a source that is already dead;
- it counts FAULTED exactly as much as DISPOSED, because a store is a `ReplaySubject` and a terminal
  `OnError` is permanent under the Rx grammar.

Every cache already refuses to *serve* a corpse. What was missing is letting it **go** — and, before
that, letting a consumer **ask**.

## The three steps, in the only order that works

1. ✅ **Expose the predicate.** `SynchronizationStreamLiveness.IsUsable(stream)` and
   `stream.TryGetHub()` make the existing answer reachable from other assemblies.
2. ✅ **Migrate the dereference sites** onto `TryGetHub()`, which can answer `null`.
3. **Only then** clear `Hub` on disposal — at which point a null hub is a state the contract admits
   rather than a lie. **Not done.**

Doing 3 before 2 is the production NRE, in the order it happened. Doing 2 before 1 is impossible.

### What step 2 actually migrated, and what it deliberately did not

Measured on the branch that did it: **54** occurrences in `src/` where the receiver of `.Hub` is an
`ISynchronizationStream` (excluding comments, and excluding `SynchronizationStream`'s own bare
`Hub` field reads). The header's "47" is the earlier count and is superseded. Of those 54, **28**
were migrated and **26** were deliberately left.

**Every migrated site answers with the "no" its own signature already modelled** — that is the rule,
not a style preference. `GetPatch` returns `null` (its callers already test for it); `GetStream<T>`
returns `Observable.Empty<T>()` (a dead stream's completed store would produce exactly that);
`GetDataBoundValue<T>` returns `default` (the answer it already gives twice for an absent value);
`SubmitModel` returns an `ActivityLog` carrying `activity.dataUpdate.streamClosed`, the same shape
as its existing no-route branch; `ToDataChanged` returns `null`, its declared "nothing to forward".
Where a surface has no absent value, the refusal goes out the channel it does have:
`WorkspaceOperations.UpdateStream` throws its existing `DataException`, and `LayoutAreaHost`'s
constructor and `MarkdownExecutionExtensions.Execute` throw `HubDisposingException` — an
`ObjectDisposedException`, so it classifies as `ErrorType.ShuttingDown`, the transient
"retry, the address may reactivate" answer instead of the terminal `DeliveryFailure` the NRE
produced. **No site swallows.**

The 26 left fall in three groups, and the reason differs per group:

- **Already answerable, or already a liveness check (3).** `LayoutClientExtensions`'
  conversion-failure logger is already written `stream?.Hub?.…`. `Workspace`'s cache probe reads
  `existing.Stream.Hub is { RunLevel: <= Started }` — a null-safe pattern that IS a liveness check;
  replacing it with `IsUsable` would TIGHTEN it (chain walk, fault check) and so change behaviour
  for a live stream, which step 2 may not do. `Workspace`'s eviction path calls
  `kv.Value.Stream.Hub.Dispose()` inside a try/catch that already logs the failure — and guarding it
  would SKIP a disposal, leaking the hub of a reduced stream whose parent died first.
  (`SynchronizationStream`'s own write paths funnel through the private `TryGetActiveHub`, and
  `DeliverMessage` / `OnNext` / `RegisterForDisposal` carry `isDisposed || Hub is null`. Those are
  bare `Hub` field reads with no stream receiver, so they are outside the 54 by construction.)
- **Hub-turn confined (3).** The three `Stream.Hub.Version` reads inside `LayoutAreaHost`'s
  `Stream.Update(…)` lambdas. `Update` already refuses on a dead stream via
  `SignalDisposedToProducer`, and the lambda itself runs inside the sync hub's
  `UpdateStreamRequest` handler — a hub that is executing a turn is by construction alive.
- **Owner-side, no way to say "no" (20).** `StandardReducers` (10), `MeshDataSource`'s patch
  reducer (2), and the stream-creation paths in `WorkspaceStreams` (2),
  `PartitionedHubDataSource` (2), `VirtualDataSource` (1), `GenericUnpartitionedDataSource` (1) and
  `JsonSynchronizationStream`'s `reduced.Hub.Register` (2). A reducer's signature is
  `… → ChangeItem<T>`: it MUST return a change, so
  there is no absent value to hand back and no error channel to use. Forcing one would mean
  inventing a failure mode rather than migrating onto an existing one — so they are named here
  instead. **They are step 3's remaining work**: before `Hub` can be cleared on disposal, each of
  these needs a decision about what a reducer does when its stream has died, and that is a design
  question, not a mechanical substitution.

One more constraint the migration follows: **a guarded read inside a per-emission lambda re-reads
`TryGetHub()` rather than capturing the hub resolved at pipeline-construction time.** Capturing
would pin the hub's whole resolved graph for the lifetime of every subscription — the exact
retention this issue exists to release.

### Why step 1 is an extension, not an interface member

`StreamLiveness` records both reasons and neither has expired:

- **Adding a member to a public interface breaks every downstream implementer**, and these
  assemblies ship as packages.
- **`IStreamLivenessSource.Source` is not a consumer-facing concept.** It exists so the reduce chain
  is walked in exactly ONE place; a consumer walking it by hand is the ad-hoc predicate that whole
  design removed.

An extension method satisfies both: the predicate becomes reachable, the interface it reads stays
internal, and no implementer is touched. The public methods delegate and add no logic of their own —
`StreamLiveness` exists *because* three hand-copied liveness predicates diverged, and a public
wrapper that re-implemented the check would be the fourth. The name stays `IsUsable` for the same
reason: a dozen comments across `Workspace`, `JsonSynchronizationStream` and
`StreamNotConvergingException` already point readers at "StreamLiveness.IsUsable", and a consumer
following one of them must find the thing it names.

### The guard that keeps step 1 from silently reverting

`MeshWeaver.Data` grants `InternalsVisibleTo("MeshWeaver.Data.Test")`, so a test that merely *called*
`stream.IsUsable()` would compile and pass just as happily if the surface reverted to `internal` —
which is the exact state this step exists to leave behind. So
`StreamLivenessIsConsumerReachableTest.TheSurfaceIsGenuinelyPublic` reads accessibility by
**reflection**, which `InternalsVisibleTo` does not alter. Measured: with the class flipped to
`internal` the project still builds and the other three tests still pass — only that one goes red.

## Related

- `Doc/Architecture/RemovingHandWovenGates` — the sibling rule for gates the actor model cannot hold
- `Doc/Architecture/AsynchronousCalls` — why every hub-reachable path is `IObservable<T>`
- Systemorph/MeshWeaver#3321 (this program) · #1455, #2387 (why `StreamLiveness` is one predicate)
