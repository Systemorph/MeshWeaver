---
nodeType: WhatsNew
Name: A safety net that could never have caught anything
Category: Fix
Description: "A build check that promised to give up after thirty seconds would in fact have waited for ever, because of the order it did two things in. It now gives up when it says it does, and the two other checks written the same way were already correct."
Icon: Timer
Order: -20260918
---

One of the checks that runs before a release starts a small helper program, reads everything it
prints, and then waits up to thirty seconds for it to finish — failing with a clear message if it
has not. The thirty seconds are there for one situation only: the helper getting stuck.

That is the one situation in which the thirty seconds could never have been reached. Reading
everything a program prints finishes when the program has nothing left to say, and the way a
program says it has nothing left is by **ending**. So the reading step could only complete once the
helper had already finished — and if the helper hung, the check sat in the read, for ever, never
arriving at the countdown meant to rescue it. The bound was reachable only when it was not needed,
and the message naming it could never have been printed.

Nothing has ever gone wrong here: the helper is a short calculation and has always finished in a
tenth of a second. What was wrong was the guarantee. A check of this kind exists to turn a hang into
a named failure, and this one would have turned a hang into a hang — on a build machine, that is a
job burning its whole time budget and then reporting nothing about why.

The obvious repair, doing the two steps the other way round, trades one stall for another: a program
whose output nobody is reading eventually fills the buffer it is writing into and stops there, so
the countdown would expire on a helper that had finished its work and was simply waiting to be
listened to. The fix is to listen and count at the same time, which is what the check now does — and
what two other checks in the same codebase, written with the same problem in mind, already did. When
the bound is now exceeded, the stuck helper is stopped rather than left running, and the failure says
so.

The fix was confirmed by pointing the repaired check at a helper deliberately built to hang: it now
reports its named failure after thirty seconds, where the previous version waited indefinitely.
