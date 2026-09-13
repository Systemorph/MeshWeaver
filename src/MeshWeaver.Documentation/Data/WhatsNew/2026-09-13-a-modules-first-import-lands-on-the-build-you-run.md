---
Name: A module's first import lands on the build you are running
Category: Fix
Description: When a portal sets up a module for the first time, it now fetches the exact version of the sources that the code it is running was built from — instead of whatever the repository happened to look like at that second.
Icon: Checkmark
Order: -20260913
---

# A module's first import lands on the build you are running

When a portal is configured to pick modules up automatically, it does two things the first time it
sees one: it creates the module's space, and it fetches the module's content into it.

That second step used to ask the repository a question with a moving answer — *"what does this branch
look like right now?"* — and take whatever came back. Nobody was watching: this runs by itself, at
startup and whenever the catalogue is re-read.

## Why that was the wrong question

A portal does not run "the latest" of anything. It runs one specific build, and the ready-made
program files for the content it serves were produced from one specific version of the sources. Those
two have to match. When they don't, the portal cannot use the files it was given and has to rebuild
the content itself on every restart — slower, and occasionally not at all.

Asking for the branch as it stands invites exactly that mismatch, and the gap is never smaller than
the time between the last build and now. Everywhere else this had already been fixed: an update
triggered by a finished build names the version that build produced, precisely so the content and the
program files stay one thing. The first-time fetch was the last place still asking the moving
question. It was left that way deliberately, because it seemed to have nothing better to ask — the
usual signal arrives with the build notification, and a portal that receives none has no such
notification to read.

That turned out to be true and beside the point. Such a portal does hold the answer, on its own disk:
the ready-made files it starts from record the exact source version they were produced from.

## What changes

**The first fetch now asks for that version.** If the portal is running a prepared set of files for
the module's repository, the module's content is fetched at the version those files were built from —
so the content and the program files that serve it arrive as one matching pair.

**If the portal holds nothing for that repository, nothing changes.** It fetches the branch, exactly
as before. That is the case the old behaviour was there for, and it still works the same way,
including on portals that receive no build notifications at all.

**If the answer is unclear, it waits instead of guessing.** Where the prepared files are incomplete,
do not record a version, or two of them disagree, the module's space is still created and wired up,
but no content is fetched yet and the reason is written to the log. The next catalogue scan tries
again, so it resolves itself once the files are complete — and an empty new space is a far smaller
problem than content that does not match the program serving it.

## What this does not change

**Nothing about the "Update from GitHub" button.** Someone pressing it is asking for the branch as it
stands and still gets exactly that.

**Modules already set up are untouched.** This is only about the very first fetch into a new space.
