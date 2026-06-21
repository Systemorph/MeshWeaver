---
Name: "Owner Injection — the standing access identity for a node's hub"
Abstract: "In any node/thread/activity context the NODE OWNER (resolved from the node) is the standing access context, injected everywhere and carried forward across Rx hops via CircuitContext. Genuine infrastructure (doc sync, cache hydration) runs as System. An empty access context is NEVER faked — it is rejected instantly."
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

2. **Carry it forward — `CircuitContext`, not just `Context`.** `AccessService.Context` is an
   `AsyncLocal` that is **wiped across every Rx hop** (a `Subscribe` callback, a `Throttle` tick,
   a remote-stream initial-snapshot continuation, a deferred sync write). `CircuitContext` is
   mesh-global per hub and **survives** those hops. Owner injection therefore stamps
   **`SetCircuitContext(owner)`** (the carry-forward slot), not only `SetContext(owner)`. A write
   that only set `Context` is lost the moment it crosses a scheduler boundary.

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

## The motivating bug — cold-start submit deadlock

`OrleansChatHistoryTest.ColdStart_AgentSeesAllPreviousMessages` (2-core) is the canonical failure
this rule fixes. A thread is seeded in persistence; on a **cold start** (grains inactive) a user
submits a message:

1. `ThreadInput.AppendUserInput` runs with `Context=null`, `CircuitContext=TestUser`, and writes
   the pending message via `GetMeshNodeStream(threadPath).Update(...)`.
2. That write reaches a freshly-activated owner whose **data-source sync stream** posts an
   internal `UpdateStreamRequest`. On the deferred continuation the live `AsyncLocal` is gone, and
   the stream's creation context was captured cold (from the empty sync sub-hub) — so the post
   carries a **null AccessContext**.
3. The never-null guard **fails it closed** → the patch never commits → the thread node never gets
   `PendingUserMessages` → the submission watcher observes `pending=0` forever → no round is
   dispatched → `Messages.Count` is stuck below the expected count → 30 s timeout.

Proven with a probe: with the owner injected the chain runs end-to-end
(`pending=1 → CLAIMED → DISPATCH_ROUND → msgs=6`); without it, `pending=0` throughout.

The fix is **not** a System fallback at the sync-write layer (that would make a *user* write run
as System and violates rule 3 + the `StreamUpdate_WithoutAsyncLocalIdentity_FailsClosed`
contract). The fix is to **inject the thread owner** so the context is non-empty and correct.

## Where it is wired (implementation map)

| Layer | What injects the owner |
|---|---|
| Thread hub | `ThreadExecution.SetThreadHubIdentity` — reads the thread node's `CreatedBy` and stamps it as **both** `Context` and `CircuitContext` (carry-forward) on hub activation. |
| Activity hub | The activity control-plane establishes the activity owner the same way (resolve from the activity node, inject as CircuitContext). |
| Per-node data source / sync stream | The node's data-source `SynchronizationStream` must carry the **node owner** for its deferred `UpdateStreamRequest` writes — its `_creationContext` is the owner (resolved from the node), not a value captured from the empty sync sub-hub. Genuine infra streams (doc sync) carry System. |
| One-shot helpers | `AccessContextScope.FromNode(node, accessService)` — runs a block under the node's owner (`CreatedBy`/`LastModifiedBy`), falling back to System only for an unattributed node. |

> 🚧 **Status:** the thread-hub and `FromNode` injection are in place; extending owner injection
> down to the per-node **data-source sync stream** (so the cold-start owner-side write carries the
> owner without relying on the thread hub's CircuitContext, which a separate sync sub-hub does not
> inherit) is the remaining cross-cutting piece. It composes with the deferred-write context
> capture added in the `SynchronizationStream` access work.

## See also

- [AccessContextPropagation](/Doc/Architecture/AccessContextPropagation) — how a user's identity
  rides a call across `.Subscribe()` boundaries.
- [CqrsAndContentAccess](/Doc/Architecture/CqrsAndContentAccess) — `GetStream` is access-checked;
  the never-null invariant.
- [SyncedQueryDataSource](/Doc/DataMesh/SyncedQueryDataSource) — `hub.GetQuery()`, the
  access-checked synced collection cold-start data should read through.
