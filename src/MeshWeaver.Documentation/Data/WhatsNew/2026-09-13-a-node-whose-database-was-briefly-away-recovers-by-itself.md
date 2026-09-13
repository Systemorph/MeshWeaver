---
Name: A node whose database was briefly away recovers by itself
Category: Fix
Description: A node hub whose start-up could not reach the database — a connection timeout, a name that did not resolve — no longer stays broken until the portal restarts; the next request brings it back and it initialises again.
Icon: ArrowSync
Order: -20260913
---

# A node whose database was briefly away recovers by itself

When a node's hub started up during a short database or DNS outage, its initialisation failed and
the hub was marked failed for good: every later request to that node was refused with
"initialization failed", the node rendered as unavailable, and only a recycle or a portal restart
brought it back — long after the database had returned.

A start-up that fails on a transient infrastructure fault now retires that activation instead of
marking it failed. Anyone waiting on it is told the address is shutting down and may be retried,
the hub disposes itself, and the next request creates it afresh, so its initialisation runs again
against the database that has meanwhile come back. Faults that really belong to the node — a type
that does not compile, a handler that throws — are still reported as before, and a hub that
cannot be re-created on demand (the portal's root hub, a stream's own sub-hub) keeps the previous
behaviour with a log line that now names the transient cause.
