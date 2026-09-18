---
Name: A dropped request no longer waits a minute to say so
Category: Fix
Description: A request the system decided to drop used to leave its caller waiting the full minute and then produce a timeout that named the wrong causes. The drop is now reported at once, and the timeout carries the request's own trail.
Icon: Timer
Order: -20260916
---

# A dropped request no longer waits a minute to say so

Sometimes the platform deliberately refuses a message: a component is shutting down, a burst of
identical traffic is being broken up, or a component's start-up has stalled and its backlog has hit
the cap that keeps memory bounded. Those refusals are correct. What was not correct is that **the
sender was never told.**

The refusal was computed, recorded internally — and then thrown away on the way back to the caller,
which received an envelope saying "accepted". The caller then waited its entire one-minute budget
for an answer that had been discarded before it left the building, and the timeout it finally got
could not say why.

Three things changed.

## The refusal reaches the caller

A post that was dropped now says so in the result it hands back. That also switches on a safety
check that was already written and could never fire: the code that posts the verdict of a node
create verifies that a route actually took it, and falls back to a second route when it did not.
It was looking for exactly this signal, and the signal never arrived.

## A request dropped by the backlog cap is answered

When a component's start-up gate is stuck, everything behind it is parked, and past a fixed depth
the overflow is dropped so memory stays bounded. That is right for the background traffic it was
written for, which re-synchronises by itself. It is wrong for someone waiting on a reply — nothing
re-sends a "create this node" request on its own. Those are now answered immediately, with a status
that says **no verdict was reached, this is worth retrying** — never "denied" and never "not
found", both of which would send someone to fix something that was never broken.

## The timeout says what the waiting component did, and shows the trail

Two corrections to the message itself:

- It used to say *"this hub was idle while waiting, so it processed everything delivered to it"*.
  That was read off a single snapshot at the moment it gave up, and asserted about the whole
  minute — so a component that was saturated for 59 seconds and cleared in the 60th printed it.
  The message now reports **how many messages it actually handled while waiting**, which is a
  measurement of the interval rather than a guess from its last instant.
- When the sender and the destination are **the same component** — which is how every node
  create, delete and move in the system is issued — it now says so, and stops offering causes
  that cannot apply. There is no delivery route to lose the message and no return route to lose
  the answer, so the honest remaining possibilities are much narrower, and the message names those
  instead.

It also now prints **the request's own trail**: every stage that request passed through, ending in
a one-line verdict. That was already being recorded; the message simply never looked at it while
telling the reader it could not tell them anything.

## What did not change

No timeout was lengthened, nothing retries, nothing is swallowed. Messages that are dropped are
still dropped, and traffic that nobody is waiting on is still dropped silently — answering it is
what feeds the retry storms these safeguards exist to stop.
