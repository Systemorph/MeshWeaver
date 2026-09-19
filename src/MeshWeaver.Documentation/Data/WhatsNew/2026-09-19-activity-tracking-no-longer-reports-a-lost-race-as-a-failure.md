---
Name: Activity tracking no longer reports a lost race as a failure
Category: Fix
Description: Opening the same page twice at once could make the portal report an error. Nothing was wrong — two visits had simply been recorded at the same moment and one of them lost a counter increment, which the next visit corrects by itself. The error was loud, unactionable, and kept re-opening an unrelated bug report that had already been fixed.
Icon: Bug
Order: -20260919
---

# Activity tracking no longer reports a lost race as a failure

Every time you open a page, the portal records the visit: when you last looked at it, and how many
times you have. That record lives on a node of its own, and — like every node — exactly one owner
decides what it says.

If two visits are recorded at the same instant, one of them has to lose. The owner takes the parts
that do not clash and refuses the rest, then tells the writer: *re-read and re-apply*. The platform
already does that, automatically, twice, each time re-computing the change against whatever the node
says now. Almost always the second attempt lands and nobody ever knows.

Occasionally it does not — the other writer is still going, and the retry budget runs out. That is
the case this fixes.

## What was happening

The portal reported it as an error, with a stack trace, as though the visit had failed to record.

It had not. The other writer recorded the visit; what was lost was one increment of the view counter
and a fraction of a second on the "last opened" time. The very next visit recomputes that counter
from whatever the node currently holds, so the number corrects itself without anyone doing anything.

The cost of calling it an error was real, though. That log line is what the incident watcher groups
faults by, so each lost race filed a production incident — and because the grouping is done on the
outer sentence rather than the specific fault underneath it, those incidents landed on an unrelated
bug report that had been investigated and fixed weeks earlier, re-opening it again and again.

## What changed

A lost race is now recorded quietly, with a sentence that says what actually happened: the owner
refused after the platform's own retries, this visit's increment is not recorded, and the next one
will fold it back in.

Everything else is untouched and still loud. A permission refusal, a malformed record, an owner that
cannot be reached — each of those is a genuine fault in activity tracking, and each still reports as
one. The distinction is drawn on the owner's own structured verdict rather than on the wording of a
message, so it cannot quietly stop working if either sentence is ever reworded.
