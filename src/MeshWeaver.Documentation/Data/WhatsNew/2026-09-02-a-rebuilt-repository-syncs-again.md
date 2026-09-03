---
Name: A rebuilt repository syncs again
Category: Fix
Description: A content repository whose build was started by something other than a commit — a platform release re-verifying it, a nightly run, a manual re-run — went green without its content ever reaching the spaces that sync it. One space sat 38 hours behind, with every part of the chain reporting success. Those builds now count.
Icon: ArrowSyncCheckmark
Order: -20260902
---

# A rebuilt repository syncs again

Content that lives in a git repository reaches your installation when that repository's **build goes
green** — never on the merge itself, and never on a timer. The build is the evidence: it is the only
thing that knows whether what was merged is actually installable, so the space waits for it.

That works exactly as intended when the build was started by a commit. It did not work when the build
was started by anything else — and increasingly, builds are.

A platform release asks every content repository to rebuild itself against the new platform, to
confirm it still works. Nightly runs do the same. Someone re-running a build by hand does the same.
In all three cases the repository builds the *same* branch, the *same* files, and reports the *same*
green verdict — there is simply no new commit behind it. The chain that listens for green builds was
looking for a commit, found none, and quietly dropped the result.

Quietly is the problem. Nothing failed: the connection was live, every delivery was accepted, every
build was green, and every dashboard agreed. The only visible symptom was content that had stopped
arriving. One space stayed **38 hours** behind a change that had been merged and verified, and it
would have stayed there indefinitely — there is no background poll behind this to eventually notice,
because a poll would import content no build had vetted.

**A green build of a repository's main branch now counts as a publish signal however it was
started** — a commit, a platform release, a schedule, or a person pressing the button. The two things
that must be true are unchanged, and one of them is now stated rather than implied:

- the build must be of the **main branch's own content** — a pull-request build is unmerged code and
  is still refused, as is anything that merely *reports* green without building the branch (a
  code-review bot, for instance);
- and it must have **passed**.

Anything not on that list is refused rather than admitted, so a new kind of build event cannot start
publishing by accident.

Re-verifying a repository that has not changed stays silent: a space already holding the built
version is skipped, so a nightly rebuild of an untouched branch does nothing at all — exactly as
before.
