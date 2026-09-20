---
Name: A recycle can now say why
Category: Fix
Description: Recycling a node tears its hub down and rebuilds it, and the log line recording that has always named who did it and through which button. What it could not carry was the operator's own reason, because the surface they use took a path and nothing else. It can now, and a recycle with no reason still records the same sentence it always did rather than a blank.
Icon: ArrowSync
Order: -20260920
---

# A recycle can now say why

Recycling a node tears down the running copy so the next visitor gets a freshly built one. It is the
standard remedy after fixing something the platform had already loaded — a corrected source file, a
package that installed while the old version was still in memory.

Every recycle writes a line saying the hub was torn down and why. That line has named *who* asked
and *through which surface* since an earlier fix stopped it being anonymous. What it could not carry
is the thing only the person clicking knows — what they were actually trying to fix.

## Why that half was missing

Not an oversight in the log line, but in the surface. The operator-facing way to recycle accepted a
path and nothing else, so there was nowhere to put a reason even for someone who wanted to give one.
The platform's own guidance says always to state one; the tool it named could not.

## What changed

The reason can now be supplied and travels into the record, alongside the sentence that was already
there rather than instead of it — the two answer different questions, and a reader working backwards
through a log wants both.

## Deliberately unchanged

Leaving the reason out still produces exactly the sentence it produced before. That sounds obvious,
but it is the half worth stating, because adding an optional field is precisely the change that
quietly turns a helpful fallback into an empty string — and an empty reason is worse than the
fallback, since it looks like someone answered. Blank and whitespace-only reasons are treated as no
reason at all, which is what a form with an untouched text box sends.
