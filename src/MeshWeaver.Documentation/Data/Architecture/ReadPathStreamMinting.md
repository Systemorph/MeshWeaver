---
Name: The Read Path Minted a Hub Per Read
Category: Architecture
Description: A live census of a production replica decomposed its 4,687 sync/ hubs into their holders, falsified the standing explanation, and pinned the growth on one line - a GetDataRequest resolved its stream through the uncached configured overload, so every read left a permanent sync/ hub on the owning node hub. Six reads, six hubs, measured on the running portal.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7z"/><circle cx="12" cy="12" r="3"/><path d="M19 5l2-2"/><path d="M19 19l2 2"/></svg>
---

# The Read Path Minted a Hub Per Read

[#3432](https://github.com/Systemorph/MeshWeaver/issues/3432) has carried the same title since it was
filed: *population MEASURED, cause NOT established*. This page closes the cause, and it does so with a
**controlled experiment on a running production replica** rather than another reading of `src/`.

> **Six `GetDataRequest`s against one node hub took it from 8 to 14 `sync/` hubs.** One per read, 1:1,
> permanent.

It also **falsifies** the explanation that stood before it — not the population measurement, which was
always solid, but the mechanism [The Evicted-Stream Retention](../EvictedStreamRetention) proposed.
That is recorded here in full, because a wrong mechanism that survives is worse than no mechanism: the
next session builds on it.

## 1. The instrument: a live census, no heap dump

Every previous attempt needed a `--type Heap` dump, and [Portal Heap Is Hubs](../PortalHeapIsHubs)
measured what that costs — a **106 s** thread suspension against a **90 s** liveness budget, i.e. the
replica restarts and the state being measured is destroyed. The freeze scales with the heap, so the
measurement is self-defeating exactly where the population exists.

It is not needed. A `Code` node running through the kernel executes **inside the portal process**, so
it can walk the live hub tree and read the holders' own fields:

- climb `Configuration.ParentHub` to the mesh root;
- recurse `MessageHub.hostedHubs` (the same walk `MessageHub.LiveHubTree` does for the platform
  meter), tallying `Address.Type == "sync"` per parent;
- for each parent, resolve its `IWorkspace` and read `_remoteStreamCache`, `_evictedRemoteStreams`,
  `_remoteStreamLeases`, `_clientSubscriptions`, `_localStreamCache` and each data source's `Streams`;
- attribute a `sync/` hub to the stream that minted it by walking the hub's own `disposables`
  composite to the closure that captured the stream (`RegisterForDisposal(_ => ReleaseHub())`), then
  reading that stream's `Reference` and `Owner`.

Read-only, bounded, and it runs in ~10 ms. It is the same class of instrument as the platform meter
(#3488), one level finer.

🚨 **A run lands on whichever replica hosts the activity hub.** Two runs are comparable only when the
`pod` and `pid` printed by the census match; a differing pod is a different population, not a trend.

## 2. The decomposition (memex.meshweaver.cloud, pod `…-sd7p4`, 2026-09-10 17:34Z)

Uptime 89.8 min, working set 5,382 MiB, GC heap 2,396 MiB.

```
hubs=5362  sync=4687  parents=661
cached=214  parked=475  subs=821  local=653  dsStreams=728
explained=2891   residual=1796      (38 % held by NONE of the known holders)
```

| holder | count | what it is |
|---|---:|---|
| `_clientSubscriptions` | 821 | owner-side twins of live cross-hub subscriptions |
| data-source `Streams` | 728 | one per data source per partition |
| `_localStreamCache` | 653 | cached plain reduces |
| `_evictedRemoteStreams` | 475 | change-feed-parked remote mirrors |
| `_remoteStreamCache` | 214 | live remote mirrors |
| **unaccounted** | **1796** | — |

## 3. 🚨 What this falsifies

[The Evicted-Stream Retention](../EvictedStreamRetention) concluded that the parked set is filled by
**unleased** streams — *"`LayoutAreaReference` is the volume case"* — because `ReclaimIfUnheld`
returns early when `_remoteStreamLeases.TryGetValue` is false. Classified on the live replica:

```
parked   474  MeshNodeReference   : lease == 1
parked     1  LayoutAreaReference : no lease entry
```

**One of 475.** The parked set is dominated by the mesh-node cache's own mirrors, which *are* leased —
they take the `leases != 0` branch, which is the branch that is **correct**, and they are released by
the idle sweep when their cache entry is. The unleased mechanism is real, and it is 0.2 % of the
parked set.

The parked count was also **flat across three readings** (474 / 475 / 475) while the total grew by 120.
It is not what grows, so it cannot be what #3432 is about.

The [2026-09-09 comment](https://github.com/Systemorph/MeshWeaver/issues/3432#issuecomment-5599424759)
on #3432 was therefore right on both counts, and its warning stands: do **not** change the
`ReclaimIfUnheld` predicate. `EvictForPath` parks a HEALTHY stream precisely because an undeclared
reader may still be attached; disposing on "nobody ever declared a lease" would cut live readers off
silently, and would buy ~475 hubs for a correctness regression.

## 4. Where the growth actually is

Three readings on the same pod and pid:

| UTC | uptime | `sync/` hubs |
|---|---:|---:|
| 17:30:48 | 85.9 min | 4,647 |
| 17:34:41 | 89.8 min | 4,687 |
| 17:38:07 | 93.2 min | 4,767 |

**≈ +16 hubs/min ≈ +985/h ≈ +385 MB/h** at the ~390 KB a hub retains — the same order as the ~210 MB/h
the issue reported on a 26 h replica. **Growing, not a high-water mark**, and the growth is in the
residual.

Attribution of the residual named it precisely. On this replica one node hub —
`LocalSyncDemo/_Sync/party-1` — held **257** `sync/` hubs (229 → 237 → 243 → 257 over eight minutes),
and **25 of 25 sampled** were streams with

```
MeshNodeReference  owner=ds/LocalSyncDemo/_Sync/party-1
```

while that data source's own `Streams` dictionary held **one**. Streams reduced from the data source's
primary stream, retained by nothing that indexes them.

## 4b. It is NOT [#3593](https://github.com/Systemorph/MeshWeaver/issues/3593) — measured, not assumed

#3593 reports a mass event on one memex-cloud pod in which every affected hub was also a `sync/` hub
also at `RunLevel=Started`, reading `Disposal=Pending`, `buffer=1`, `exec=0`: `Dispose()` HAD been
called and the action block never dequeued the buffered `ShutdownRequest`, so teardown never started
and *"each pending hub pins its stream, subscriptions and object graph until restart."* Same family,
opposite mechanism — so the census asked the question directly rather than reasoning about it. Every
`sync/` hub on the replica, classified by the same fields `MessageHub.AppendDiagnostics` prints
(`disposalStarted`, `DisposalSignalled`, and `MessageService.GetQueueSnapshot()`):

```
CENSUS6  pod=…-f9lb5  upMin=123.5  sync=4649
         withBufferedMessage=0  executingATurn=0  snapshotUnreadable=0
   4648  Started  / Disposal=<not started>
      1  Starting / Disposal=<not started>
```

**Zero `Pending`, zero buffered, zero executing, and the denominator is the whole population** —
`snapshotUnreadable=0` means every one of the 4,649 was actually read, not skipped.

So these hubs were **never asked to dispose**. Nothing is wedged, nothing is retained against a
teardown in progress, and no pump is stuck: they are alive because the stream that owns each of them
is alive and nothing ever released it. That is the opposite end of the lifecycle from #3593, and it
rules the wedged-pump explanation out for this population rather than leaving it open. #3593 keeps its
own defect and its own fix; nothing here should be filed under it.

## 5. The cause, and the controlled experiment that pins it

`Workspace._localStreamCache` caches a **plain** reduce and deliberately does not cache a
**configured** one. Its own field note says why, and says what a configuration means:

> A caller-supplied `configuration` DOES make the stream caller-specific (client id, subscriber,
> initialization callback, property bag), so those stay uncached.

The read handlers passed a configuration that is **none of those things** — a constant:

```csharp
// DataExtensions.GetDataResponseObservable<TReference> — the GetDataRequest handler
var stream = workspace.GetStream(reference, x => x.ReturnNullWhenNotPresent());
```

`ReturnNullWhenNotPresent` sets one `bool` on the configuration record. It says how the reduction
answers an absent state; it identifies no caller. But it is *a* configuration, so every read took the
uncached branch — and, as the same field note spells out, an uncached reduce constructs a
`SynchronizationStream`, hence a hosted `sync/{id}` sub-hub with its own Autofac scope, `TypeRegistry`
and `JsonSerializerOptions`, **registered for disposal on its hub-lifetime parent**. One permanent hub
per read, released only when the owning node hub dies.

The experiment, run in-process on the live replica against a node in the caller's own partition:

```
BEFORE 8 sync hubs under rbuergi/HubCensus0910
  read 1: ok  now  9 sync hubs
  read 2: ok  now 10 sync hubs
  read 3: ok  now 11 sync hubs
  read 4: ok  now 12 sync hubs
  read 5: ok  now 13 sync hubs
  read 6: ok  now 14 sync hubs
AFTER 14 sync hubs (delta 6 for 6 reads)
```

Three call sites shared the defect: the `GetDataRequest` handler
(`GetDataResponseObservable<TReference>`), the unified-reference read (`GetDataFromWorkspaceCore`) and
`DeleteUnifiedReference`.

## 6. The fix

The flag belongs in the **cache key**, not in a caller-specific configuration. `_localStreamCache` is
keyed `(WorkspaceReference Reference, bool NullReturn)`, and the read path resolves its stream through
`IWorkspace.GetNullableStream`, which shares one stream per reference:

```csharp
public ISynchronizationStream<TReduced> GetStream<TReduced>(
    WorkspaceReference<TReduced> reference,
    Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>>? configuration)
    => configuration is not null
        ? ReduceLocalStream(reference, configuration)      // genuinely caller-specific: unchanged
        : GetCachedStream(reference, nullReturn: false);

public ISynchronizationStream<TReduced> GetNullableStream<TReduced>(
    WorkspaceReference<TReduced> reference)
    => GetCachedStream(reference, nullReturn: true);
```

The interface member carries a **default implementation** forwarding to the uncached overload, so an
implementer outside this repository keeps working unchanged.

The population is now bounded by **distinct references per hub** instead of by reads. Nothing about the
read's semantics changes: the stream still replays its current value to each new subscriber and still
pushes every later change, which is what the handler's *"No `Take(1)`: updates flow continuously"*
contract requires.

🚨 **The new bound is never worse than the old one**, and that is the whole safety argument: distinct
references ≤ calls, always. It is not always *tight* — a reference that carries a per-request unique
field (an `EntityReference` for a one-shot delete, say) now caches one stream per distinct entity
rather than one per call. That is strictly fewer, it is exactly what `_localStreamCache` has always
done for a plain reduce, and where it matters the answer is a narrower reference, not a fresh stream.

**Controls in both directions** — `test/MeshWeaver.Data.Test/ReadPathStreamMintingTest.cs`:

| arm | asserts |
|---|---|
| `ReadingTheSameReferenceRepeatedly_DoesNotMintASyncHubPerRead` | five reads of one reference add **0** `sync/` hubs to the owner (it fails with **+5** on the pre-fix call site) |
| `AnUncachedConfiguredReduce_MintsOneSyncHubPerCall` | five genuinely-configured reduces add **5** — the counter can see growth, so the flat arm is not vacuous |
| `TheSharedReadStream_StaysLive` | a write after the reads is visible to the next read — the shared stream is a live mirror, not a snapshot |
| `UnifiedUpdate_WaitsUntilTheSharedReadStreamCarriesTheUpdate` | a successful update is withheld while the owner has committed but the shared read actor is parked on the old entity |
| `UnifiedDelete_WaitsUntilTheSharedReadStreamCarriesAbsence` | a successful delete is withheld while the shared read actor is parked, even after the owner has committed; it answers only after the read view carries absence |
| `UnifiedDelete_RetainsItsAbsenceAcknowledgementAcrossSameIdRecreation` | the read view reaches the delete's committed version before a later same-ID recreation becomes its newest state, so the delete still answers without mistaking the recreation for stale data |
| `ReadVersionBarrier_AcceptsNewerStateAfterTheDeleteFrameWasReplaced` | a late barrier subscription completes from the newer recreation after the delete's exact null frame has been replaced in the shared one-item replay slot |

### A shared stream makes its propagation boundary observable

The data-source stream and a reduced read stream are separate actor-backed streams. Applying a change
to the owner synchronously publishes a source frame, but forwarding that frame to the reduced stream
posts work to the reduced stream's own hub. The owner's post-apply callback can therefore run while
the read frame is still queued.

This distinction did not matter when every read built a new reduction: a reduction built after the
owner commit starts from the owner's latest replayed store. Once reads share the existing reduction,
an immediate read can replay that reduction's previous value until its queued frame lands. The
release gate exposed both directions: a successful `UpdateUnifiedReferenceRequest` followed by the
old entity, and a successful `DeleteUnifiedReferenceRequest` followed by the deleted entity.

Both handlers now warm the same shared entity stream **before** invoking the eager owner write. After
commit they capture the owning stream's committed version and wait until the shared read stream has
applied that version or a newer one. Checking the current snapshot inside a deferred subscription,
then relying on the stream's own one-item replay, means the boundary cannot miss a frame that lands
between those two operations. The version is the causal proof: a same-value older frame cannot release
an update, while a legitimate same-ID recreation after a delete is accepted as newer state instead
of leaving the delete waiting forever for a null that has already been replaced. There is no polling
and no replacement stream. Owner commit remains the storage boundary; successful unified
update/delete is also the causal read-view boundary for the API's shared read path.

### One behaviour DID change, and it is the better half of an existing contract

`ReadDuringDisposalWindowTest` (#1470) pins what a read gets when its owner is tearing down. It warms
the reference first, then reads it again inside the disposal window — and before this change the
second read FAULTED, because building a fresh stream is refused once hosted-hub creation is frozen,
which is what produced the transient `ShuttingDown` NACK. With a shared stream the warmed reference is
now **answered from the live mirror, with the data**.

That is not a weakening. What #1470 forbids is a fabricated `GetDataResponse{Error}` that
`GetMeshNode` maps to *"this node does not exist"*; real data is its opposite. The contract now has two
halves and both are pinned:

| the read | answer |
|---|---|
| its stream already exists | SERVED from the live mirror — data, no error |
| its stream would have to be BUILT in the frozen window | transient `ShuttingDown` NACK naming `HubDisposingException` |

The NACK arm's reference is deliberately one the owner has never reduced, and the file says so — warm
the same reference and that test would pass on a hub that refused nothing.

## 7. 🚨 Still open, and named

- **The rest of the residual.** The fix addresses the `GetDataRequest` path, which is where the
  attribution landed. 1,796 unaccounted hubs on that replica were not attributed one by one, and the
  census is the instrument for finishing that: re-run it after a deploy carrying this change and read
  `residual` and its slope. Two readings on the same `pod`+`pid`, minutes apart, are the trend; one
  reading is a snapshot of a moving target. The census used here survives as a `Code` node in the
  author's own partition, but it is a scratch node — §1 is the recipe, and the recipe is the durable
  form.
- **`ContentCollection.GetMarkdown` has the same shape one layer down.** It calls
  `markdownStream.Reduce(reference, c => c.ReturnNullWhenNotPresent())` — the stream-level *uncached*
  reduce — once per call, while the cached sibling `ReduceShared` takes no configuration by design. Same
  remedy (the flag in `sharedReduceCache`'s key), **not measured**, so not changed here.
- **A `GetDataRequest` read is never unsubscribed.** `HandleGetDataRequest` registers its subscription
  for disposal on the hub, and nothing else ends it, so an owner keeps posting responses to a caller
  that stopped listening until the hub dies. That is a lifetime defect in its own right; it costs an
  observer, not a hub, and it is untouched here.

## Related

- [The `sync/` Hub Population](../SyncHubPopulation) — the 1:1 stream↔hub identity this all rests on
- [The Evicted-Stream Retention](../EvictedStreamRetention) — the mechanism §3 re-scopes to 1 of 475
- [Portal Heap Is Hubs](../PortalHeapIsHubs) — the dumps, and why the dump is the wrong instrument
- [Stream Liveness and the Hub Reference](../StreamLivenessAndTheHubReference) — #3321's `Dead` half
- [Hub Disposal Model](../HubDisposalModel) — the state machine §4b's `Disposal=` reading comes from
