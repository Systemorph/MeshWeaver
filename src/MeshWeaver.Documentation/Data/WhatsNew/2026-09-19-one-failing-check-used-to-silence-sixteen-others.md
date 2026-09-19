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

There is a genuine exception, and keeping it is the other half of the fix. A few steps at the very
front are real prerequisites — fetching the repository's files, and the tools to read them. If one of
those fails, the later checks have nothing to read, and letting them run would produce a page of
misleading errors and, for any check that happens to pass over an empty folder, a *false* pass. So
those front steps now announce that they succeeded, and every check asks for that announcement.
A prerequisite failing still stops everything, with its own error as the answer; a check failing stops
nothing but itself.

The same arrangement was found in the platform's own equivalent list — sixty checks behind two
prerequisites, covering the release pipeline and every script the other repositories fetch — and it got
the same treatment.

A new check keeps both lists that way. It refuses a newly added check that would go back to inheriting
the previous one's outcome, or that forgets to ask whether the prerequisites succeeded; it refuses
moving a prerequisite out of the front of the list, because one in the middle silences everything after
it for the same reason; it refuses a list with no checks left in it, so it cannot pass by having nothing
to look at; and it refuses a front step that has stopped announcing its success, which would silence
everything. Nineteen cases, each confirmed to fail on the defect it describes and to pass on the fix —
including on the arrangement as it stood before this change, where it reported all ninety-six checks.
