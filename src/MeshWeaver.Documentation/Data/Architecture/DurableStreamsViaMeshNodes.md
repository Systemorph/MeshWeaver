---
Name: Durable Streams Are Mesh Nodes
Category: Architecture
Description: "The design that retires the Orleans memory stream: what it still carries (three different kinds of traffic), why no durable stream PROVIDER is needed for any of them, and what replaces each — the node's own version chain for data sync, a fast transient NACK for requests, the storage layer's own change feed for cross-silo invalidation, and the _Inbox node pattern for at-least-once work. With the derived-lifetime rule from #2426 applied to every one."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><ellipse cx="12" cy="5" rx="9" ry="3"/><path d="M3 5v14a9 3 0 0 0 18 0V5"/><path d="M3 12a9 3 0 0 0 18 0"/></svg>
---

# Durable Streams Are Mesh Nodes

> **Read first:** [Orleans Stream Pub-Sub Durability](/Doc/Architecture/OrleansStreamPubSubDurability)
> (the defect and why a durable `PubSubStore` did not close it) and
> [Pod-Hub Delivery](/Doc/Architecture/PodHubDeliveryRollPlan) (the transport swap that made the
> stream a *fallback*). This page is the design those two pages end on. Maintainer direction,
> 2026-08-27, recorded on #2320 / #2322: *"we can essentially use mesh nodes for durable streams"*
> — **do not evaluate `Microsoft.Orleans.Streaming.AdoNet`**.

**Status: design, with the first slices landed.** Everything under *Where it stands* names the PR
that shipped it or the issue it is waiting on.

## The one idea

"Use mesh nodes for durable streams" is not one replacement — it is **three different answers**,
because the memory stream carries three different kinds of traffic, and *durable* means something
different for each:

| traffic | what "durable" must mean | the node-backed answer |
|---|---|---|
| **data-synchronization frames** (patches, `DataChangedEvent`) | a lost frame is recovered, never replayed | **already the node** — `MeshNode.Version` + the `BasedOnVersion` resync; nothing to build |
| **request / response** (`SubscribeRequest`, `GetDataRequest`, a NACK) | the requester learns *fast* that the target is not live | **not durability at all** — a transient NACK within milliseconds, so the caller's own recovery runs |
| **cross-silo change notifications** (`IMeshChangeFeed`) | every process sees every commit, or reconciles on start | **the storage layer's own change feed** — PG `LISTEN/NOTIFY`, which is already running on every pod |
| **at-least-once work items** (webhooks, platform builds, payments, inbound mail) | survives a restart, consumed exactly once in effect | **the `_Inbox` node pattern** — already shipped, three consumers in production |

A durable stream *provider* would have bought the first two rows a property they do not need and
left the third and fourth exactly where they are. That is why the provider decision went the other
way, and why the memory stream can be retired without a successor of the same shape.

## What the memory stream still carries — measured on `main`, not remembered

Registered once, `silo.AddMemoryStreams(StreamProviders.Memory)` in
`OrleansServerRegistryExtensions.cs`. Every remaining user, from a grep of `GetStreamProvider` /
`GetStream<` across `src/`:

| user | what rides the stream | who consumes it | if a frame is lost |
|---|---|---|---|
| `RoutingGrain.BuildPodHubRoute` → `FallBackToStream` | a delivery to a stream-routed address (`portal`, `client`, `cache`, `mesh`, `import`) **only after** the directed `IPodHubGrain.Deliver` threw `PodHubNotHereException` | the owner's `SubscribeWhenStreamingReadyAsync` subscription | the requester waits out its budget (#2320, #2322, #2406) |
| `RoutingGrain.PostFailure` → `PublishFailureOverStream` | a `DeliveryFailure` to a stream-routed sender, when the directed NACK failed for any reason | same | the sender waits out its budget |
| `OrleansMeshChangeFeed.BroadcastAsync` | every `MeshChangeEvent` (Created / Updated / Deleted), one stream per kind | `PathCacheInvalidatorGrain` on every other silo → `InProcessMeshChangeFeed.PublishLocal` | **permanent, silent, per-node staleness** on every other silo — the path-resolution cache, the remote-stream cache, `NodeTypeRebindWatcher`, `EventSubscriptionRunner`, `AccessGrantNotifier`, `SyncedQueryMeshNodes` all miss it for the life of the process |
| `RootMeshHubReplyStreamService` | the `mesh/{id}` root hub's *subscription* (not a publisher) | the root hub | cross-silo replies to the root hub — served by the directed call since the swap; the stream is its fallback |

