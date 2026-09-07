---
Name: A stalled shutdown now names what actually stalled
Category: Fix
Description: When part of the platform took too long to shut down, the report always blamed something underneath it — even when there was nothing underneath. It now says which part is stuck, and admits it when it cannot tell.
Icon: Bug
Order: -20260907
---

# A stalled shutdown now names what actually stalled

When something inside the platform takes too long to shut down, it writes a report so an operator
can see what is holding it up. That report used to end with the same sentence every time: the hold-up
is in something below this — go and look there. On one restart it said that 47 times about parts that
had nothing below them at all, so every reader was sent to the wrong place.

Three of the numbers it printed were not measurements. One was a constant that read the same in every
possible state. One was printed under a name that meant the opposite of the value. One was the report
quoting its own starting point back as though it were progress.

Those are now real readings, and there is a new verdict for the case that had none — the one where
the work is queued and simply never picked up. When none of the known causes fits, the report now
says plainly that it does not know, and prints what it did observe, instead of picking the
nearest-looking answer.

This makes the next occurrence diagnosable. It does not by itself stop the stall.
