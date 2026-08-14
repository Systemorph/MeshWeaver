---
Name: No more failure storms when a node or pod goes away
Category: Fix
Description: Routing no longer answers heartbeats and error replies with more error replies, which used to flood the portal during shutdown and node deletion.
Icon: Sparkle
Order: -20260814
---

# No more failure storms when a node or pod goes away

When the mesh could not deliver a message, it replied to the sender with a delivery failure. That is
right for a real request someone is waiting on, and wrong for two kinds of traffic: background
keep-alive pings, which nobody is waiting for, and failure replies themselves, which produce another
failure reply and go round in circles.

Routing had always been written to skip both — but the check was looking at the wrong thing and
never actually matched, on every routing path in both the single-process and clustered hosts. So a
node that had been deleted, or a pod that was shutting down, generated a burst of pointless failure
traffic at exactly the moment the portal had least capacity to carry it: pages that hung, background
work that stalled, and in the worst case a restart.

The check now travels with the message instead of being re-derived after the fact, so it works
everywhere, and both hosts genuinely behave the same way. Real requests still get their answer
immediately, so genuine errors surface just as fast as before.
