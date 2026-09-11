---
Name: A saved change is confirmed even during shutdown
Category: Fix
Description: A change that was saved while the system was shutting down could be reported back as "the owner did not answer" after half a minute. The confirmation was being thrown away on its way back. It now arrives.
Icon: ArrowReply
Order: -20260911
---

When the system shuts down, every part of it is asked to finish what it is doing before it goes.
A change that is being saved at that moment is one of those things: it gets written, and then a
confirmation travels back to whoever made the change.

That confirmation could be lost. The part of the system that passes messages along stopped passing
*anything* once shutdown had started — on the assumption that whoever was waiting was going away
too. They were not: the one waiting for the confirmation was still there, doing exactly what it is
supposed to do during shutdown, which is wait for the answers it is owed. It waited out its full
half-minute and then reported that the change had not been confirmed — for a change that had in
fact been saved.

## What changed

**A confirmation is now delivered to whoever is waiting for it, even while shutdown is under way.**
Only answers are carried this way, and only to someone who is still there to receive them; nothing
new is started during shutdown.

## What did NOT change

Everything that is *not* an answer is still stopped at shutdown, exactly as before. That is what
keeps a shutdown from turning into a flood of messages nobody will ever read, and a test now checks
that it stays that way.