Two facts that change the shape of the design, both verified from source:

1. **No production process hosts hubs as an Orleans client.** `UseOrleansMeshClient` has exactly
   two callers, both test fixtures (`OrleansMeshTestBase`, `OrleansDocumentationTest`). `Memex.Portal.Distributed`
   is a co-hosted silo; the monolith, `LocalMesh` and the bake host run no Orleans at all. So *"a
   client cannot host a grain, therefore it keeps the stream permanently"* — the standing
   justification for the fallback — describes the **test rig**, not the fleet.
2. **The database's own cross-process feed is live in production.** `AddPartitionedPostgreSqlPersistence`
   registers `PostgreSqlChangeListener` *and* the `IHostedService` that opens its `LISTEN mesh_node_changes`
   session (`PostgreSqlExtensions.cs`, pinned by `ChangeListenerWiringTests` since #1814/#1816).
   Every commit on any `mesh_nodes` table fires `notify_mesh_node_changes()`, which already
   de-duplicates no-op updates. The feed carries `{path, op}` and surfaces as
   `IStorageAdapter.Changes` (`DataChangeNotification`, `Entity = null`). Several code comments still
   say this session "is not started in the partitioned wiring"; they date from before #1816 and
   are wrong — see *Stale claims* below.

## 1 · Data synchronization — already node-backed, nothing to build

A `SynchronizationStream` mirror does not need the transport to be lossless: every patch carries
`BasedOnVersion`, a gap is detected on arrival, and the mirror re-requests from the node — whose
`Version` is the durable, monotonic revision counter. That IS the durable stream, and it is why
the earlier "frame loss" storms (#1384, #2641) were *resync* storms rather than data loss.

What the transport owes this row is only **honest classification**: a fault that is a lifecycle
transition (a silo departing, a grain-directory handover, a container disposing) must reach the
mirror as `ErrorType.ShuttingDown`, which the resubscribe latch rides out — never as a terminal
`Failed`, which tears the mirror down. #2518 (directory instability), #2647 (scope teardown) and
#2645 (attach retry) are that work. **Kept, unchanged.**

## 2 · Request / response — retire the stream from the routing leg

A request whose target is not live should fail in **milliseconds, transiently**. The stream
fallback does the opposite twice over: a publish into a stream with no live subscriber *succeeds
and discards* (the subscriber probe narrows this but fails open by design), and a publish into a
stream whose queue grain is wedged or whose producer never registered *stalls* for 30–60 s
(#2322, #2320, #2406 — all three are Orleans-internal, and there is no MeshWeaver line to change
on that path). Durability cannot fix a request that should not have been queued.

The design is the roll plan's **release N+2**, made precise:

- **`PodHubNotHere` after the directed call is answered with a transient NACK, not a publish.**
  `RoutingGrain.BuildPodHubRoute`'s `FallBackToStream()` arm becomes
  `PostFailure(…, ErrorType.ShuttingDown, TargetUnserved: true)` — the same verdict the subscriber
  probe already produces for "no silo serves this hub" (#1742), now reached in one hop.
  `SynchronizationStream` and `MeshNodeStreamCache` ride it out; a requester gets its answer
  inside the directed call's own budget instead of the stream's.
- **The owner's claim gets a DERIVED lifetime.** `OrleansRoutingService.AttachPodHub` used to make
  six attempts over ≈3 s and then give up at `Debug` — after which a silo-hosted hub kept the
  stream *forever*, invisibly (the only signal was the router-side fallback line). The claim
  now retries with its capped backoff **until one of two real terminals**: the hub's
  registration is disposed, or `IHostApplicationLifetime.ApplicationStopping` fires (the gate
  `GrainWhileRunning` already expresses). Once the initial budget is exhausted **on a process that
  can host grains** the line is `Warning` naming the hub — abnormal, and the fleet has
  no clients for which it would be noise. This is the #2426 rule applied to the claim: no
  cleanup message a restarting process would never send, only lifetimes derived from the hub and
  the host.
  - **A third terminal, and it is derived too: IMPOSSIBILITY.** A process that cannot host a grain
    can never win the claim — `PodHubGrain` is `[PreferLocalPlacement]`, so from a cluster client
    the activation lands on some silo with no local route and answers `false`, for ever. There the
    initial budget *is* the end and the give-up stays at `Debug`, because that is the expected
    permanent outcome. Retrying it would not be a lifetime, it would be a poll that cannot
    converge — and a measurable one: every attempt makes the *silo* log `[POD-HUB] Attach … landed
    on a silo that has no local route` at `Information`, i.e. one line per hub per backoff
    interval, which is the storm shape #2426/#2546 exist to remove. The discriminator is Orleans'
    own: `ILocalSiloDetails` is registered by `DefaultSiloServices` and by nothing else.
- **The stream stays only for CLIENT-hosted address types, by declaration.** The fallback is gated
  on the address type being declared client-hosted (`MeshBuilder.AddClientHostedAddressType`),
  **never** on the grain answering "not here".
  In production no address type is declared client-hosted, so the router never publishes. The
  Orleans test rig declares all four built-in stream-routed types — it hosts a hub of each on its
  cluster client (`client/{id}` from `GetClient`, the client host's own root `mesh/{guid}`,
  `portal/{guid}` in the documentation/graph/markdown tests, and the client's `cache` hub) — and
  keeps its stream until the rig hosts its hubs on a silo, at which point `AddMemoryStreams` and
  `PubSubStore` go with it.
- **The verdict is `ShuttingDown` + `TargetUnserved`, and the owner-side eviction had to be
  re-gated to see it.** `DataExtensions.HandleTargetUnservedFailure` — the #2426/#2546 fix that
  stops an owner fanning changes out to a dead subscriber forever — required
  `TargetUnserved && ErrorType == NotFound`. That second test was redundant belt-and-braces from
  the era when the *only* producer of the stamp was the stream leg's subscriber probe; left in
  place it would have made the new one-hop verdict **inert**, silently re-opening the leak for
  every dead circuit in the fleet. The gate is now the STAMP alone, which is what
  `DeliveryFailure.TargetUnserved`'s own contract always said ("only the router … may stamp
  this"). The two facts are complementary rather than contradictory: the **subscriber** rides
  `ShuttingDown` out and re-asks, while the **owner** drops the server-side half it can no longer
  push to.
- **`StreamMessageSizeGuard` (#1890) needs no code change, and did not get one.** The guard exists
  to turn a *silent* drop into a loud, NACK'd refusal: an oversized payload on the memory stream
  succeeds at the publish and dies inside `PersistentStreamPullingAgent`'s non-convergent retry
  loop, naming only a queue id. On the directed call the wall is Orleans' `MaxMessageBodySize` and
  crossing it **throws**, which `BuildPodHubRoute`'s `TerminalCallFailure` already turns into a
  classified `DeliveryFailure` naming the address, the delivery id and the sender — the outcome
  the guard exists to produce. Retargeting the constant itself (plumbing `SiloMessagingOptions`
  into `RoutingGrain` plus its own refusal shape) is a separate change and is *not* required for
  correctness here; the guard stays on `PostToStream`, which after this slice is reachable in
  production for nothing at all.

### The N+2 gate, and why these two slices shipped without waiting for it

**The roll plan's gate was**: a full rolling deploy with none of `[ROUTE] Pod-hub grain for
{Address} is not attached — falling back to the stream publish` in Loki. That gate measured a
*risk that the two slices above jointly remove*, so it was satisfied by construction rather than
by observation:

- The gate's worry is the window "the owner exists, but its claim has not landed" — in which
  removing the fallback would drop a delivery to a live hub. **Slice 2 does not drop it: it
  ANSWERS it**, with a transient NACK inside the directed call's own budget. Every consumer on
  that path already has recovery machinery armed for exactly this verdict
  (`SynchronizationStream`'s resubscribe latch, `MeshNodeStreamCache.IsTransientOwnerFailure`,
  `MeshNodeStreamExtensions`' paced retry), which is why the verdict is `ShuttingDown` and not
  `NotFound`. The pre-change alternative was strictly worse for the same window: a publish that
  *succeeds and discards* when nobody is subscribed, or stalls 30–60 s on a wedged queue grain —
  the failure with **no** signal at all.
- **Slice 1 closes the window rather than surviving it.** The claim now retries until it lands, so
  "owner exists, claim not landed" resolves by retry instead of persisting for the life of the
  process. Before slice 1 it could persist forever, which is precisely why the gate was needed.
- And the gate's own instrument was unreliable in the direction that matters: the line it counts
  is `Information`, emitted per delivery, and **its absence was never proof** (the page said so).
  A `Warning` naming the hub, emitted once per claim that has not landed, is a strictly better
  instrument — and it is what slice 1 adds. The fallback line survives, but after slice 2 only a
  DECLARED client-hosted type can reach it, so in the fleet it cannot be emitted at all.

Residual risk, stated plainly: an address type that is genuinely client-hosted in some deployment
and was never declared would go from "works over the stream" to "NACK'd transiently". No such
deployment exists — `UseOrleansMeshClient` has only test-fixture callers — and the symptom would
be loud (a windowed `Warning` naming the address) rather than silent.

## 3 · Cross-silo change notifications — the storage layer's feed is the durable channel

This is the only memory-stream user whose loss is **permanent**, and it is also the one with a
ready-made durable substitute that the platform already operates, monitors and backs up.

Today there are two parallel cross-process channels for the same commit:

```
write ──commit──► pg_notify('mesh_node_changes', {path, op})  ──LISTEN──► IStorageAdapter.Changes  ─► synced queries re-run,
                                                                                                    MeshDataSource reconcile re-reads
      └─post-commit─► IMeshChangeFeed.Publish(MeshChangeEvent)  ─local Subject─► consumers
                                └─► Orleans memory stream "mesh-{kind}" ─► PathCacheInvalidatorGrain (other silos) ─► PublishLocal
```

The design collapses the second cross-process leg onto the first:

1. **The NOTIFY payload carries `nodeType` and `version`** beside `path` and `op` — the trigger
   already has `NEW.node_type` / `NEW.version` in hand. `DataChangeNotification` gains
   `NodeType` and `Version` as **optional** members (additive; every existing producer passes
   `null`). Core lands first, the trigger change in the PostgreSql adapter second; the relay below
   tolerates a payload without them by re-reading the node before any consumer that filters on
   type sees the event.
2. **A relay, not a grain.** A mesh-scoped singleton `StorageChangeFeedRelay` subscribes
   `IStorageAdapter.Changes` and relays each notification into
   `InProcessMeshChangeFeed.PublishLocal(MeshChangeEvent)`. It replaces `OrleansMeshChangeFeed`'s
   broadcast queue and `PathCacheInvalidatorGrain` entirely; `IMeshChangeFeed` is
   `InProcessMeshChangeFeed` on every host, and a monolith or `LocalMesh` process that shares a
   database with another process gains cross-process invalidation it never had.
3. **Own-write echoes are already tolerated.** Every consumer of `IMeshChangeFeed` is an
   invalidation or a `Pending → Fired`-gated continuation, and on the publishing silo they *already*
   receive each own write twice — once from `Publish`, once relayed back by that silo's own
   `PathCacheInvalidatorGrain`. The relay changes the second delivery's transport, not its
   existence.
4. **Loss semantics are strictly better.** A NOTIFY is missed only inside the listener's own
   reconnect window (a 5 s retry loop, logged at `Error`), which is the window the synced queries
   already accept and the reconcile-on-start pattern in
   [Event Subscriptions](/Doc/Architecture/EventSubscriptions) already covers. There is no
   rendezvous grain to time out, no queue grain to lose its RAM, and no membership handover in
   the path — the three mechanisms behind #2320, #2322 and #2406.
5. **Backend-agnostic by construction.** Cosmos and Snowflake already feed `Changes` with
   `Entity = null`; the in-memory and file-system adapters feed it in-process. The relay does not
   know which one it is on.

## 4 · At-least-once work items — the `_Inbox` node is the durable stream

Where a message genuinely must **outlive the process that will handle it**, the node-backed stream
already exists and is documented: [Webhook Inbox](/Doc/Architecture/WebhookInbox). Its contract, restated
here because it is exactly the contract a durable stream needs:

- **an entry is a write-once node** at `{target}/_Inbox/{id}` — its own existence is its lifetime;
- **the consumer is a live children query** taking `Initial | Added | Reset`, processed with
  `Concat` (one at a time) — `Initial` is the replay-on-start leg, so an entry written while the
  consumer was down is delivered when it comes up;
- **the ack is a delete**, under system identity, on *every* outcome including "unverifiable"
  and "irrelevant" — a poison entry is dropped, never looped; every action is idempotent because
  replay is the normal case, not the exception;
- **the consumer lives on an always-on hub.** A drain armed on an on-demand per-instance hub runs
  only while somebody happens to be looking at that node — Plugins#777 is precisely that defect
  (`Hosting/Deployment`'s inbox watcher stopped consuming platform-build announcements until a
  Deployment page was opened). A fleet-wide consumer belongs beside a hub warmer or in a
  host-level hosted service, and wants the positive liveness signal *"the inbox is non-empty and
  ageing"*.

## The derived-lifetime rule, applied

#2426 found a server-side subscription that only an explicit `UnsubscribeRequest` could dispose —
immortal by construction, because a portal that *restarts* never sends one. Every row above is
checked against it:

| row | the lifetime, and what derives it |
|---|---|
| data sync | the mirror's subscription; ended by the owner's `TargetUnserved` verdict (#2620) or the subscriber's own disposal |
| request / response | the pod-hub claim; ended by hub disposal or `ApplicationStopping` — never by a message |
| change notifications | the LISTEN session; owned by the process, ended by host stop |
| `_Inbox` entries | the entry's own existence; ended by the consumer's delete |

## Where it stands, and the order of work

| slice | state | note |
|---|---|---|
| classification of lifecycle faults as transient | **landed** — #2518, #2645, #2647 | row 1 needs nothing else |
| this design | **this page** | records the direction on #1742, #2320, #2322, #2406 |
| stale "listener never started" comments corrected | **landed with this page** | see below |
| pod-hub claim: indefinite, derived lifetime, `Warning` where grains can be hosted | **landed** — #2745 | closed the #1742 residual *"a claim that fails to land degrades silently"*; core only |
| routing: transient NACK on `PodHubNotHere`; fallback gated on declared client-hosted types | **landed** — #2745 | closed #2320, #2322, #2406 as *made unreachable*. Shipped with slice 1 rather than after a clean roll — see *The N+2 gate* above for why the two together satisfy it by construction |
| owner-side eviction re-gated on the `TargetUnserved` STAMP alone | **landed with the slice above** | required by it: gating on `NotFound` would have made the new verdict inert and re-opened #2426/#2546 |
| `DataChangeNotification.NodeType/Version` + `StorageChangeFeedRelay` | next | **core first**; the relay must tolerate a payload without the new fields |
| `notify_mesh_node_changes()` emits `nodeType`, `version` | after the core slice | PostgreSql adapter (MeshWeaver.Plugins); a schema-initializer revision, re-applied by the existing DROP-then-CREATE |
| delete `OrleansMeshChangeFeed` broadcast + `PathCacheInvalidatorGrain` | after both | the memory stream then carries routing fallback only |
| `StreamMessageSizeGuard` retarget onto `MaxMessageBodySize` | optional, not blocking | the directed call already THROWS at that wall, which is the outcome the guard produces — see the bullet above |
| test rig hosts hubs on a silo → `AddMemoryStreams` deleted | last | the only remaining user |

## How to check a live cluster

```
# 🚨 Must be EMPTY, always, not merely after a roll: only an address type DECLARED client-hosted
# can reach this line, and production declares none. A hit means somebody added a declaration.
{namespace="memex-cloud"} |= "falling back to the stream publish"

# THE instrument now — a hub whose claim has not landed, once per claim, naming the address.
# The claim keeps retrying, so a matching "landed after its initial budget was exhausted" line
# for the same address is the resolution; one without it is a hub still on no transport at all.
{namespace="memex-cloud"} |= "Pod-hub claim for" |= "did not land"

# the transient NACK that replaced the publish — windowed to one Warning per address per 60 s
{namespace="memex-cloud"} |= "was refused: no silo in this cluster is currently serving that hub"

# the database feed is up on every pod — one line per pod per LISTEN (re)connect
{namespace="memex-cloud"} |= "PostgreSQL LISTEN started on mesh_node_changes"

# the stream-provider failure family this design retires
{namespace="memex-cloud"} |~ "RegisterAsStreamProducer failed|memorystreamqueue.*Enqueue"
```

## Stale claims this page corrects

Code comments and doc pages in core (`BuildCoordinationExtensions`, `BuildNodeType`,
`BuildProtocolDriver`, `RegistryUpdateReconciler`, [Build Coordination](/Doc/Architecture/BuildCoordination),
[Plugin Update on Green Build](/Doc/Architecture/PluginUpdateOnGreenBuild)) stated that
`PostgreSqlChangeListener` is *"registered and never started in either partitioned-PG overload"*;
the PostgreSql adapter's `PostgreSqlPathRoutingAdapter` (in MeshWeaver.Plugins) still says *"the
pg_notify LISTEN fallback is disabled for partitioned PG"*. That was #1440 as filed on 2026-08-13;
it was the middle leg of the #1814 outage and was fixed by #1816 — the partitioned overload now
registers the hosted service that opens the session, and `ChangeListenerWiringTests` fails the
build if it ever stops. The *conclusions* those comments draw (read the durable witness, never wait
to be told — `BuildProtocolDriver`, `BuildNodeType.ArbitrateDurably`, `ObserveBuildGo`,
`RegistryUpdateReconciler`) remain correct for a different reason: a NOTIFY is delivered to a *live*
LISTEN session on the *same* database and is never replayed, so a mirror that activates after the
write — or a deployment on another database — is still not told. Every core occurrence is corrected
with this page; the adapter's one is corrected in its own repo.

## Related

- [Orleans Stream Pub-Sub Durability](/Doc/Architecture/OrleansStreamPubSubDurability) — the defect,
  the durable `PubSubStore`, and the two residuals this page answers
- [Pod-Hub Delivery](/Doc/Architecture/PodHubDeliveryRollPlan) — the transport swap and the N+2 gate
- [Event Subscriptions](/Doc/Architecture/EventSubscriptions) — the live + reconcile-on-start pattern
- [Webhook Inbox](/Doc/Architecture/WebhookInbox) — the `_Inbox` node contract
- [Error Propagation & Wedges](/Doc/Architecture/ErrorPropagationAndWedges) — "an undeliverable
  delivery must surface as a `DeliveryFailure`, never as silence"
- Issues: #1742, #2320, #2322, #2406, #2426, Plugins#777
