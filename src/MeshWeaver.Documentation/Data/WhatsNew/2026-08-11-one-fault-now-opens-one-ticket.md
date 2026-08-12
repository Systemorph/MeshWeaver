---
Name: One fault now opens one ticket
Category: Fix
Description: Automatic incident tickets no longer double up when a single error is logged by more than one component.
Icon: Sparkle
Order: -20260811
---

# One fault now opens one ticket

When the portal hits an error in production, it opens a ticket for you automatically — and the
promise has always been one ticket per defect, however many times the error fires. That promise was
being broken in a specific way: a single failure that gets logged more than once on its way up (once
by the component that hit it, again by the component that cleaned up after it) was counted as two
separate defects and opened two tickets, with the same stack trace in both.

The incident's identity is now taken from the fault itself — the method that failed and the error it
raised — instead of from whichever component happened to report it. Reports of the same failure now
fold into one ticket with a single occurrence count, no matter how many places logged it.

Errors that genuinely happen in two different places still get their own ticket each: the change
removes duplicates without hiding one defect behind another.
