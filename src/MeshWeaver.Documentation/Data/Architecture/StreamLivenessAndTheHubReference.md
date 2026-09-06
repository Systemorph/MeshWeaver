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

1. **Expose the predicate.** `SynchronizationStreamLiveness.IsUsable(stream)` and
   `stream.TryGetHub()` make the existing answer reachable from other assemblies.
2. **Migrate the dereference sites** onto `TryGetHub()`, which can answer `null`.
3. **Only then** clear `Hub` on disposal — at which point a null hub is a state the contract admits
   rather than a lie.

Doing 3 before 2 is the production NRE, in the order it happened. Doing 2 before 1 is impossible.

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
