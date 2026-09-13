---
Name: A notification burst is one read per replica, not one per notification
Category: Fix
Description: A burst of database change notifications for one node now costs each portal replica a single coalesced read, restoring the read-storm guard that the cross-replica cache invalidation had bypassed.
Icon: ArrowSync
Order: -20260913
---

# A notification burst is one read per replica, not one per notification

The cross-replica cache invalidation shipped on 2026-09-12 reads the committed row once when an
older database notification arrives without its node type and version. That read ran once per
notification, ahead of the coalescing every other consumer of the change feed goes through — so a
bulk import or a rapid sequence of edits to one node made every replica read that node once per
notification. The plugins test suite's read-storm guard measured it exactly: 200 notifications on
one path cost 201 reads where the contract is a handful, and every plugins `main` run since that
set stopped on it, which also skipped module tagging and the platform bake behind the red.

The relay's compatibility read now goes through the same per-path coalescer the per-node hub's own
reconcile has always used: at most one read per 50 ms of quiet on a path, the last notification of
a burst is the one that reads, and reads on a path are serialised so a burst that lands while a
read is in flight queues behind it. A notification that already carries the node, or its node type
and version, or announces a delete, is relayed at once as before. The PostgreSQL feed's own
notification shape — identifiers only, never the node — is now recognised as its designed shape
rather than logged as an error on every notification.
