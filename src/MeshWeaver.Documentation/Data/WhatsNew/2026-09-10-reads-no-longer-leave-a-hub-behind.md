---
Name: Reading data no longer leaves a hub behind
Category: Fix
Description: Repeated reads of the same data now share one stream instead of leaving a permanent hub behind on every request.
Icon: Sparkle
Order: -20260910
---

# Reading data no longer leaves a hub behind

Every read of a node's data used to build its own private copy of the underlying stream, and that copy stayed alive for as long as the node was loaded. On a busy portal the copies accumulated: a replica gained roughly a thousand of them an hour, each holding a few hundred kilobytes, which is memory a running portal never gave back.

Reads of the same data now share one stream. The read itself is unchanged — it answers with the same data, and later changes still reach whoever is listening — but the portal keeps one stream per view instead of one per request.
