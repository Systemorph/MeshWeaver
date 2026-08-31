---
Name: A write is answered even when its owner goes down with the mesh
Category: Fix
Description: When a node's owner was shut down as part of a wider teardown, the "this write never applied" answer it produced was thrown away, and whoever was waiting heard nothing for a full 31 seconds. The answer now goes straight to the waiter.
Icon: Sparkle
Order: -20260831
---

# A write is answered even when its owner goes down with the mesh

When the component that owns a node shuts down with a write still in flight, it produces an explicit
answer — *the write never applied; it is safe to try again*. Delivering that answer is the whole
point of producing it: whoever is waiting can retry immediately instead of waiting to find out.

The answer was sent as a message through the owner's parent, and only while that parent had not yet
begun shutting its own children down. That excluded exactly the case it was needed for. A whole
batch of owners going down at once — which is what a shutdown *is* — happens after the parent has
passed that point, so every answer in that batch was discarded. The waiter then heard nothing, and
nothing is indistinguishable from a slow owner, so it waited out the full 31-second budget for an
answer that had already been written and thrown away.

The justification recorded in the code was that during a wider teardown *nobody is waiting*. That
was never something the code could check, and it is false precisely when it matters: somebody whose
wait began before the shutdown started is still waiting, and they were the ones guaranteed to be
ignored.

The answer is now handed directly to whoever is waiting, rather than posted and routed in the hope
of arriving. Waiters are already tracked by name, so the owner can simply look — which also replaces
the old guess with a fact: if nobody is registered, nobody is waiting.

Handing it over directly is also what makes this affordable. The obvious repair — send the message
through whichever ancestor can still forward it — was implemented and measured at roughly ten times
the cost on every shutdown, because it wakes waiters throughout the drain. A lookup that finds
nothing costs nothing, so the ordinary case pays nothing at all.
