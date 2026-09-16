---
Name: Recycling a node no longer competes with the portal's routing
Category: Fix
Description: The Recycle tool and the Compile button used to tear a node's hub down from the mesh's router itself, so a burst of recycles queued behind — and ahead of — everyone else's page loads. Both recycle surfaces now issue the teardown off the router, and the guard that is supposed to catch this class was widened so it can no longer miss a message type.
Icon: ArrowSync
Order: -20260916
---

# Recycling a node no longer competes with the portal's routing

The mesh's root hub is its **router**: every message between two hubs passes through it, one at a
time. Work executed there does not merely run slowly — it takes turns away from routing, and a burst
of it starves the ordinary `SubscribeRequest` traffic that makes pages appear. That is what wedged a
production portal on 2026-06-11.

`Recycle` — the MCP tool, and the **Compile** button that shares its code path — posted its teardown
straight from the router. So did `RecycleNode`, the framework's one recycle surface. Neither was
slow on its own; both put node-hub lifecycle on the one action block that must never queue. Both now
hand the teardown to the mesh's dedicated off-router hub first. For every caller that already held a
session, portal or per-node hub — which is nearly all of them — nothing changes at all: the hop is
the identity function for any hub that is not the router.

## The half that mattered more

A ratchet already existed to stop exactly this, and it did not see it. It matched a **literal list of
six node-CRUD request names**, and a teardown is not one of them — so the most router-hostile message
in the mesh was invisible to it, and the only thing that noticed was a runtime detector firing in
production ([#4463](https://github.com/Systemorph/MeshWeaver/issues/4463)). Adding "teardown" to the
list would have bought exactly one more message.

The guard now **derives** what counts as lifecycle from the framework's own handler registrations —
the ones a hub makes for itself at construction, and the ones the node-operation hub makes — and
filters them through the same predicate the runtime detector evaluates. You cannot add a lifecycle
message without registering a handler for it, so a new one joins the guard automatically; a name in
a test can be forgotten, a handler cannot. On its first run the derivation picked up a verb the
hand-written list had already missed.

What this does not change: the older, broader
[#1140](https://github.com/Systemorph/MeshWeaver/issues/1140) family stays open. Its remaining strand
is a stream unsubscribe, which has no equivalent seam and whose origin hub is a correlation question
rather than a routing one — see
[Router Traffic Detection](/Doc/Architecture/RouterTrafficDetection).
