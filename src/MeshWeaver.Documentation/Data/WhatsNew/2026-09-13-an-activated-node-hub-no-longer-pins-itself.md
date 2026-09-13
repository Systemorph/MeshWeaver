---
Name: An activated node hub no longer pins itself — idle nodes can retire on the Orleans host
Category: Fix
Description: On a multi-replica portal every node ever activated stayed resident for the life of the process, with its cache entry and the sync hubs on both sides — the unexplained half of the heap growth measured in #3432. The hub was handed the process cache's live view of its own path and kept it subscribed; the entry then heart-beat the hub's own grain alive. The hub now receives the node it was activated with, as it always did on the single-process host.
Icon: Recycle
Order: -20260913
---

A portal running on Orleans activates a node's hub the first time anything addresses it, and lets
it go again when nothing has addressed it for a while. That second half had stopped happening. Every
activated hub was handed the process-wide mesh-node cache's live view of its own path as the source
of its own node, and kept a subscription on it for its whole life; an entry with a subscriber is
never released, and the entry's own hydration stream sends the hub a heartbeat every 45 seconds that
tells the grain not to deactivate. A loop with nothing outside it — so every node ever touched on a
replica stayed resident, with its cache entry and about six sync hubs, until the pod restarted. On
the live portal that was the population issue #3432 measured growing at ~25 hubs a minute and never
retiring, concentrated on the per-activity records every compile and every import writes.

The hub now receives the node it was activated with — one emission, then done — which is what the
single-process host has always handed it. Nothing was lost: after activation the cache's view of a
node is hydrated by the node's own hub, so everything it could emit was an echo; writes reach the
owner as writes and changes made elsewhere reach every process through the change feed, as before.
A test pins it: after a node is activated and its reader has gone, the silo's cache can release the
node's entry; on the previous grain it never could.

Read [A Hub That Pins Its Own Cache Entry](/Doc/Architecture/AHubThatPinsItsOwnCacheEntry).
