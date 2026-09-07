---
Name: The sync/ Hub Population
Category: Architecture
Description: A sync/ hub is not an independent population — there is exactly one per SynchronizationStream, and one cross-hub subscription makes two of them. What retires a Started one under a live parent is a single idle sweep whose window is refreshed by the very writes that orphan streams into it.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 6h16"/><path d="M4 12h16"/><path d="M4 18h16"/><circle cx="8" cy="6" r="2"/><circle cx="16" cy="12" r="2"/><circle cx="8" cy="18" r="2"/></svg>
---

# The `sync/` Hub Population

A 26 h production replica held **9 398 `MessageHub` instances**, of which **6 925 were `sync/` hubs
at `RunLevel=Started`** — about 2.7 GB of a 3.5 GB live heap (#3432). This page is what the code says
about where they come from and what is supposed to take them away. It is a **narrowing, not a
diagnosis**: the sections marked 🚨 NOT ESTABLISHED are still open.

## 1. A `sync/` hub is not a population — it is one field of a stream

`SynchronizationStream<T>`'s constructor mints exactly one, unconditionally, for every stream:

```csharp
// src/MeshWeaver.Data/Serialization/SynchronizationStream.cs
var syncHub = Host.RunLevel > MessageHubRunLevel.Started
    ? null
    : Host.GetHostedHub(
        SynchronizationAddress.Create(ClientId), ConfigureSynchronizationHub, HostedHubCreation.Always);
…
Hub = syncHub;
syncHub.RegisterForDisposal(_ => ReleaseHub());
```

And **one cross-hub subscription builds two streams, hence two hubs**:

| side | who builds it | where |
|---|---|---|
| client | `JsonSynchronizationStream.CreateExternalClient`, `Host` = the subscribing hub | `JsonSynchronizationStream.cs` |
| owner | `Workspace.SubscribeToClient` → `CreateSynchronizationStream` → `ReduceStream(… WithClientId(request.StreamId))`, `Host` = the owner's hub | `Workspace.cs` · `WorkspaceStreams.cs` |

Both carry the **same** `ClientId`, so the two hubs share an address string while living under
different parents.

**This settles the first of #3432's open questions.** The dump's own arithmetic agrees:

```
6 925 Started sync/  +  1 495 Dead sync/  =  8 420
SynchronizationStream total                =  8 461     (0.5 % apart)
```

So there is no separate "sync hub leak" to explain. **The question is entirely "why are there 8 461
live `SynchronizationStream`s"**, and each one costs its own Autofac `ILifetimeScope` and
`TypeRegistry` (~390 KB, of which ~173 KB is framework metadata duplicated per hub).

## 2. What retires one — three paths, and only one of them applies here

| path | where | which population it reaches |
|---|---|---|
| the stream is disposed | `SynchronizationStream.Dispose()` disposes its own `Hub` | the 11 |
| the **parent** hub is torn down (circuit ends, `DisposeRequest`, recycle) | `HostedHubsCollection` → the `RegisterForDisposal(_ => ReleaseHub())` hook (#3427) | the 1 485 measured **Dead** |
| the cache releases the path | `MeshNodeStreamCache` idle sweep → `TryReleaseUnwatched` → `DetachUpstreams` → `TearDownEntry` → `stream.Dispose()` | **the Started population — and nothing else does** |

The middle row is #3321 step 3, and it is why the `Dead` bucket exists at all. It does not touch a
hub whose parent is alive. So for a **`Started` `sync/` hub under a live parent — which is this
issue's entire 6 925 — the idle sweep is the only reaper in the process.**

`MeshNodeStreamHandle.DetachUpstreams()` is reachable from nowhere else, and it is also the only
thing that collects the change-feed-**parked** predecessors described below.

## 3. The sweep's predicate, and why traffic defeats it

```csharp
// src/MeshWeaver.Hosting/MeshNodeStreamCache.cs — Entry
public bool TryMarkIdleEvicted(TimeSpan idleWindow)
{
    lock (gate)
    {
        if (evicted || subscribers > 0
            || Environment.TickCount64 - lastActiveAt < (long)idleWindow.TotalMilliseconds)
            return false;
        evicted = true;
        return true;
    }
}
```

Window 10 minutes, swept every minute (`MeshNodeStreamCacheOptions`). Two conjuncts, and the option's
own summary states what refreshes the second: *"Every `GetStream` subscription, unsubscription **and
`Update`** on the path refreshes the window."*

🚨 **The `subscribers` count is NOT the Rx-subscriber trap** `Workspace` warns about. That warning
("the reduce chain subscribes to the stream ITSELF, so an evicted stream measures 2–3 subscribers and
never reaches zero — implemented, measured and reverted") is about a *different, abandoned* mechanism
at the workspace layer. `Entry.subscribers` is a purpose-built refcount moved only by
`TryAddSubscriber`/`RemoveSubscriber` from the cache's own `SharedView`, and the internal hydration
subscription bypasses it. **It genuinely can reach zero.** Anyone reasoning about #3432 from the
`Workspace` comment alone will mis-attribute this.

What actually holds the sweep off is the other conjunct.

## 4. The change-feed orphan — the write both creates it and postpones its collection

`Workspace.EvictForPath` runs on **every** change-feed event for an owner path and removes **every
identity's** cached stream for that owner:

```csharp
_evictedRemoteStreams[removed.Value] = 0;
ReclaimIfUnheld(removed.Value);        // disposes ONLY if no declared lease remains
```

The mesh-node cache's hydration is a declared holder **for the entry's whole life** — that is
deliberate, and `MeshNodeStreamHandle.Subscribe` says so: *"The lease lives exactly as long as this
subscription. The shared mesh-node cache's hydration comes through here, so its entry IS the declared
holder of the path's upstream for the entry's whole life — which is what makes every OTHER
(write-scoped) stream for the same path reclaimable on eviction."*

So on the first write to a cached path:

1. the entry's stream **S1** is removed from `_remoteStreamCache` and parked;
2. `ReclaimIfUnheld(S1)` finds the hydration's lease and does **not** dispose it;
3. nothing re-points the hydration — the sweep *"only ever CLOSES idle entries; it never
   re-subscribes anything (the 2026-06-08 rule)"* — so the entry keeps reading **S1** forever;
4. the next reader or writer misses the cache and builds **S2**, which becomes the cached one.

From then on the path holds **two live streams — four `sync/` hubs, ~1.5 MB** — where the design
intends one. Write-scoped streams still churn correctly (an unleased evicted stream *is* reclaimed at
once), so the accumulation is a **floor of one orphaned pair per cached, written path**, not growth
per write.

🚨 **And the same act does both halves:** the `Update` that fires the change-feed event which orphans
S1 is also an `Update` that `Touch()`es the entry, pushing the 10-minute window out. **A path written
at least once every ten minutes can never be released, so its orphan can never be collected.** The
only reaper is switched off by the traffic that feeds it.

That is the answer to *"is the retirement path missing, or present-but-not-firing?"* — **present, and
structurally unable to fire on exactly the paths that generate the garbage.**

## 5. Bounding

Neither cache has memory-pressure eviction or any other TTL:

- `Workspace._remoteStreamCache` — a plain `ConcurrentDictionary` keyed by
  **`(Owner, Reference, Identity)`**, so a path read under N identities holds N streams.
- `MeshNodeStreamCache._streams` — a plain `ConcurrentDictionary` keyed by path. (The
  `MemoryCache` in the same file is the **write**-queue's, not this one's.)

So the live-stream count is bounded only by *(distinct paths × identities) touched since process
start*, with the per-path floor above added on top.

## 6. 🚨 What is NOT established

- **Whether the 8 461 are garbage.** A portal with thousands of open Blazor circuits, each holding
  live layout-area streams, is *supposed* to look like this. Sections 1–5 explain the shape of the
  population, not that it is waste.
- **The split** between the per-path floor of §4 and legitimately-live streams. That is the number
  that decides whether #3432 is a leak with a fix or the per-hub DI cost the issue's own third
  outcome row describes.
- **Whether #3427 moved this number.** Still not re-measured; no deployment carried `f41f8bda` at the
  time of the last attempt.

## 7. The discriminator — a ratio, from data the existing plan already collects

#3432's plan calls for a ClrMD **referrer walk**, which is the expensive half and needs a dump that
(measured) restarts the replica. §1 makes a much cheaper reading decisive, and it comes off
`dumpheap -stat`, which that plan already takes:

> **count `MeshNodeStreamCache+Entry` alongside `SynchronizationStream`.**

| reading | what it means | what to do |
|---|---|---|
| streams ≈ **2 ×** entries | the §4 floor dominates: one change-feed orphan plus one live stream per cached path | a fix with a named code path — release the entry's lease on eviction (or re-point the hydration) so `ReclaimIfUnheld` collects S1. Halves the population without touching anyone's live view |
| streams ≈ **1 ×** entries | no orphaning; the population is one stream per cached path | the leak framing is **falsified** — this is per-hub DI cost (#3432's third outcome row); close it and open the metadata-sharing issue |
| streams **≫ 2 ×** entries | something outside the mesh-node cache mints them | look at the identity dimension of `_remoteStreamCache`'s key (§5) before anything else |

No referrer walk, no field traversal, no second dump — one extra line of a histogram the plan already
produces. 🚨 It does **not** replace the referrer walk for the third row; it tells you whether you
need one.

## Related

- [Stream Liveness and the Hub Reference](../StreamLivenessAndTheHubReference) — #3321 step 3, the
  parent-teardown hook that reclaimed the `Dead` half
- [MeshNode Stream Cache](../MeshNodeStreamCache) — the entry, the refcount and the idle sweep
- [Portal Heap Is Hubs](../PortalHeapIsHubs) — the five dumps, the measurement hazard, and the ClrMD
  recipe
