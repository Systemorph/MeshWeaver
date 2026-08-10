---
Name: A blocked update now tells you why
Category: Feature
Description: A candidate release can now be verified against exactly the modules your portal runs — one command builds and tests your set inside the new image — and the verdict lands on the admin Updates tab, so a build that would break you reads "cannot update — these modules fail against it" instead of an eternal "update available".
Icon: ShieldCheckmark
Order: -20260810
---

# A blocked update now tells you why

Until now, an update that would break your portal was invisible until it broke
it. The admin tab said "update available", the platform tried to take it, and
the only evidence that something was wrong lived in the logs of a pod that
never came up — the hardest possible place to look.

The verification gate is now wired end to end. One command takes the exact set
of modules your portal runs — rebuilt at the versions you actually have — and
compiles, renders, and tests every one of them **inside the candidate image**,
before that image is ever offered to your instance. The outcome is one of
exactly three honest answers:

- **Green** — every module builds and tests cleanly against the candidate.
- **Red** — the candidate breaks you, and the verdict names *every* failing
  module with its diagnostics, never just the first one found.
- **Not verifiable** — the question could not be answered (a module pinned to a
  moving branch, a fetch that failed), with every reason listed. An unanswered
  question is never dressed up as either "broken" or "all clear".

The verdict is recorded on the platform's update policy, next to the version it
is about. The admin **Updates** tab shows it where the decision is made: a
verified build reads as verified, and a breaking one reads "cannot update to
this version — these modules do not compile or test against it", with the
module list right there. In English and German, like the rest of the portal.

Every verdict also names precisely what it checked: each module's resolved
commit and a fingerprint of its content, plus the digest of the image it ran
against — so "verified" always means *this* set against *that* build, not a
rough approximation of either.
