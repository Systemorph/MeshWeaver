---
Name: Shutdown lets work finish, and names what it had to stop
Category: Fix
Description: A portal going down no longer cuts off the work it had already accepted. Running activities, in-flight reads and writes, and handlers mid-turn are given the chance to finish; only something that has stopped making progress for eight seconds is stopped, and when that happens the log says which one, with enough detail to find out why. The forced teardown that used to declare a hub finished while its work was still running is gone.
Icon: ArrowSync
Order: -20260904
---

# Shutdown lets work finish, and names what it had to stop

When a portal restarts — a deploy, a scale-down, a recycle of one node — everything it was doing
has to come to an end. Until now it did that impatiently. The moment a hub began shutting down it
cancelled whatever it was running, cancelled every read or write still in flight on the I/O pools,
and if the shutdown made no progress for eight seconds it tore the hub down from the outside and
declared it finished — with the work that had wedged it still running underneath.

Measured on the production portals, that last step fired dozens of times per shutdown, and
sometimes on a portal that was not shutting down at all. Each time, a hub reported itself gone
while it was still holding something.

## What changes

**Accepted work is drained, not cut off.** A handler in the middle of a merge finishes the merge.
A queue of messages the hub had already accepted is processed before the shutdown takes its turn.
A write that would have landed in fifty milliseconds lands. A running activity keeps running for
as long as it keeps reporting progress.

**Only what has stopped is stopped.** Something that has made no progress for eight seconds — an
activity that has not logged a line, an I/O operation that has not completed, a handler that has
not returned — is cancelled once, cooperatively. If it ignores that, it is reported again and the
shutdown proceeds around it at the host's own deadline, but the hub that owns it never claims to
have finished.

**Every stop is named.** Each cancellation and each message a hub had to discard is logged as an
error carrying the hub, the message or activity, how long it had been stuck, and a snapshot of
what it was waiting on. On production portals those errors are what the log triage turns into
issues, so a wedge is a ticket with a reproduction attached rather than a line nobody reads.

Nothing about a healthy shutdown changes: a hub with nothing in flight goes down as fast as before.
