---
Name: A delivery alert now closes itself, and a new CI gate reaches the merge queue
Category: Fix
Description: The issue that tracks a broken image publication stayed open forever and collected four days of unrelated causes; and a gate added to main was skipped on exactly the change it was written for. Both are placement bugs, and both are fixed.
Icon: CheckmarkCircle
Order: -20260907
---

Two CI signals were reporting things they could not actually mean.

**A delivery alert that could never be resolved.** When a build fails to publish a complete set of
deployment images, CI files a GitHub issue so somebody notices — and the reconciler that re-attempts
the publication writes its attempts onto the same issue. When the hole was filled, the bot commented
*"Close this issue if nothing else is outstanding"* — advice, to nobody in particular — and closed
nothing.

Nobody ever did. Because every later failure appends to whichever alert issue is **open**, the first
one ever filed absorbed every attempt and every heal from then on: **324 comments spanning four days
and a dozen unrelated causes**, under a title describing one commit's problem that had stopped being
true on day one. That takes away the only two things an alert says. Its *existence* is supposed to
mean "delivery is broken right now", and an issue that is open permanently cannot mean that. Its
*age* is supposed to be the outage's age, and it was measuring the age of an unrelated outage from
the previous week.

The successful heal now closes the issue. Nothing is lost — the thread and every comment stay where
they were — and the next failure opens a **fresh** one, so an alert's title names its own cause and
its age is its own. A close that is refused by the API is a warning rather than a failure: the issue
just stays open and the next heal closes it, and turning a hiccup in the alerting into a red build
would report a broken delivery on a run that delivered.

**A gate that missed the one change it was written for.** A pull request has two builds, and a check
added to `main` afterwards can miss *both*: its own build runs the workflow file as it stood when the
branch was cut, and the merge-queue build — the one that sees the change at the moment it lands —
skipped the job the new check had been put in. So the check went live, and every pull request already
green at that moment was silently exempt from it, including the single one that changed the code the
check exists to watch. No harm that time, by luck.

The check has moved into the job that runs on **both** events, so the queue build now enforces it at
the moment a change becomes `main`. Where a declaration in the pull-request body can relax it, that
still works on the pull request; in the queue — which can carry several pull requests at once — the
strict rule applies. It was verified the way the report says it should be: watched passing on a clean
tree, then watched **failing** on a deliberately blinded parser, then watched passing again.

Both fixes are exercised by a test that extracts the workflow steps and runs them, so neither can
quietly stop doing its job.
