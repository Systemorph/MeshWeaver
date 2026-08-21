---
Name: Messages keep flowing while a server is shutting down
Category: Fix
Description: A server going offline no longer strands the queue of messages waiting behind it, so pages served by the remaining servers stay responsive throughout a deployment.
Icon: Sparkle
Order: -20260821
---

# Messages keep flowing while a server is shutting down

When a portal server begins shutting down — which happens on every deployment — it stops the background workers that carry messages between parts of the mesh. A message already being carried was torn down at that moment without ever reporting that it had finished, so the bookkeeping that lets the next message for the same destination start never ran. The queue behind that destination stopped moving and never recovered, for as long as the process was still winding down.

The tear-down now reports itself, so the next message starts as it should. Deployments observed queues thirty-three messages deep sitting completely still for over ten minutes; that backlog no longer forms, and the diagnostics that say "the backlog cleared" can be emitted again instead of being impossible to reach.
