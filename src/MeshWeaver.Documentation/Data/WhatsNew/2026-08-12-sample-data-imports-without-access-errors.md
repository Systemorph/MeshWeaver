---
Name: Sample data imports without access errors
Category: Fix
Description: Syncing the MeshWeaver samples no longer reports dozens of refused access grants — the import finishes clean.
Icon: Sparkle
Order: -20260812
---

# Sample data imports without access errors

Every time a portal synced the MeshWeaver sample content, the import finished with errors and the
log filled with dozens of refused access grants. The sample data shipped access assignments that
gave named demo users edit rights on a space the repository owns — a combination the mesh refuses
by design, because a space kept in sync with a repository is rewritten on every sync and only the
sync itself may change it.

The sample grants are now read-only, so the import completes cleanly and its status reads
"Imported" instead of "Imported with errors". Three grants that named a role which does not exist
now name a real one, so the group hub they were meant to serve actually gets the read access it
was always supposed to have.

Nothing changes for anyone's real access. Administrator rights on the demo spaces were only ever
meaningful on a developer's own machine, and are now configured there the same way every other
deployment configures them.
