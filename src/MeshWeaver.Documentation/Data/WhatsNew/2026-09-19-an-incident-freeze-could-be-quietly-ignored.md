---
Name: An incident freeze could be quietly ignored
Category: Fix
Description: During an incident, a repository can be pinned to one specific platform build so that nothing moves while the problem is understood. With the stricter verification option turned on — the one that reads each build's own published receipt rather than trusting its metadata — the pin could be judged not to name the build it actually named, and the repository would quietly build against an older one instead. Green, and not what was asked for.
Icon: Bug
Order: -20260919
---

# An incident freeze could be quietly ignored

When something is wrong with a platform build, a repository can be **frozen** to one specific build,
named by the commit it came from. Nothing then moves while the problem is understood. A freeze is an
instruction, not a preference — the tooling either honours it or refuses out loud, and it is never
allowed to quietly substitute something else.

There is also a stricter verification option. Normally a build is identified by the commit its CI run
was started from; with verification on, it is identified by the **receipt the build itself publishes**
when it finishes, which is the authoritative statement of what was actually built. The two usually
agree. The whole reason the option exists is the cases where they do not.

## What went wrong

The decision "does this freeze name this build?" was made from the run's starting commit — *before*
the receipt was read. So with verification on, a build whose receipt names the frozen commit while its
starting commit does not was judged **not** to be the frozen build, by the definition of the very
option that was switched on.

For most builds that made no difference, because nothing else depended on the answer. But there is one
place it did: a build whose platform half has finished while its companion publication is still being
sealed is deliberately passed over as an ordinary candidate, and taken anyway when a freeze names it.
Judged not-frozen, such a build was passed over, its receipt never read, and the repository resolved to
an **older** build instead — with no warning, and a green result.

That is the path most likely to be used during an incident and least likely to be noticed while it is
happening.

## What changed

For the one case where the answer can change a decision — a freeze by commit, with verification on —
the receipt is now read *before* the decisions the freeze governs, and the freeze is matched against
it. A starting commit that matches still counts, so the ordinary build where the two agree behaves
exactly as before, and no extra reading happens on any other path. A build that cannot produce a
receipt at all still leaves the honest answer — "no evidence that this is the frozen build" — rather
than an invented one, because during an incident most builds in view are not the frozen one and
stopping on the first of them would make the option unusable.

When the frozen build is taken while its companion publication is still sealing, the existing warning
saying so is now actually printed, which is the line that tells a reader the publication may not be
fetchable yet.

## Proving it

The fix has a case on each side. The frozen build's receipt names the frozen commit, its starting
commit does not, and its companion publication is still sealing; a second, fully sealed build
republishes the same source under an older release number. With the fix, the frozen build is chosen
and the warning is printed. With only the fix removed, the same case chose the **older** build and
printed nothing — which is the defect, reproduced.

Separately, the part of the tool that reads how far its own repository has already got now has cases
proving it keeps reading past the first page of results — one with the evidence on the second page and
one with it on the last page it is allowed to read, each asserting which page was actually requested.
Both were confirmed to fail when the paging is broken, and the second also fails on an off-by-one that
stops one page early while the first still passes.
