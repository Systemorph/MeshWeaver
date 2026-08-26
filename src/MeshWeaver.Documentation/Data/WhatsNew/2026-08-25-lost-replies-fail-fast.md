---
Name: A reply that cannot be delivered now says so instead of hanging
Category: Fix
Description: When a page or agent asked the mesh for something the server could not deliver, the failure notice could itself be dropped — so the request sat there until it timed out a minute later with no error anywhere. The notice now takes a delivery route that cannot silently vanish, and you get the error immediately.
Icon: ArrowReply
Order: -20260825
---

# A reply that cannot be delivered now says so instead of hanging

When the portal runs on more than one server, a request may be answered by a different server from
the one your session is on. If that server could not complete the request, it sent back a failure
notice — and that notice travelled on a channel with no delivery guarantee. Publishing to a channel
nobody is listening on *succeeds*, so the notice was simply discarded. Nothing failed, nothing was
logged, and your request waited out its full minute-long budget for an answer the server believed it
had already sent.

That was the worst possible shape for it: a lost error is indistinguishable from a slow one, so a
page that should have shown "not found" in a fraction of a second instead looked frozen, and there
was nothing in the logs to explain it.

Failure notices now travel the same directed route that ordinary messages take, which either lands
or reports back. So an error reaches you as an error, at once. In the one case where that route is
not available — during a rolling upgrade, while some servers are still on the previous release —
the old channel is still used, but it now checks for a listener first and says loudly in the logs
when there is none, instead of pretending the message went through.
