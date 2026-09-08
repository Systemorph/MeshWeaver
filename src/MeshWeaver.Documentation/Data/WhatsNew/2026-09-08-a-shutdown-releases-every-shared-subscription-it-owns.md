---
Name: A shutdown releases every shared subscription it owns
Category: Fix
Description: Several services shared one background subscription among many readers and had no way to stop it when they shut down, so it could keep running — or start late — against a container that was already gone. Each now hands its subscription to its owner, which releases it on shutdown; a reader arriving after that gets an error naming the owner instead of waiting forever.
Icon: Stop
Order: -20260908
---

# A shutdown releases every shared subscription it owns

A number of services share one background subscription among every caller that asks for the same
thing — the compile pipeline's activity record, a content collection's initial load, a repository's
canonical name, the user directory index, the in-flight activity and routing counters, and the
node cache's synced queries. Sharing is right: the work runs once and every reader sees the same
result. What was missing was an owner. The mechanism that opened the shared subscription kept its
handle to itself, so when the service shut down nothing could close it, and when the subscription
was still queued for a background thread at that moment, it started *after* the shutdown and tried
to build itself against services that no longer existed. On the build servers that surfaced as a
burst of "already disposed" errors after a test had reported a clean teardown.

Every such subscription now belongs to the service that opened it and is released as part of that
service's own shutdown: one that is running is unsubscribed, one that is still queued is cancelled
before it starts, and one that has already finished its work no longer counts as open. A caller who
asks after the shutdown is answered immediately with an error naming the service, rather than being
handed a stale result and then left waiting for updates that will never come. A guard in the test
suite keeps every future shared subscription on the same rule.
