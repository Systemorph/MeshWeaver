---
Name: A finished activity lets go of its hub
Category: Fix
Description: A compile, import or export that has ended no longer holds its own activity node open. The keep-alive it used to leave behind is what made a busy morning of recompiles grow the portal without bound.
Icon: ArrowSyncCheckmark
Order: -20260813
---

# A finished activity lets go of its hub

Every compile, import, export and mirror writes its progress into an activity node, and the write
opens a shared connection to that node so the next line can be appended cheaply. When the activity
ended, that connection stayed open.

It looked harmless — the platform already releases connections that have gone quiet, and node hubs
already shut down when they go idle. But an open connection sends a heartbeat to the node it is
connected to, every 45 seconds, precisely so that node stays up. So a finished activity was not
waiting to be tidied away: it was **holding itself open**, and holding its node's hub open with it.
On a morning of merges — each one re-importing content and recompiling the types it touched — the
portal accumulated one of those per compile and never gave any of them back.

Now the write that records the final status also lets go of the connection. Nothing else changes:
if you have the finished activity open in front of you, your view keeps its live connection and
nothing is torn down under you; and if the release does not happen for any reason, the existing
quiet-path cleanup still gets there. Measured on the recompile repro, hubs retained per compile
activity drop from 6.5 to 5.0, and the connection-side share of that goes to zero.
