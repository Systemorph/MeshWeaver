---
Name: Routing back-pressure alerts now name what they actually saw
Category: Fix
Description: A busy silo raised a critical alert that blamed a stuck delivery — almost never the real cause. The alert now reports whether anything is genuinely blocked and how long the episode lasted, and a delivery cancelled at shutdown no longer leaves its slot behind.
Icon: Pulse
Order: -20260812
---

# Routing back-pressure alerts now name what they actually saw

When a silo had a lot of deliveries in flight at once, it raised a critical
alert saying that deliveries "were not completing" and pointed at whichever
address happened to be routed at that instant. Both halves were misleading. The
count it reports rises whenever the machine is short of CPU — every delivery can
be moving along perfectly and the number still climbs — and the address named is
simply the last one dispatched, not a slow one.

That wording sent several investigations after the wrong thing, so the alert now
reports only what it can actually see: how many deliveries are in flight, how
many destinations they are spread across, and whether any of them are queued
behind another delivery. The last of those is the one that matters — deliveries
waiting on a delivery is a real blockage, while the same number spread across
many destinations is just a busy moment. When the backlog clears, the silo now
also says how long it lasted, which separates a two-second burst from something
genuinely stuck without anyone having to inspect a running server.

Separately, a delivery that was cancelled while the silo was shutting down used
to end without ever reporting that it had finished, so the slot it occupied was
never given back and the queue for its destination was left standing. Those
deliveries now close out properly.
