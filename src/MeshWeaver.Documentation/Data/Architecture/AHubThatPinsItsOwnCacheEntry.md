---
Name: A Hub That Pins Its Own Cache Entry
Category: Architecture
Description: On the Orleans host every activated node hub was handed the process-wide mesh-node cache's live view of its OWN path as its own-node source, and kept a subscription on it for life. The entry could never reach zero subscribers, its hydration stream heart-beat the grain alive, and the loop closed with nothing outside it — every node ever activated on a replica stayed resident with its cache entry and the sync/ hubs on both sides. The chain, the falsifying test, and why a one-shot own-node source loses nothing.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M17 3a4 4 0 0 1 0 8h-1"/><path d="M7 21a4 4 0 0 1 0-8h1"/><path d="M12 7v10"/><path d="M8 11l4-4 4 4"/><path d="M16 13l-4 4-4-4"/></svg>
---

# A Hub That Pins Its Own Cache Entry

[#3432](https://github.com/Systemorph/MeshWeaver/issues/3432) measured a `sync/` hub population that
grew ~25 per minute on a live replica and never retired, concentrated on parents whose paths are
per-activity satellites (`<partition>/_Activity/compile-state`, `…/content-manifest`): 6–11 `sync/`
hubs per parent, 3–5 of them explained by the known holders. [The `sync/` Hub
Population](../SyncHubPopulation) established that such a hub is one field of a
`SynchronizationStream` and named the three paths that retire one;
[Read-Path Stream Minting](../ReadPathStreamMinting) closed one creation path and left the residual
named. This page names the retainer behind the residual — read out of `src/` and pinned by a test
that fails on the unfixed code — and the one-line change that removes it.

## The loop

Five links, each of them the platform doing what its own comments say it does:

1. **The grain resolves its node through the cache.** `MessageHubGrain` composes its activation
   source as `Observable.Merge(pathResolver.ResolvePath(path), streamCache.GetStream(path))`
   (`MessageHubGrain.cs`, `ComposeActivationSource`). The cache leg is
   `MeshNodeStreamCache.SharedView(path)`, which registers a **live subscriber** on the entry
   (`Entry.TryAddSubscriber`) and never completes — it relays a `ReplaySubject(1)`.
2. **The activation chain takes one node and lets go** — `BuildActivationChain` ends in `Take(1)`, so
   its subscriber is released. **But the same cold source was then handed to the hub** as its
   own-node stream: `config.WithOwnNodeStream(sourceStream)` in `CompleteActivation`.
3. **The hub keeps that stream for life.** `MeshNodeTypeSource` wraps it as
   `DistinctUntilChanged().Replay(1).RefCount()` and subscribes at construction
   (`enrichmentCaptureSub`, registered with `workspace.Hub.RegisterForDisposal`); `Initialize`
   concatenates it after the durable seed. The RefCount connection re-subscribes the cold Merge —
   a second `SharedView` subscription — and holds it until the hub is disposed.
4. **An entry with a subscriber is never released.** `Entry.TryMarkIdleEvicted` and
   `IsIdleCandidate` refuse while `subscribers > 0`; so does `ReleaseIfUnwatched`, the terminal
   release [#1435](https://github.com/Systemorph/MeshWeaver/issues/1435) added for finished
   activities — it makes "the same atomic zero-subscriber check as the sweep", and answers `false`.
   On Orleans that release was therefore a no-op for every activated node.
5. **The entry keeps the grain alive.** The entry's hydration is a remote sync stream from the cache
   hub to the owner (`MeshNodeStreamHandle.AcquireStream` →
   `AcquireRemoteStreamUnchecked<MeshNode, MeshNodeReference>`). Every 45 s it posts a
   `HeartBeatEvent` to the owner (`JsonSynchronizationStream`, `SyncStreamOptions.HeartbeatInterval`);
   the owner's `HandleHeartBeat` (registered on every node hub by `WithNodeOperationHandlers`) calls
   `GrainKeepAliveCallback.KeepAlive()` → `TryDelayDeactivation(10 min)`. The grain never goes idle,
   so the hub is never disposed, so link 3 never releases.

Grain alive ⇒ entry subscribed ⇒ hydration stream alive ⇒ heartbeats ⇒ grain alive. Nothing outside
the loop can open it: not the idle sweep, not the terminal release, not Orleans' idle collection.
Per activated node it retains the cache entry, the client-side `sync/` hub of the hydration stream
(parked with its lease after the first write — the census's "parked"), that stream's owner-side twin
(the census's "subs"), the data source's primary and the shared collection reduce — the 6–7 per
parent the census could not attribute. `IMeshNodeStreamCache.ReleaseIfUnwatched`'s own remarks
describe the symptom without the cause: *"the heartbeat resets every idle clock the platform has. A
finished import or compile therefore pins its own node hub (plus the sync sub-hubs on both sides)
for as long as the mirror lives."*

Why the per-activity satellites dominate: they are systematic (one per NodeType compile, one per
content import), they are **activated by their own write** (an upsert to a node that is not this
hub's own is `GetMeshNodeStream(path).Update(…)` through the cache, which opens the hydration and
therefore the grain), and nothing ever asked to release them.

## Why the Monolith never had it

`MonolithRoutingService` hands the hub `Observable.Return(enriched)` — one emission, then
completion — with the comment *"One-shot is fine on Monolith — cross-hub MeshNode updates flow
through IDataChangeNotifier / IMeshChangeFeed already."* The same is true on Orleans, and one thing
more: the Orleans cache leg is hydrated by a `SubscribeRequest` routed to **this very grain**, so
after activation everything it could ever emit is an echo of the hub's own state. A write to the
node reaches the owner as the write; a change made elsewhere reaches every process through the
change feed; a recreate is a new activation. The "live updates" the holder's documentation credits
the routing stream with had, on Orleans, no source but the hub itself.

## The change

`MessageHubGrain.CompleteActivation` hands the hub `Observable.Return(node)` — the enriched node it
just resolved — exactly the Monolith's shape. The cache leg stays where it belongs: in the activation
chain, which takes one node and unsubscribes. Nothing about the cache, the sweep, the heartbeat or
the keep-alive changes; the entry simply reaches zero subscribers once activation has settled, and
the release paths the platform already owns can do their work.

## The test, and what it falsifies

`ActivationLeavesNoOwnCacheSubscriberTest` (`test/MeshWeaver.Hosting.Orleans.Test`): create a node,
activate its grain the way a reader does (a `GetMeshNodeStream(path)` read through the client's
cache, released with `FirstAsync`), then wait for `ReleaseIfUnwatched(path)` on the **silo's** cache
to answer `true`.

| grain | result |
|---|---|
| unfixed (`WithOwnNodeStream(sourceStream)`) | `TimeoutException` after the convergence budget — the hub's own subscription is permanent, the entry never reaches zero |
| fixed (`WithOwnNodeStream(Observable.Return(node))`) | released on the first poll |

The wait is a `Where(released).FirstAsync().Timeout(…)` over a 50 ms interval, so a release that
lands a few milliseconds after the read (the activation chain's `Take(1)` disposes its own subscriber
asynchronously) is not a failure and a permanent pin is.

## What this does not claim

- **The live number.** The census that measured the population is the instrument that confirms the
  slope changed; this page is read out of source and pinned by a deterministic test, not re-measured
  on a pod. Read `residual` on a replica carrying this change, twice, minutes apart.
- **The whole residual.** The remaining unindexed streams per activated hub
  ([Read-Path Stream Minting](../ReadPathStreamMinting) §7, and the owner-side shared collection
  reduce / kernel area stream that no census holder counts) are bounded per hub; with the hub itself
  now able to retire, they retire with it.
- **The upsert path's missing terminal release.** `HandleCreateOrUpdateNodeRequest` writes a
  terminal `ActivityLog` through `WriteThroughStream` and never calls `ReleaseIfUnwatched`; on the
  unfixed grain that call could not have succeeded anyway. Whether it should now is a separate,
  smaller question.

## Where the code is

| Piece | File |
|---|---|
| the activation source and the hand-off | `src/MeshWeaver.Hosting.Orleans/MessageHubGrain.cs` (`ComposeActivationSource`, `CompleteActivation`) |
| the lifetime subscription | `src/MeshWeaver.Graph/MeshNodeTypeSource.cs` (constructor, `Initialize`) |
| the subscriber gate | `src/MeshWeaver.Hosting/MeshNodeStreamCache.cs` (`Entry.TryAddSubscriber`, `TryMarkIdleEvicted`, `SharedView`, `ReleaseIfUnwatched`) |
| the heartbeat and the keep-alive | `src/MeshWeaver.Data/Serialization/JsonSynchronizationStream.cs`, `src/MeshWeaver.Mesh.Contract/MeshExtensions.cs` (`HandleHeartBeat`) |
| the Monolith's one-shot | `src/MeshWeaver.Hosting.Monolith/MonolithRoutingService.cs` |
| the pin | `test/MeshWeaver.Hosting.Orleans.Test/ActivationLeavesNoOwnCacheSubscriberTest.cs` |

## Related

- [The `sync/` Hub Population](../SyncHubPopulation) — one hub per stream, two per cross-hub subscription
- [Read-Path Stream Minting](../ReadPathStreamMinting) — the creation path closed before this one
- [The Evicted-Stream Retention](../EvictedStreamRetention) — the parked-lease mechanism this page's link 5 rides on
- [Mesh Node Stream Cache](../MeshNodeStreamCache) — the entry, its subscribers, its idle sweep
- [Mesh Node Versioning](../MeshNodeVersioning) — the durable seed and the version floor the own-node stream feeds
