---
Name: A version number could be rewritten backwards
Category: Fix
Description: The tool that records each module's version derives the number from the releases it can see. In a copy of a repository that cannot see them all, it wrote a number lower than the one already recorded — silently, and reporting success. It now refuses and says which releases it could not account for, because a released version describes one set of files forever and re-issuing a lower one hands that number to a different set.
Icon: Bug
Order: -20260919
---

# A version number could be rewritten backwards

Each module in a content repository carries a small record of exactly what it contains, and the
version number in that record is *derived*, not typed in by hand: the tool reads the releases that
have already been published, compares this module's current contents against the newest one, and adds
one when they differ. That is what makes a version stable — it counts content changes, not builds.

The derivation is only as good as the list of releases it can see, and there are ordinary situations
where that list is incomplete: a copy of the repository fetched without its release history, or a
working copy that has no link to where the releases were published at all. The tool already refused
to derive a number when it could *ask* and got no answer. What it did not cover is the case where
there is nothing to ask.

## What went wrong

In that case the derivation quietly produced a *lower* number than the one already recorded, and the
tool wrote it. Whenever the module's contents had changed, the record had to be rewritten anyway — and
the version went out with it, downgraded, with a line reporting the rewrite as a success.

That is the one rewrite that can never be correct. A published version describes one exact set of
files forever; issuing it again for a different set means two different things ship under one number,
and everything that depends on that number is then pinned to whichever arrived second.

The tool's own warning elsewhere says exactly this. It was the tool itself doing it.

## What changed

Before writing anything, the tool now compares the number it derived against the number already
recorded. A derived number that is *lower* is not treated as a correction — it is treated as evidence
that the releases this copy can see are incomplete, which is what it actually is. The run stops,
names each module with both numbers, and says which of the two situations it is in and what to do
about it: fetch the missing release history, or run the tool where that history is available.

The check happens before the first file is written, so a repository with several modules cannot end
up with the first few downgraded and only then be refused.

If the recorded number really is wrong — typed in by hand, or left behind by a module that moved — the
message says so too, and asks for a deliberate correction. The tool will not make that one for you,
because it cannot tell the difference between a number that is wrong and a release it simply cannot
see.

## Proving it

Both directions are now cases in the tool's own self-test, which every content repository runs: a
content change whose derived number is below the recorded one must refuse, and the same change with
nothing recorded above what can be derived must still go through and move the version forward. The
first was confirmed to fail on the previous code — it rewrote a recorded 1.2.1 to 1.2.0 and reported
success — and the second is there so the refusal cannot pass by refusing everything.
