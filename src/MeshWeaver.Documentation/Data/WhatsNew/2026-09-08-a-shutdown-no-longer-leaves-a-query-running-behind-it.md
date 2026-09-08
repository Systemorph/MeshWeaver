---
Name: A shutdown no longer leaves a query running behind it
Category: Fix
Description: When a portal or a test host shut down, a synced query the node cache had just opened could still start on a background thread after everything it needed was gone. It now stops with the cache, and a query opened after shutdown answers with an error instead of waiting forever.
Icon: Stop
Order: -20260908
---

# A shutdown no longer leaves a query running behind it

The node cache serves every synced query in a process from one shared subscription per query. It
opens that subscription lazily, on the first reader, and hands the work to a background thread so
the reader is never blocked. That hand-off is where a small gap sat: the cache's own shutdown
detached every per-node stream, every pending write and every update queue it owned — but not the
queries. Their subscriptions belonged to the mechanism that had opened them, and nothing on the
shutdown path could reach them.

Most of the time that did not matter. But a query opened an instant before shutdown was still
queued for its background thread when the shutdown completed, and when the thread finally got to
it, it tried to build the query against a service container that had already been closed. On the
build servers this appeared as a burst of "already disposed" errors after a test had reported a
clean teardown — eleven of them in one run, across three unrelated test suites, all from this one
place.

The cache now keeps hold of each query's subscription and releases them all as part of its own
shutdown: a query that was already running is unsubscribed, and one that was still queued is
cancelled before it starts. A query opened after the cache has shut down answers immediately with
an error naming the cache, rather than waiting on a subscription that will never be served.
