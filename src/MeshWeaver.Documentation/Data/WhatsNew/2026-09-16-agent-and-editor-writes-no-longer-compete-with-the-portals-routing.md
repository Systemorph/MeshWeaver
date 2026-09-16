---
Name: Agent and editor writes no longer compete with the portal's routing
Category: Fix
Description: An agent reading a node's content collections, running a script, resolving a reference — or anyone editing a node through the mesh node editor — used to send that work from the mesh's router itself, so it queued against everyone else's page loads. Those exchanges now leave from the mesh's off-router hubs, and a second guard makes the next one impossible to miss.
Icon: ArrowSync
Order: -20260916
---

# Agent and editor writes no longer compete with the portal's routing

The mesh's root hub is its **router**: every message between two hubs passes through it, one at a
time. Work executed there does not merely run slowly — it takes turns away from routing, and a burst
of it starves the ordinary traffic that makes pages appear. That is what wedged a production portal
on 2026-06-11, and it is the defect
[#1140](https://github.com/Systemorph/MeshWeaver/issues/1140) has been reporting ever since.

The previous fix moved the **Recycle** teardown off the router. This one moves what was sitting
right beside it: the agent and MCP surface's node reads, its content-collection lookups, its
reference resolution and its script dispatch, plus the write the mesh node editor makes when you
change a node. Each of those went out stamped as the router and took its reply back there too.
Nothing about them changes for anyone holding a session, portal or per-node hub — which is nearly
every caller — because the hop is the identity function for any hub that is not the router.

Two dead code paths that carried the same shape were **deleted** rather than corrected: nothing in
the product called them, and a router-issuing exchange sitting in unused code is a trap for whoever
wires it up next, not a thing to tidy.

## Why it was missed, and what stops the next one

A guard already existed for exactly this class, and it could not see these sites. It keys on the
**message**: a post counts when the framework registers a lifecycle handler for what it carries.
That is the right rule for what it covers — but the fix belongs to the **hub**, not to the message.
So the guard saw the one teardown on the agent surface and none of the five other exchanges leaving
from the same field, three lines apart.

There is now a second guard that asks the other question: once a file has declared a hub reference
router-capable — by moving a single message off it — **every** other addressed message on that same
reference has to move too, whatever it carries. Nothing has to be listed or remembered: writing the
first hop *is* the declaration, and it cannot be written without meaning it.

What this still does not cover, and is written down rather than implied: a component that has never
moved anything off the router declares nothing, so it stays invisible to both guards until its first
sighting. One such strand — a stream unsubscribe whose origin hub is a correlation question rather
than a routing one — remains open on purpose, tracked as
[#4489](https://github.com/Systemorph/MeshWeaver/issues/4489). See
[Router Traffic Detection](/Doc/Architecture/RouterTrafficDetection).
