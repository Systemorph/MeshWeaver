---
Name: A deploy no longer rolls past a plugin that stopped being baked
Category: Fix
Description: The gate that holds a release until every installed package has been built for it was deciding which packages to ask about by reading the same store it was about to judge — so a package whose build broke quietly dropped out of the check. It is now decided from what the environment has ever been able to use.
Icon: Checkmark
Order: -20260906
---

# A deploy no longer rolls past a plugin that stopped being baked

Before an environment moves to a new platform release, it checks that every package it has
installed has been prepared for that release. If something is missing, the update is held and the
reason is recorded — otherwise the environment would come up recompiling that content from source
on every start, slowly and sometimes not at all.

That check was passing when it should have held.

## What was wrong

The check has two halves: *which packages must be prepared* (the expected set) and *which ones
actually were* (what it finds). The second half was right. The first half was being worked out by
looking at the same storage the check was about to inspect — a package counted as "ships content"
only if it already had a prepared build there.

So the moment a package's build stopped being produced, that package quietly dropped out of the
list of things being asked about. It was no longer missing; it was no longer *expected*. Every
subsequent update went through cleanly, reporting nothing wrong, about exactly the package that had
broken. In the extreme — a storage location with nothing prepared in it at all — the entire content
half of the check had nothing to compare and still reported success.

This is what let environments move forward while course content had not been properly built.

## What changes

The expected set is now taken from what the environment has **ever** been able to use — every
platform build recorded in its storage, not just the one it happens to be running. A package that
has once shipped a prepared build stays on the list permanently, so a build that regresses to
nothing now holds the update instead of excusing itself from the question.

- **Updates hold, and say which packages and why.** The reason names each package that is missing a
  prepared build for the target release.
- **A package that genuinely ships no such content is still not asked for one.** Nothing that has
  never produced a build is suddenly demanded — an environment held forever would be a worse
  problem than the one being fixed.
- **"Nothing is prepared here" is now stated rather than passed over.** An environment whose
  storage holds nothing at all is reported as one the check does not apply to, with the reason
  written to the log, instead of quietly counting as a pass.
- **The expected count is logged**, so it is possible to see that the check looked at something.

No action is needed. An environment that is currently missing prepared content for a package will
now hold its next update and say so, which is the intended behaviour.
