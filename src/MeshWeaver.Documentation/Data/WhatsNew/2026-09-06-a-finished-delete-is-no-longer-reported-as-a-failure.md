---
Name: A finished delete is no longer reported as a failure
Category: Fix
Description: Removing a large or busy branch could be declared a timeout after it had already finished — aborting the rest of an approved operation — while a delete that left a node behind was reported as done. Both come from the same completion check, and both are fixed.
Icon: Delete
Order: -20260906
---

# A finished delete is no longer reported as a failure

Deleting a branch removes everything under it, then goes back to storage and checks. Two things
about that check were wrong, and they pointed in opposite directions.

**A delete that had finished could be reported as a timeout.** The check gave the whole removal a
single time budget, so a delete that was steadily removing nodes was stopped once it had spent that
budget — not because it had stalled, but because there had been a lot to remove. On the production
portal on 6 September that ended a 68-step operation someone had approved at step 53, with 15 steps
never run, over a branch that was in fact already gone. The report even said so: it named more paths
removed than it had planned to remove, and still called it a failure. The budget now measures
something else — how long the delete has gone **without removing anything**. A delete that keeps
making progress runs to the end; one that genuinely stalls still fails, and now says "made no
progress" rather than quoting a count against a plan that was taken before it started.

**And a delete that had NOT finished could be reported as done.** The check looked at everything
*underneath* the branch, which never includes the branch's own node. So if that one node survived —
put back by something else, or removed by a storage backend that reported success without actually
removing it — the delete answered "done" with one node still there. Seven spaces in the same
operation were in exactly that state, and nothing said so. The check now covers the branch's own
node too: if it is still there, the delete removes it and looks again, and if it cannot be removed
at all the operation fails and names it.

The practical difference is that a successful delete now means the branch is gone.
