---
Name: The /api/content 503 — Two Causes, One Discriminator
Category: Architecture
Description: "A content file that answers 503 after exactly ~10.3 s has burned ReadBudget.Default on a collection-config read. Only two things produce that, they need opposite fixes, and the line the framework already prints tells them apart in one field."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6"/><path d="M12 11v4"/><path d="M12 18h.01"/></svg>
---

# The `/api/content` 503 — two causes, one discriminator

`/api/content/{node}/{collection}/{file}` answering **503 after ~10.3 s** is not a class of failure.
It is one failure with two possible causes, and the fix for one does nothing for the other. This
page is the elimination: what the 10 s is, what it is *not*, and the single field that decides.

Filed as [#2901](https://github.com/Systemorph/MeshWeaver/issues/2901) — *"a freshly uploaded
content file 503s for minutes"*.

## 🚨 The one sentence

> **The 10 s is `ReadBudget.Default` expiring on ONE request — the collection-config
> `GetDataRequest` — and it expires only when no notification reached the ISSUING hub's callback
> in time. Either the reply could not be DELIVERED there (a lost pod-hub claim), or it was
> delivered and not DISPATCHED (that hub's action block was saturated). Nothing else on the route
> produces 10.3 s.**

🚨 **The issuing hub used to be `portal/nodeops-{meshId}` — the mesh's node-CRUD EXECUTION hub —
which is what made the second case reachable from an ordinary upload. It is now
`portal/reads-{meshId}`, a hub that registers no handlers — see *"Cause B's cure"* below. The
elimination
below is written against the pre-fix code, because that is what the production occurrences ran, and
because it is still the map of the route.**

## The read path, and where the 10 s lives

| # | Step | Where | Bounded? |
|---|---|---|---|
| 1 | decode + traversal guard | `BlazorHostingExtensions.ResolveContentFile` (Plugins, `:791-806`) | n/a |
| 2 | **resolve the owning node** — `IPathResolver.ResolvePath` | `ContentFileResolver.cs:114-117` | **NO — unbounded** |
| 3 | **read the collection config** — `GetDataRequest(ContentCollectionReference)` to the owner | `ContentFileResolver.cs:183-193` | **`ReadBudget.Default` = 10 s** (`ReadBudget.cs:92`) |
| 4 | mount check (`IsStatic`) | Plugins `:823-824` | n/a |
| 5 | permission gate, tri-state | Plugins `AllowContentRead` `:595-605` / `GatedContentRead` `:631-646` | fold-local |
| 6 | open the collection + read the bytes | `ContentService.GetCollection` → `ContentCollection.GetContent`, on the collection's `IIoPool` | pool-bounded |

Step 3 is the only 10-second bound on the path. `FailIfNoFirstEmission` (`ReadBudget.cs:120-134`)
is an `Observable.Amb` of the request and a 10 s timer; when the timer wins it throws
`HubUnreachableException`, a `TimeoutException` subclass, which
`BlazorHostingExtensions.ContentFailure` maps to **503** (Plugins, `:746-748`). So a measurement of
*~10.2–10.8 s then 503* is `ResolvePath` (a few hundred ms) plus the budget, exactly.

**Step 3 is issued from, and answered onto, a hub that is not the caller's own.** The
`/api/content` endpoint holds the DI-injected `IMessageHub`, which in the mesh's root container *is*
the router, and the router must be neither end of a delivery — so `ContentFileResolver` hops onto a
dedicated off-router hub. **Until this page's fix that hop landed on `portal/nodeops-{meshId}`,
the mesh's one node-operation hub** (`hub.NodeOperationIssuingHub()`); it now lands on
`portal/reads-{meshId}` (`hub.ReadIssuingHub()`). Either way the reply must be dispatched by *that
hub's* single-threaded action block before `hub.Observe` emits (`MessageHub.cs:451` registers
`HandleCallbacks` as a delivery rule, and every delivery — responses included — goes through
`EnqueueTurn`, `MessageService.cs:906`). Which hub it is decides what else can be in that block's
way.

## What is provably NOT the cause

Each of these was a candidate on #2901. Each answers, and answering is not silence.

| Candidate | Why it cannot produce 10.3 s |
|---|---|
| **The config handler is slow / propagating** | `HandleCollectionConfigRequest` (`ContentCollectionsExtensions.cs:300-346`) is a pure in-memory dictionary read followed by `hub.Post`. There is nothing to settle. |
| **The owning node is not content-enabled** | The generic workspace read answers anyway: `GetDataResponseObservable<TReference>` returns `GetDataResponse(null, 0)` when the reduce manager resolves no stream (`DataExtensions.cs:3092-3104`). `ReadCollectionConfigs` then yields null → **404**, in milliseconds. |
| **No handler matched at all** | `MessageHub.FinishDelivery` NACKs any unhandled `IRequest<>` with a typed `DeliveryFailure(NotFound)` (`MessageHub.cs:936-965`). |
| **The permission fold reached no verdict** | `AccessControlPipeline` answers `DeliveryFailure` with `Unavailable` / `ShuttingDown` (`AccessControlPipeline.cs:618-641`) — a fast 503, not a silent one. |
| **A storm breaker latched by the first early read** | The per-key breaker trips at **2000 messages/second on one key** (`MessageStormBreaker.cs:92`) with a 2 s cooldown (`:100`); the aggregate shed only touches `[CanBeIgnored]` traffic (`:505`) and `GetDataRequest` is not (`Messages.cs:318-319`). A browser cannot reach either. |
| **Per-node hub warm-up** | Refuted by the issue's own measurement: `AgenticPrimer/content/og.png` answered in 0.9 s while `AgenticPrimer/content/videos/skills.mp4` — the *same* node, the *same* `GetDataRequest` — took 10.3 s in the same window. An activating hub cannot answer one and drop the other. |
| **A settle window on the uploaded file** | Nothing on the path is keyed to the file. The bytes are read directly from the store; the blob provider has no monitor at all (`AzureBlobStreamProvider.AttachMonitor` returns null), and the file-system monitor drops every non-`.md` event at the watcher callback (`FileSystemStreamProvider.cs:272-277`), so 32 binaries neither ingest nor lengthen collection init. |

### The per-file appearance is a cache key, not a settle window

The one per-file thing on the whole path is `PathResolutionService`'s value cache, whose key is the
**full joined path including the file name** (`PathResolutionService.cs:442-505`). So every distinct
file URL is its own entry and its own storage query on first request, and a warm URL replays
synchronously (`:456-458`) while a cold sibling pays a live cross-schema query. That produces
per-file *latency* differences. It does not produce the 503 — step 3 is identical for both files.

## Cause A — the reply cannot be delivered

The owning hub produces the `GetDataResponse` and the router refuses to carry it, because the
grain directory holds no claim for `portal/nodeops-{meshId}`.

`portal` is a stream-routed address type (`MeshConfiguration.cs:69-70`), so `RoutingGrain` takes the
directed pod-hub route. On a refusal it reaches `AnswerPodHubNotHere`
(`RoutingGrain.cs:610-652`), and `ClientHostedAddressTypes` is **empty in production by explicit
design** (`MeshConfiguration.cs:78-82`) — so there is no stream fallback. The transient
`DeliveryFailure` is posted **to the sender** (`RoutingGrain.cs:650`), i.e. to the *responding* node
hub. The requester is told nothing and can only wait out its budget.

This is [#2938 / #2915](https://github.com/Systemorph/MeshWeaver/issues/2938), root-caused on
[#2901](https://github.com/Systemorph/MeshWeaver/issues/2901) on 2026-09-02 from production logs
(33 occurrences in 24 h, every one on a single pod, onset in the same hour as a silo death). The
cure is [The Pod-Hub Claim Must Be Re-Asserted](../PodHubClaimReassertion), merged as `f37f3fc87`.

**Signature.** The `HubUnreachableException` message carries the reader's own snapshot
(`ReadBudget.cs:234-245` → `MessageHub.GetPendingRequestDiagnostics`, `:2116-2136`):

```text
Reader: Hub portal/nodeops-… RunLevel=Started Queue(buffer=0,deferred=0,exec=0)
        PendingCallbacks=1[…=GetDataRequest@DoublePendulum(10003ms)]
```

**Empty queue, nothing executing.** The reader processed everything delivered to it; the silence is
upstream. Peer pods carry the matching `[ROUTE] Directed delivery to pod hub '…' was refused` line.

## Cause B — the reply is delivered and not dispatched

`portal/nodeops-{meshId}` is *also* the mesh's ONE node-CRUD execution hub. Every
`CreateNodeRequest` / `CreateOrUpdateNodeRequest` issued from the router runs there and is
serialised on the same action block (`MeshService.cs:93` issues on it, `:104-108` targets
`NodeOperationTarget`, whose fallback is that same hub, `MeshExtensions.cs:325-326`). The turn loop
runs exactly one turn at a time and does not advance until the current turn's observable completes
(`MessageService.cs:945-1006`).

That this hub stops draining for tens of seconds under a bulk node-CRUD burst is **measured**, from
a different direction, in [Bake Seal — NodeOps Saturation](../BakeSealNodeOpsSaturation): queue
latency bimodal at ≤ 3.2 s / 33–49 s, and one capture of
`Queue(buffer=45,…) Executing(CreateNodeRequest, 24888ms)`.

**A content upload burst is a node-CRUD burst.** With the indexing pipeline registered, each
uploaded file starts its own Activity — and `ContentIndexingObserver.OnUploaded` fires them
**unbounded and fully parallel**, one per file (Plugins,
`ContentIndexingObserver.cs:107-116`; contrast `ReindexAll`, which sequences the identical work with
`.Concat()` at `:270`). Per file, on `portal/nodeops-{meshId}`:

| Work | Deliveries on that action block |
|---|---|
| `meshService.CreateNode` for the `_Activity` node (`ContentIndexingActivity.cs:123`) | request + response |
| `MeshDocumentSink.WriteDocument` → `CreateOrUpdateNodeRequest` for `_Documents/{slug}` (`MeshDocumentSink.cs:64-65`) | request + response |

…plus one fresh per-node hub activation for `{owner}/_Activity/{id}`, routed through the mesh
router's own single-threaded block, and ~3 activity-log `stream.Update`s on `cache/{meshId}`.
Thirty-two files is on the order of a hundred deliveries. The burst does not drain when the files
are written — it drains when the slowest indexing leg finishes, and for image posters that leg is a
**vision-model round trip on the `Http` pool, capped at 16** (`ContentIndexingService.cs:215-224`;
`IoPoolOptions.cs:160`). That is where *minutes* comes from, and why it heals untouched.

**Signature.**

```text
Reader: Hub portal/nodeops-… RunLevel=Started Queue(buffer=N>0,deferred=0,exec=…)
        Executing(<message>, <thousands>ms) PendingCallbacks=…
```

## 🚨 The discriminator

The two causes are separated by **one field in a line the framework already prints** — no new
instrumentation, no cluster access beyond the log:

```text
Queue(buffer=0,…) and no Executing(…)   ⇒ Cause A: the reply never arrived.
Queue(buffer>0,…) or Executing(…, Nms)  ⇒ Cause B: the reply arrived and waited.
```

Grep the portal for `Reading content collection config from` and read the `Queue(` on the same line.
`grep -o "Queue(buffer=[0-9]*"` over a window, bucketed, answers it for a whole day at once.

**Do not apply Cause A's fix to a Cause B occurrence.** Re-asserting a claim that was never lost
changes nothing, and a single-replica or restarted portal removes the *exposure* to Cause A without
touching Cause B — which is why a 12/12 green probe proves neither.

## 🚨 Cause B's cure — the read does not belong on that block at all

The fix is not to make the node-CRUD hub drain faster. It is that **a bounded read with a person
waiting on it must never be issued on the hub that EXECUTES the mesh's node CRUD**, whatever that
CRUD costs. A serial execution hub occupied by a bulk write burst is that hub working as designed;
a ten-second interactive read queued behind it is not.

`MeshExtensions.ReadIssuingHub()` is the seam. It hops a **router**-held caller onto
`portal/reads-{meshId}` — a hub wired exactly as `portal/nodeops-{meshId}` is (the mesh's own type
registry, the mesh hub's permission evaluator, registered with the routing service so replies land
on it cross-silo) with **one deliberate difference: it registers no handlers.** Nothing executes
there, so the only thing its action block ever dispatches is the reply to a read issued on it. Two
call sites moved:

| Read | Was | Now |
|---|---|---|
| `ContentFileResolver.Resolve` — the collection-config `GetDataRequest` (step 3 above) | `NodeOperationIssuingHub()` ⇒ `portal/nodeops-{meshId}` | `ReadIssuingHub()` ⇒ `portal/reads-{meshId}` |
| `MeshNodeStreamExtensions.GetMeshNodeOutcome` — the one-shot node read | same | same |

`NodeOperationIssuingHub()` is unchanged and still correct for a **write**: a target-less
`CreateOrUpdateNodeRequest` posted there executes on the node-CRUD hub, and a write ack that waits
behind the writes queued ahead of it is the ordering that hub exists to impose. The two seams differ
because reads and writes want opposite things from the same block.

### The repro

`ContentReadIsNotQueuedBehindNodeCrudTest` (`test/MeshWeaver.Graph.Test`) — a monolith mesh, no
sleeps, no cluster. An `INodeValidator` parks the create of ONE node (matched by path, so nothing
else in the mesh is slowed), which holds the node-CRUD execution hub's turn exactly as a real write
does; with the block held, `ContentFileResolver.Resolve` must still answer. Reverting the two call
sites to `NodeOperationIssuingHub()` reproduces this page's Cause B **verbatim**, including the
discriminator:

```text
Reading content collection config from 'TestData/ContentProbe' gave up after 10s — the owning hub
never answered. … Reader: Hub portal/nodeops-GMUrd3FU90aD8lxUt_XUlw RunLevel=Started
Queue(buffer=1,deferred=0,exec=0) Executing(CreateNodeResponse, 10004ms)
PendingCallbacks=1[…=GetDataRequest@TestData/ContentProbe(10002ms)]
```

`Queue(buffer=1)` and an `Executing(…, 10004ms)` line — Cause B by the discriminator above, on a
run where the owning hub answered promptly. Note *what* is executing: the create's continuation is
running inside the turn that delivered an intermediate `CreateNodeResponse` to that hub, so the
chain's remaining legs are charged to a node-CRUD delivery turn. That is the mechanism by which a
single create can occupy the block far longer than any one of its own steps.

### What it does NOT fix

**Nothing about how long a node-CRUD turn takes.** [#2543](https://github.com/Systemorph/MeshWeaver/issues/2543)
— the same hub seen from the write side, where a `CreateNodeRequest` turn was captured at 24 888 ms
and queue latency is bimodal at ≤ 3.2 s / 33–49 s — is untouched and stays open. What this change
does take off that block is the **router-issued reads**: the `PendingCallbacks=26[GetDataRequest@Store/Core,
@Store/Install, …]` in #2543's own capture are `GetMeshNode` reads issued from mesh-singleton
services that hold the DI root hub, and every one of them now registers on `portal/reads-{meshId}`
instead. That removes a contributor and, more usefully, removes a confound: pending callbacks left on
`portal/nodeops` after this change were issued by something running **on that hub**, not by a
router-held caller.

## Recorded, not fixed

Two unbounded seams on this exact path. Neither causes the 503; both make an occurrence harder to
read.

1. **`ResolvePath` is unbounded here** (`ContentFileResolver.cs:114-117`) while the routing path
   bounds the identical call at 30 s (`RoutingServiceBase.cs:304-306`). A resolution that never
   emits — a documented shape, pinned by `PathResolutionCachePoisonTest.HungFirstQuery_DoesNotPoisonCache` —
   never even subscribes step 3's budget timer, so the request hangs until the client aborts with
   **no 503 and no log line**. Strictly worse than the failure this page is about.
2. **The issuing hub is re-resolved per request** (`ContentFileResolver.cs`, now `ReadIssuingHub()`)
   rather than cached as `MeshService.cs:93` does. During mesh teardown it returns **the router**
   (`MeshExtensions.MeshReadHub` returns null past `DisposeHostedHubs`), reinstating the
   router-as-both-ends hang the long comment above that call exists to prevent.

And one in the plugins repo: the per-file indexing fan-out has **no concurrency bound**, while its
own sibling walk does.

## On answering "not ready" with 409/425

Half of #2901's second ask is already true: the 503 is not generic. It is the deliberate answer for
*"no verdict was reached"*, kept distinct from the 404 a missing or refused file gets — see
`ContentUnavailable` and `PermissionApi` → *"The anonymous gate is tri-state too"*. What stands is
that 503 is also the code for *down*, so a monitor cannot separate the two. Under the analysis above
that complaint has no "still settling" case left to serve: both causes are genuine unavailability of
a dependency, and both are correctly 503. A distinct status would only be warranted if a
*ready-later* state existed; none does.

## Related

- [Bake Seal — NodeOps Saturation](../BakeSealNodeOpsSaturation) — the same hub, measured from the write side, with the open question of *which rule* holds its block.
- [The Pod-Hub Claim Must Be Re-Asserted](../PodHubClaimReassertion) — Cause A's mechanism and cure.
- [Action-Block Wedge Prevention](../ActionBlockWedgePrevention) — the invariants a single-threaded hub must satisfy.
- [Bounds Must Be Ordered](../BoundsMustBeOrdered) — why an inner bound just under an outer one destroys the outer one's diagnosis.
- [Controlled I/O Pooling](../ControlledIoPooling) — where per-file work belongs instead of on an action block.
- [CQRS and Content Access](../CqrsAndContentAccess) — why the collection-config read is a request/response and deliberately uncached.
