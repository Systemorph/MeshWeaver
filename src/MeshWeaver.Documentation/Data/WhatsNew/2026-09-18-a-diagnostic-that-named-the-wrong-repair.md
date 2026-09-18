---
Name: A diagnostic that named the wrong repair now names the right one
Category: Fix
Description: When the platform caught a component handing work to the part whose only job is passing messages along, it printed instructions for fixing it — and those instructions had gone out of date, offering two remedies where there are three. Taking the wrong one would have silenced the warning and stopped the data with it.
Icon: Bug
Order: -20260918
---

# A diagnostic that named the wrong repair now names the right one

Inside an installation one component is the **router**: it exists to pass messages between all the
others, and nothing else. Handing it actual work competes with that job, and under load the
installation stops answering. The platform watches for this and writes a line into the log naming
what happened, where, and — the useful part — **how to repair it**.

That last part had gone out of date. There are three places work can be moved onto instead of the
router, one for each kind of thing being done: a change to a stored item, a one-off read, and a
**live connection** to something that keeps sending updates. The third was added recently. The
checks that scan the platform's own source learned about it; the printed instructions did not, and
nothing compared the two.

## Why a stale instruction is worse than stale prose

Nobody reads this line for pleasure — they read it while repairing something, and follow it. Of the
two remedies it still offered, one is actively wrong for a live connection: that destination is
built to answer nothing at all, deliberately, which is what makes it right for a one-off read and
wrong for a stream. Move a live connection there and the warnings stop — **and so does the data**.
The instrument goes quiet at the same moment as the thing it was watching, which is the one way a
diagnostic can make an installation harder to repair rather than easier.

It had already done damage of a smaller kind. The platform files its own reports from these lines,
and one such report repeated the outdated advice as its own diagnosis, pointing at a component that
had done nothing wrong.

## What changes

The line now names all three destinations, says what each is for, and says plainly that they are not
interchangeable and that choosing wrongly fails silently.

It also distinguishes something it used to blur. A warning can mean *this component handed out the
work* — in which case the place it names is the place to repair. But it can equally mean *this
component is answering somebody who asked it to*, and then the named place is the innocent half:
a reply goes where the request came from, so the repair is with whoever asked. The line now says
which of the two it is looking at, so the reader is not sent to correct code that is behaving
correctly.

The three destinations are now written down in exactly one place, and a check refuses any change
that leaves the printed advice naming fewer of them than the platform actually has.
