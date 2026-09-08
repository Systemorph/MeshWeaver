---
Name: A message held during startup is no longer overtaken by a later one
Category: Fix
Description: While a node finishes starting up it parks the messages sent to it and runs them once it is ready. If another message happened to be mid-flight at the exact moment it became ready, that later message was handled first — so a cell could run before the cell that defined what it uses. Held messages are now always handled in the order they were sent.
Icon: Bug
Order: -20260908
---

# A message held during startup is no longer overtaken by a later one

Everything in the mesh is addressed, and a message sent to an address that is still waking up is
**held** rather than dropped. The moment the target is ready, the held messages run — and they run
in the order they were sent. That ordering is not a detail: it is why submitting two code cells
back to back works, why an edit followed by a read sees the edit, and why any handler may rely on
what the previous message did.

There was a gap of a few milliseconds in which it did not hold.

## What went wrong

Becoming ready is two steps — *stop holding* and *put the held messages back at the front of the
queue* — and a message that was already being processed sat between them. It had been taken off
the queue before the target became ready, and by the time it asked "am I being held?" the answer
was no. So it ran immediately, ahead of messages that had been waiting longer, and the queue was
put back in order behind it.

The effect a person would notice is a later message winning: a cell that uses a value running
before the cell that defines it, a view rendering against state an earlier write had not applied
yet. It needed the target to be busy at the exact instant it became ready, which is why it showed
up under load and never on a quiet machine — measured at about **one occurrence in a hundred** on
our own build runners, with a different-looking symptom each time.

An earlier fix had closed the half of this that was visible in the queues. This is the other half:
the message that was in no queue at all when the switch flipped.

## What changes

The two steps are now one: the held messages are put back **before** the target stops holding, so
there is no instant in which a message can be told "nothing is waiting" while something is. And a
message that is already running when the switch flips now notices that older work has appeared
ahead of it and rejoins the queue in its own place instead of overtaking.

Nothing to configure, and nothing to change in any code that sends messages — the ordering that was
always promised now actually holds, including at that boundary.
