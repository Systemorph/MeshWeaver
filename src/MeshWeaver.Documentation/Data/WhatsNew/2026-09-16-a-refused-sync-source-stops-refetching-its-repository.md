---
Name: A refused sync source stops re-fetching its repository — and an edit retries at once
Category: Fix
Description: A GitSync source whose subdirectory matches nothing used to be re-imported on every publication announcement at the same commit — a full repository fetch and an Activity about every two minutes, forever. The refusal is now a final verdict for that commit and that configuration, so nothing re-reads it until the repository moves or someone edits the source.
Icon: PlugDisconnected
Order: -20260916
---

# A refused sync source stops re-fetching its repository — and an edit retries at once

A Space synced from a repository subdirectory refuses to import when that subdirectory matches
nothing: an empty snapshot would prune the whole Space. Since this morning the refusal is recorded
on the source as `lastSyncOutcome: Refused`. What it still did was **try again** — on every
publication announcement, at the very same commit. On memex.systemorph.com that was about 32
refusals an hour for two Spaces, each one a full fetch of the repository and a new import Activity,
none of which could ever succeed.

Two things change:

- **A refusal is a final verdict.** The same commit under the same subdirectory lists the same
  nothing, so neither the green-build webhook nor the publication reconciler fetches it again. The
  settings tab shows the outcome as *refused* with what to check, and the usual *this commit has a
  final verdict* line.
- **Editing the source retries immediately.** The verdict is recorded against the configuration it
  was reached under — repository, branch, subdirectory, ignore patterns, direction and two-way. Change
  any of them and the next trigger attempts again at the same commit, instead of waiting for the
  repository to produce a new one. This applies to every final verdict, not only refusals.

A source recorded as settled before this change is attempted once more and then records the
configuration it was read under.

Full detail: [What a Green Build Costs a Synced Space](/Doc/Architecture/GitSyncTriggerCost).
