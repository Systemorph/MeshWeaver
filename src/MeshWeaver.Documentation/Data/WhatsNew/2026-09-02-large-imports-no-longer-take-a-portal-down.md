---
Name: A very large import can no longer take a portal down
Category: Fix
Description: Fixed an out-of-memory failure that could kill deliveries — and destabilise a whole portal instance — when a bulk import built one message too large for the mesh to carry.
Icon: Sparkle
Order: -20260902
---

# A very large import can no longer take a portal down

Bulk operations that move a lot of content at once — imports above all — could build a single
internal message so large that the portal ran out of memory while preparing it to send. When that
happened the message was simply lost: whatever operation it carried stalled, nothing said why, and
the failed allocation put every other piece of work on that instance at risk.

The mesh already refused messages that were provably too large to deliver, but it did the check one
step too late — after the point where this particular failure happened. The check now runs before
the message is handed on, so an oversized delivery is turned away immediately and cleanly instead of
exhausting memory.

When that happens you now get a real answer instead of silence. The operation fails straight away,
and the log names what was refused, how big it was, the limit it was measured against and where it
was going — enough to identify the source without guesswork. Nothing that works today is newly
refused: the limit being applied is the one the transport already enforced.

The accompanying failure report no longer carries a copy of the oversized content either. It used to,
which meant the report about a message too big to send was itself too big to send — so it vanished
too, and the operation waited on an answer that could never arrive.

Very large imports are still better split into batches; this change makes the failure loud,
attributable and survivable rather than silent.
