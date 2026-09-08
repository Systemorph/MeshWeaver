---
Name: The Evicted-Stream Retention
Category: Architecture
Description: A change-feed eviction parks a remote sync stream, and ReclaimIfUnheld refuses to dispose one that carries no lease entry — so every unleased call site retains one live stream, and two sync/ hubs, per change event. Established by a controlled two-arm test, not by a heap dump.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 6h18"/><path d="M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/><path d="M10 11v6"/><path d="M14 11v6"/></svg>
---

# The Evicted-Stream Retention

[The `sync/` Hub Population](../SyncHubPopulation) settled *where* the hubs come from — one per
`SynchronizationStream`, two per cross-hub subscription — and left the question that decides
[#3432](https://github.com/Systemorph/MeshWeaver/issues/3432) open: **are those streams garbage, and
if so what holds them.** This page answers it for one named mechanism, and the evidence is a
controlled in-process experiment rather than a heap dump.

**The verdict is RETENTION, not a leak.** Nothing is created and forgotten. The streams are parked
*deliberately*, by code that says so, and then the one routine that could release them structurally
declines to — for a reason that is correct in general and wrong for the majority of call sites.

## 1. The two lines

`Workspace.EvictForPath` runs on **every** change-feed event and parks every cached remote stream
whose owner matches the changed path:

```csharp
// src/MeshWeaver.Data/Workspace.cs — EvictForPath
_evictedRemoteStreams[removed.Value] = 0;
ReclaimIfUnheld(removed.Value);
```

and `ReclaimIfUnheld` opens with:

```csharp
// src/MeshWeaver.Data/Workspace.cs — ReclaimIfUnheld
if (!_remoteStreamLeases.TryGetValue(stream, out var leases) || leases != 0)
    return;
```

Read the first clause carefully. A stream **nobody ever leased** has no entry in
`_remoteStreamLeases` at all, so `TryGetValue` returns `false` and the method returns having disposed
nothing. `leases != 0` — "a holder is still declared" — and "no holder was ever declared" take the
same branch, and only the first of those is a reason to keep the stream.

The stream is now in `_evictedRemoteStreams`, an instance `ConcurrentDictionary` on the **singleton**
workspace, holding its client `sync/` hub and (through the owner's mirror) its owner-side twin. The
next caller misses the cache, builds a fresh stream, and the next change event parks that one the
same way.

🚨 **The same file already handles this case correctly two hundred lines away.**
`DiscardFaultedRemoteStream` parks a stream, checks `_remoteStreamLeases.ContainsKey` **explicitly**,
and disposes on the spot when no holder was ever declared. Two sites, one situation, opposite
outcomes — the second delegates to a helper that cannot distinguish "held" from "never declared".

## 2. Why the parking is deliberate — and why that is not the whole story

`EvictForPath` carries an explicit instruction not to dispose unconditionally:

> Do NOT unconditionally dispose the evicted stream — an undeclared reader (e.g. a `MeshDataSource`
> reduce callback that handed the stream on) may still be attached and needs to keep receiving
> updates until it drops on its own.

That is sound. An undeclared reader is invisible, so the workspace genuinely cannot know. The
`_remoteStreamLeases` note (the #1324 fix) states the intended remedy in as many words:

> A stream NOBODY leased is never in this registry and keeps the old conservative parking —
> undeclared holders … are unaffected. **Opting a call site in is one line** and is what makes its
> streams reclaimable.

So the design is: *leasing is opt-in, and the un-opted-in remainder is knowingly retained.* The
finding here is that **the un-opted-in remainder includes the portal's highest-volume path**, and
that nothing ever drains it.

## 3. Nothing else collects them

There is exactly one other drain, `Workspace.DetachRemoteStreams`, and it is reachable from exactly
one call site — `MeshNodeStreamHandle.DetachUpstreams()` — which always passes
`new MeshNodeReference()`. It matches parked streams on `Equals(parked.Reference, reference)`.

| a parked stream whose `Reference` is… | reaped by the mesh-node cache's idle release? |
|---|---|
| `MeshNodeReference` | yes — if the cache holds an entry for the path and the sweep fires |
| `LayoutAreaReference`, `CollectionReference`, anything else | **no. Nothing matches it.** |

For the second row the only remaining disposal is `Workspace.Dispose()` — i.e. process exit.

**And `LayoutAreaReference` is the volume case.** `LayoutExtensions.GetControlStream` — the call
every rendered layout area goes through — is
`hub.GetWorkspace().GetRemoteStream(address, new LayoutAreaReference(area) { Id = id })`: the public
`GetRemoteStream`, which caches the stream and takes **no lease**. `EvictForPath` matches on the
owner alone and ignores the reference, so a change event on that owner parks the layout area's
stream too. When the Blazor circuit later ends, its subscription drops — and the stream itself is
disposed by nobody.

The other unleased openers are `MeshOperations` (also `LayoutAreaReference`), and the
`MeshDataSource` / `SyncedQueryDataSource` reduce callbacks (`MeshNodeReference`, so at least
reachable by the idle release).

## 4. The controlled experiment

`test/MeshWeaver.Data.Test/EvictedUnleasedStreamRetentionTest.cs` drives the identical sequence twice
— resolve a remote stream for one `(owner, reference, identity)` triple, fire one owner-path
change-feed event, five times — and counts the live `sync/` hub population the way #3432 counts it:
`HostedHubsCollection.Hubs` filtered to `Address.Type == SynchronizationAddress.AddressType`.

**The two arms differ in exactly one thing: whether a lease is taken.**

```
DIAG unleased: opened=5 closed=0                   clientSyncHubs=+5
DIAG leased:   opened=5 closed=0 realLeases=5/5    clientSyncHubs=+0     (run 1)
DIAG leased:   opened=5 closed=0 realLeases=5/5    clientSyncHubs=+1     (run 2)
```

Five change events on ONE cache key: the unleased arm ends with **five live client `sync/` hubs**
where the cache's own documented invariant is *"one stream per key, never two competing live
subscriptions"*. The leased arm ends at **0 or 1** — the spread is whether the final stream's
eviction had landed when the count was taken, and 1 *is* the denominator: the one current mirror for
that key. The unleased arm's 5 is flat growth with the number of change events; the leased arm does
not grow at all.

🚨 **The leased arm is the positive control, and it earned its keep.** The first version of this test
measured reclamation by counting `UnsubscribeRequest` at the owner, and the control arm **failed** —
`closed=0` in both arms. The instrument was wrong, not the mechanism: disposal in this teardown
ordering does not deliver an `UnsubscribeRequest` the owner's rule chain sees, so that counter cannot
distinguish "not reclaimed" from "reclaimed quietly". Had the control been omitted, the unleased
arm's `closed=0` would have been reported as proof of a retention it does not actually measure.
`realLeases=5/5` is in the output for the same reason — `AcquireRemoteStreamUnchecked` returns
`Disposable.Empty` when the stream it resolved is not usable, and a control arm that silently leased
nothing would have looked like "leasing does not help".

## 5. A second, independent root on the same population

`JsonSynchronizationStream` registers the stream's owner-protocol subscription **twice**:

```csharp
reduced.RegisterForDisposal(observeSubscription);
// Belt-and-suspenders: dispose the subscription when the HUB tears down too (idempotent).
hub.RegisterForDisposal(observeSubscription);
```

`hub` here is the **outer, long-lived** hub, not the stream's own. `MessageHub.RegisterForDisposal`
adds to a plain Rx `CompositeDisposable`, and **`CompositeDisposable.Add` never prunes** — disposing
a child does not remove it from the composite. So the outer hub accumulates one strong reference per
remote stream it has ever opened, and the closure captures `reduced`, the stream. For a stream that
is never disposed, that subscription is never disposed either, so the closure stays live and the
stream — with its `sync/` hub still attached — is rooted a second time.

Line 575 alone already ties the subscription to the stream's own lifetime. The `hub.` line adds no
guarantee and one monotonic root.

The same monotonic-composite shape feeds `DataExtensions` per **message** (`SubscribeRequest`,
`DataChangeRequest`, `PatchDataRequest`, `UpdateUnifiedReferenceRequest`), where the same file
elsewhere uses the correct `.TakeUntil(hub.DisposalCompleted)` + self-disposing
`SingleAssignmentDisposable` pattern.

## 6. What was ruled OUT

Both were plausible and both are closed, so nobody needs to re-run them:

- **No static collection anywhere in `src/` holds a hub or a stream.** A full audit found zero
  `static` fields of type `IMessageHub`, `MessageHub`, `ISynchronizationStream`, `IWorkspace` or
  `IMeshService`. Every hub and stream registry is already an instance field on a mesh-scoped
  singleton. (Unrelated static state does exist and is catalogued in
  [No Static State](../NoStaticState) — none of it can reach a hub.)
- **No timer roots a hub.** Every periodic subscription that could — the per-hub stale-callback
  scanner, the sync-stream heartbeat, the `MeshDataSource` persistence sampler, the kernel idle
  disconnect, the NodeType debounce — holds a `WeakReference` and self-disposes on the tick after the
  target is collected. Each carries a comment naming the `TimerQueue → PeriodicTimer → MessageHub`
  chain it was written to break.

## 7. The denominator

`_remoteStreamCache` is keyed `(Owner, Reference, Identity)` and its own comment states the
invariant: *"one stream per key, never two competing live subscriptions."* So:

```
correct live client streams  =  |distinct (Owner, Reference, Identity) triples in use|
correct live sync/ hubs      =  2 × that          (client + owner-side twin)
excess                       =  2 × |_evictedRemoteStreams|
```

Everything in `_evictedRemoteStreams` is above the denominator **by construction** — it is out of the
cache, so no future caller can adopt it, and it is not the current stream for any key.

In the experiment the denominator is 1 and the measurement is 1 per change event. For the production
figure the denominator is unknown, and that is the honest remaining gap: §4 establishes the
mechanism and its unboundedness, **not** that it accounts for all 6 925.

## 8. 🚨 The one measurement that closes #3432 — and it is not the referrer walk

#3432's plan calls for a ClrMD **referrer walk**, which needs the dump that (measured, 2026-09-06)
freezes the replica for 106 s and restarts it. §7 makes a far cheaper reading decisive, because the
excess is a *field on one object*:

> On the singleton `Workspace`, read **`_evictedRemoteStreams.Count`** and
> **`_remoteStreamCache.Count`**.

Two field reads on a single instance — no referrer walk, no full-heap traversal, no type histogram.

| reading | means |
|---|---|
| `_evictedRemoteStreams` ≫ `_remoteStreamCache` | this page's mechanism dominates; fix it at the call sites (§9) |
| `_evictedRemoteStreams` ≈ 0 | the retention is elsewhere — §5's outer-hub composite becomes the prime suspect |
| both small vs 6 925 | the streams are legitimately live and #3432 is per-hub DI cost, its own third outcome row |

Both counts are also derivable **without a dump** from existing `Debug` logging, which already emits
one line per open (*"opened remote stream {StreamId} for {Owner} as {Identity}"*), one per eviction,
and one per reclaim (*"disposed superseded remote stream … no declared holder remains"*):
`opened − reclaimed` is the retained count.

## 9. The fix, and why it is not in this change

The remedy is the one the source already names — **opt the unleased call sites into the lease** —
tying it to the consumer's subscription:

```csharp
// LayoutExtensions.GetControlStream, in shape
Observable.Create<object?>(observer =>
{
    var (stream, lease) = workspace.AcquireRemoteStreamUnchecked<JsonElement, LayoutAreaReference>(
        address, new LayoutAreaReference(area) { Id = id });
    return new CompositeDisposable(stream.GetControlStream(area).Subscribe(observer), lease);
});
```

It is safe in the way that matters: `ReclaimIfUnheld` disposes only a stream that is **also** evicted,
so a stream still in the cache is never touched, and once every consumer of a given path leases, no
undeclared reader remains to be surprised.

It is nevertheless a separate change, for reasons that are about blast radius rather than effort:
`GetControlStream` is **public** and changes the lifetime of every layout area's stream; resolving
the stream inside `Observable.Create` moves resolution from call time to subscribe time; the
consumers most affected are **in-mesh layout areas that no `dotnet build` and no CI job ever
type-checks**; and `AcquireRemoteStreamUnchecked` is `internal` to `MeshWeaver.Data` with no
`InternalsVisibleTo` for `MeshWeaver.Layout`. Deleting the redundant `hub.RegisterForDisposal` of §5
is smaller but lands on the same hot path.

Neither belongs in a change whose purpose was to establish the cause.

## Related

- [The `sync/` Hub Population](../SyncHubPopulation) — the 1:1 stream↔hub identity and the idle
  sweep that traffic keeps re-arming
- [Stream Liveness and the Hub Reference](../StreamLivenessAndTheHubReference) — #3321 step 3, which
  reclaimed the `Dead` half
- [Portal Heap Is Hubs](../PortalHeapIsHubs) — the five dumps and the measurement hazard
- [Live Mirrors and the Change Feed](../LiveMirrorsAndTheChangeFeed) — why the eviction is
  unconditional and must stay so
- [No Static State](../NoStaticState) — the rule §6 checked against
