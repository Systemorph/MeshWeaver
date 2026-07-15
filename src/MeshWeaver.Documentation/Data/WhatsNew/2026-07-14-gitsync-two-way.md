---
Name: Two-way GitHub sync — your edits are kept, not overwritten
Category: What's New
Description: A GitHub sync source can now keep changes made on the server since the last sync, instead of overwriting them on update.
Icon: Sparkle
---

# Two-way GitHub sync

GitHub sync used to be strictly git-first: an **Update to latest** overwrote (and pruned) the live
Space from the repository, silently discarding edits you'd made on the server between syncs.

Turn on **Two-way** on a sync source and an update now keeps any node changed on the server since
the last sync — it is preserved, not overwritten or pruned, and is carried back to GitHub on your
next **Commit** ("newer on the server wins"). Nodes the server hasn't touched still update from the
repo as before.

Need the old behavior for a one-off? A **forced** update (`force`) overwrites local changes from the
repository regardless — the deliberate way to discard server edits and match the repo exactly.
