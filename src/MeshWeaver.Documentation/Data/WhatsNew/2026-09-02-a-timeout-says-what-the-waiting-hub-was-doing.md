---
Name: A timeout says what the waiting hub was doing
Category: Fix
Description: When a request times out, the error now reports the state of the hub that gave up — and says plainly when it cannot tell whose fault the silence was, instead of naming two causes as though they were the only ones.
Icon: Timer
Order: -20260902
---

# A timeout says what the waiting hub was doing

`No response received in hub X within 00:01:00 for request Y → target Z` is the most common error
banner in the system. It used to end like this:

> The request may have been undeliverable or the target hub was not found.

Two causes, offered as though they were the whole set. They are not, and on 2026-09-02 both of them
were wrong at once.

Someone opened a document and got that message. It named the waiting hub and it named the document
as the target. So the obvious question was *does this document still exist?* — and that question was
never in doubt. The document existed, at version 53, edited the evening before. Its hub resolved
fine. Every other document in the same space opened instantly. Exactly one thing was wrong: the
component that owns that single document had stopped answering.

The sentence had sent the reader to the one place there was nothing to find.

## What it says now

The message now reports **the state of the hub that gave up** — what it was running, how much work
was queued behind it — and draws the distinction that actually matters:

- **If that hub was busy**, it says so, and says to look there first. A component that is itself
  stuck cannot tell the difference between "nobody answered me" and "an answer arrived and I never
  got round to reading it". From the inside, those look identical.
- **If it was genuinely idle**, it says that too — and then names the possibilities it *cannot*
  distinguish between: the request never arrived, it arrived at something stuck, or the reply was
  lost on the way back.

That last part is the real change. An explicit "I don't know, and here is exactly what I could and
could not observe" is more useful than a confident guess, because a message that offers two options
when there are four teaches people to pick whichever is nearer — and then to go looking in the wrong
place, which is what happened.

## Why this is worth a release note

Nothing about the underlying behaviour changed: the same requests time out after the same interval,
and a timed-out read is still retried exactly as before. What changed is that the error now carries
the evidence needed to act on it. On the deployment where this happened, the logs do not reach the
log service — so that one sentence was, quite literally, all there was to read.
