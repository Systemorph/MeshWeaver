---
Name: Publication updates return a retry response
Category: Fix
Description: A plugin build reading a publication during an update now receives the retry response instead of an unexpected server error when the publication seal disappears.
Icon: ArrowSyncCheckmark
Order: -20260910
---

# Publication updates return a retry response

A publication update briefly removes its completion marker while replacing the stored bundles.
A build reading at that moment could receive an unexpected server error because the marker
disappeared between the presence check and the file read.

The read now handles that removal directly. The registry returns its existing retry response
while the publication is being replaced, so the consuming build can wait for the completed set.
A publication that has been removed entirely still returns a missing-publication response.
