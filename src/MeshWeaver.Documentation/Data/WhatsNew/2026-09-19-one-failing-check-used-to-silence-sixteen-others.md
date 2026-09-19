---
Name: One failing check used to silence sixteen others
Category: Fix
Description: The shared checks that every content repository runs before a change can be merged were arranged as one long list, and the first one to fail stopped all the rest from running. Those skipped checks reported nothing at all, which the merge rules read as "nothing to object to" — so while one unrelated check was red, six real safety checks were quietly not being applied to any change in that repository.
Icon: Bug
Order: -20260919
---

# One failing check used to silence sixteen others

Every content repository runs a shared list of checks before a change can be merged: that the
package records describing each module are current, that each module's version matches what is
actually in it, that no credential a check needs can be silently missing, that no pinned reference
names a commit the repository no longer carries, and about thirty others. They are independent of one
another — each asks its own question about the repository.

They were arranged as one sequence, and the default behaviour for a sequence is to stop at the first
failure. So whenever any one of them went red, every check after it was **not run** — and a check
that was not run reports nothing, rather than reporting a problem. The merge rules treat "nothing
reported" from a check that was asked to run as "no objection", so the changes in that repository
were being merged with six of those checks having said nothing about them.

## Why it was invisible

Nothing in the arrangement looked wrong. There was no setting saying "ignore failures" and no
condition saying "only run this sometimes" — the kind of thing a reader looks for. The silence came
purely from the *order* the checks were written in.

And the symptom was misleading. The one visible red named whichever check happened to be first,
which was usually about something unrelated to the change under review — so the natural reaction was
to look at that one file, fix it, and move on, with no reason to suspect that six other checks had
been skipped in the meantime.

It was measured on a real change: one first check failed over a file that had merely fallen behind a
shared copy, and sixteen later steps were skipped behind it, including the check that catches a
module shipping at a version nobody will fetch.

## What changed

Every one of those checks now reports its own verdict independently: a failure in one no longer
prevents the others from running, and the change is still refused if any of them fails. The result
is that a red now tells you *everything* that is wrong, instead of the first thing.

A new check keeps it that way. It reads the shared list and refuses any newly added check that would
go back to inheriting the previous one's outcome, and it also refuses moving one of the three genuine
prerequisites — getting the repository's files and the tools to read them — out of the front of the
list, because a prerequisite in the middle silences everything after it for exactly the same reason.
Its own self-test was confirmed to fail on the arrangement as it stood before this change, on all
thirty-six checks, and to pass after it.
