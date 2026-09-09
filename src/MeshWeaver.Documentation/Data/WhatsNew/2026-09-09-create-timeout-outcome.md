---
Name: Create timeouts report an unknown outcome
Category: Fix
Description: A create request that times out no longer claims the write was not applied: the node may already be stored while its remaining creation steps or response are pending.
Icon: Info
Order: -20260909
---

A timed-out create request could tell callers that nothing was written even when the node was
already stored. The error now reports an unknown outcome and advises checking the node before
retrying. A missing response does not establish that the operation was rolled back.
