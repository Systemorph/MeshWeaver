---
Name: Durable streams are mesh nodes
Category: Feature
Description: The design that retires the Orleans memory stream is written down — data sync already recovers from the node's own version chain, requests get a fast transient answer instead of a queued one, cross-silo cache invalidation moves onto the database's own change feed, and at-least-once work keeps the _Inbox node pattern. No durable stream provider is added.
Icon: Database
Order: -20260830
---

# Durable streams are mesh nodes

Cross-silo delivery on the platform still had one in-memory Orleans stream underneath it, and every
so often a request would wait out a full minute because that stream's queue or registration had
stalled inside the cluster. The pending question was whether to buy a durable stream provider to
harden it.

The answer, now recorded as an architecture page, is that no provider is needed: each kind of
traffic the stream carries already has a durable home in the mesh itself. Synchronized data recovers
from the node's version chain; a request to a hub that is not live should be told so in
milliseconds rather than queued; cross-silo cache invalidation belongs on the database's own change
feed, which every pod already listens to; and work that must survive a restart already uses the
`_Inbox` node pattern. The page names the order in which the remaining slices land and the log line
that decides when the stream can go.

Alongside it, four code comments that still claimed the database change listener "is never
started" have been corrected — it has been running on every pod since the fix for the August
outage, and a test guards it.
