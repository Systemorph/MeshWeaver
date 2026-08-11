---
Name: "Owner Injection — the standing access identity for a node's hub"
Abstract: "In any node/thread/activity context the NODE OWNER (resolved from the node) is the standing access context, injected everywhere and carried forward across Rx hops via the owning hub's standing identity. Genuine infrastructure (doc sync, cache hydration) runs as System. An empty access context is NEVER faked — it is rejected instantly."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#00897b'/><circle cx='12' cy='8' r='3.2' fill='white'/><path d='M5 19c0-3.6 3.1-5.5 7-5.5s7 1.9 7 5.5' fill='white'/></svg>"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Security"
  - "AccessContext"
  - "Threads"
  - "Activities"
---

# Owner Injection

> 🚨 **The rule, in one line:** every operation that runs on a node's hub (a thread, an
> activity, any per-node hub) runs under that node's **OWNER** as the access context — resolved
> from the node, injected **everywhere**, and **carried forward** across deferred / Rx-hop
> continuations. Genuine infrastructure (documentation sync, cache hydration, heartbeats) runs
> as **System**. An **empty** access context is never faked into something — it is **rejected
> instantly**.

This is the companion rule to [AccessContextPropagation](/Doc/Architecture/AccessContextPropagation)
(how a *user's* identity rides a call) and to the never-null invariant in
[CqrsAndContentAccess](/Doc/Architecture/CqrsAndContentAccess). Owner injection answers the
question those leave open: *whose identity does a node's own hub act under when there is no live
caller* — a watcher tick, a deferred sync write, a cold-start activation, a streaming
continuation.

## The three rules

1. **Owner is the standing identity.** A per-node hub (thread / activity / any owned node)
   resolves its owner from the node (`MeshThread.CreatedBy` → `MeshNode.CreatedBy`) and stamps it
   as the hub's access context. Every context-less operation on that hub — the submission
   watcher's claim write, the round dispatch, the data-source sync propagation — runs as the
   owner. The owner is who the work is *for*; the access check that admitted the work already
   happened upstream.

2. **Carry it forward — `SetStandingIdentity(hub, owner)`, not just `Context`.**
   `AccessService.Context` is an `AsyncLocal` that is **wiped across every Rx hop** (a
   `Subscribe` callback, a `Throttle` tick, a remote-stream initial-snapshot continuation, a
   deferred sync write). The hub's **standing identity** survives those hops. Owner injection
   therefore stamps **`SetStandingIdentity(hub, owner)`** (the carry-forward slot), not only
   `SetContext(owner)`. A write that only set `Context` is lost the moment it crosses a
   scheduler boundary.

   > 🚨 **Keyed by hub — never process-wide.** This slot used to be `SetCircuitContext(owner)`,
   > which wrote a single shared `persistentCircuitContext` field on the mesh-wide
   > `AccessService` singleton. That made the owner of whichever thread hub activated last the
   > ambient fallback identity for *every* other hub, every other user, and every anonymous
   > render in the process — a cross-user identity bleed reaching RLS, write attribution and the
   > permission fold. `GetStandingIdentity(hub)` can only ever yield **that hub's** owner.
   > `CircuitContext` no longer has any process-wide fallback on a server: off a circuit's own
   > call tree it is `null` and identity resolution fails closed.

3. **Empty → reject instantly. Never fake an identity.** If no owner can be resolved and there is
   no live caller, the operation is **rejected closed** — the never-null `PostPipeline` guard
   fails the delivery; the update delegate does not run. We do **not** silently stamp the hub's
   own address or fall back to System for a *user* hub (that "hub-self fallback" masked a prod
   data-attribution bug and was deliberately deleted — see `feedback_access_context_always_set`).
   The only sanctioned non-owner identity is **explicit System** for genuine infrastructure.

## What runs as System (the carve-out)

Some streams are **not** owned by a user and legitimately run under the well-known **System**
identity, wrapped explicitly with `AccessService.ImpersonateAsSystem()` /
`PostOptions.ImpersonateAsHub(...)`:

- **Documentation sync** — the embedded `Doc/` content streams are platform-owned, not a user's.
- **Cache hydration** — `IMeshNodeStreamCache` opens its shared upstream under `ImpersonateAsSystem`;
  per-user enforcement happens at the *subscriber* boundary, not the shared pump.
- **SyncStream heartbeats / resubscribes** — infrastructure refresh, no user on the stack.

The litmus test: *can you name a user this work is for?* Yes → inject that owner. No (it is
platform plumbing) → `ImpersonateAsSystem`, explicitly. Never leave it empty and never invent a
hub-self identity.

## The motivating bug — cold-start submit deadlock (FIXED; kept as the worked example)

`OrleansChatHistoryTest.ColdStart_AgentSeesAllPreviousMessages` (2-core) is the canonical failure
this rule fixes. A thread is seeded in persistence; on a **cold start** (grains inactive) a user
submits a message:

1. `ThreadInput.AppendUserInput` runs with `Context=null`, `CircuitContext=TestUser`, and writes
   the pending message via `GetMeshNodeStream(threadPath).Update(...)`.
2. That write reaches the freshly-activated owner's **data-source sync stream**
   (`ds/TestUser/_Thread/history-cold-start`, whose `Host` IS the thread hub), which posts an
   internal `UpdateStreamRequest`. On the deferred continuation the live `AsyncLocal` is gone — so
   the post must fall back to the hub's standing owner identity.
3. **The race.** `SetThreadHubIdentity` resolves the owner from the node **asynchronously**
   (`hub.GetMeshNode(...).Subscribe(...)` — a `GetDataRequest` round-trip). On a cold start the
   **first** submit write reaches the sync stream *before* that response lands, so the owner is not
   yet on the hub's `CircuitContext` → the post carries a **null AccessContext**.
4. The never-null guard **fails it closed** → the patch never commits → the thread node never gets
   `PendingUserMessages` → the submission watcher observes `pending=0` forever → no round is
   dispatched → `Messages.Count` is stuck below the expected count → 30 s timeout.

Proven with a probe on the data-source sync stream — two writes on the SAME stream:

```
[SYNCUPD] owner=ds/…/history-cold-start host=…/history-cold-start hub=sync/2od…  hubCtx=(null)   creation=(null)   hostReal=(null)   final=(NULL→FAIL)   ← first write loses the race
[SYNCUPD] owner=ds/…/history-cold-start host=…/history-cold-start hub=sync/2od…  hubCtx=TestUser creation=TestUser hostReal=TestUser final=TestUser        ← 200 ms later, owner now established
```

The fix is **not** a System fallback at the sync-write layer (that would make a *user* write run
as System and violates rule 3 + the `StreamUpdate_WithoutAsyncLocalIdentity_FailsClosed`
contract). Nor is it "capture from a different hub" — the `Host` is already correct. The fix is to
**establish the owner before the first write can be processed**: resolve it from the node
**synchronously** (the node is already in the data-source stream's `Current` when the submit lands —
its `CreatedBy` is right there), rather than via the async `SetThreadHubIdentity` round-trip that
loses the cold-start race.

## Where it is wired (implementation map)

| Layer | What injects the owner |
|---|---|
| Thread hub | `ThreadExecution.SetThreadHubIdentity` — reads the thread node's `CreatedBy` and stamps it as **both** `Context` and this hub's standing identity (`SetStandingIdentity(hub, owner)`, the carry-forward slot) on hub activation. |
| Activity hub | The activity control-plane establishes the activity owner the same way (resolve from the activity node, inject as the hub's standing identity). |
| Per-node data source / sync stream | `SynchronizationStream.Update` resolves the node OWNER **synchronously from the node already in its own `Current`** when neither a live AsyncLocal context nor a captured creation context survives — via `IStreamOwnerResolver`, resolved off `Host.ServiceProvider`. Genuine infra streams (doc sync) carry System. |
| One-shot helpers | `AccessContextScope.FromNode(node, accessService)` — runs a block under the node's owner (`CreatedBy`/`LastModifiedBy`), falling back to System only for an unattributed node. |

> ✅ **Status: shipped.** The synchronous owner resolver is
> `IStreamOwnerResolver` (`src/MeshWeaver.Data/Serialization/IStreamOwnerResolver.cs`), implemented
> by `MeshNodeStreamOwnerResolver` in `MeshWeaver.Graph` (the layer that knows `MeshNode` —
> `MeshWeaver.Data` sits below `Mesh.Contract` and cannot read it), registered in
> `GraphConfigurationExtensions` and consumed by `SynchronizationStream.Update`. Because the node is
> ALREADY in the stream's `Current` at write time, its `CreatedBy` is available with **no async
> round-trip and no race** — which closes the cold-start FIRST-write race described above. The
> result is still filtered through the real-user invariant by the caller, so a hub/system principal
> can never leak into `CreatedBy`.

## See also

- [AccessContextPropagation](/Doc/Architecture/AccessContextPropagation) — how a user's identity
  rides a call across `.Subscribe()` boundaries.
- [CqrsAndContentAccess](/Doc/Architecture/CqrsAndContentAccess) — `GetStream` is access-checked;
  the never-null invariant.
- [SyncedQueryDataSource](/Doc/DataMesh/SyncedQueryDataSource) — `hub.GetQuery()`, the
  access-checked synced collection cold-start data should read through.
