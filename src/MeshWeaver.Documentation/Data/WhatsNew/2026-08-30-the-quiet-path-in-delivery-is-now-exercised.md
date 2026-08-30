---
Name: The quiet path in delivery is now exercised on every run
Category: Fix
Description: The delivery pipeline's bake-only branch runs only when nobody is pushing, so ordinary traffic never covered it — its first execution was in production and it failed nine times in a row over 33 hours before anyone noticed. It is now driven deterministically on every CI run.
Icon: BeakerEdit
Order: -20260830
---

# The quiet path in delivery is now exercised on every run

When continuous delivery reconciles and finds that the current commit already has a complete set of
images, it does not rebuild them — it re-asserts the content bake instead. That branch is reached
only when a reconcile finds nothing to build, which is to say **only when nobody is pushing**.

On a busy trunk it never runs. Its first execution was in production, at 22:12 on a Friday, and it
then failed nine times out of nine across 33 hours. Nobody noticed, because every push that might
have revealed it took the other branch — and each one repaired the appearance of the problem while
leaving it exactly where it was.

The specific defect was fixed. The *shape* was not: a code path gated on inactivity gets no coverage
from ordinary traffic, and no amount of care changes that.

It is now driven directly. The harness that already executes one delivery decision step — extracting
it from the workflow by its id, never copying it, so a rewritten step cannot leave a stale copy
passing — now executes the branch selector too, with the inputs the real step reads. Four cases: the
bake-only branch is taken when the image set is complete, it does not also publish, it says why, and
— the one that keeps the other three honest — an *incomplete* set on the same event still builds. If
those two ever collapse into one, delivery either stops or doubles.

## A safety device that turned out not to be optional

Writing those cases surfaced something worse than the gap. The step posts a comment to the
CD-failure issue when it decides to publish, and the harness ran it with the developer's own
environment — so on a machine with a live GitHub login, **running the tests posted real comments to
real issues**. It did, three times, and they had to be deleted by hand.

The harness now shadows the GitHub CLI with a stub that records what was asked and answers nothing,
and blanks the credentials as well, so neither alone is load-bearing. A test harness must not be able
to change anything outside its own temporary directory.
