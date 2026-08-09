---
Name: Saved edits can no longer be rolled back
Category: Fix
Description: A stale copy of a node held by a component that is shutting down can no longer overwrite newer, already-saved content.
Icon: Sparkle
Order: -20260808
---

# Saved edits can no longer be rolled back

A node is only ever supposed to move forward: each save is a new revision on top of
the last one. There was a narrow moment during every save in which that guarantee did
not hold. While a save was landing, the check that refuses backward writes was still
describing the *previous* revision — so anything that presented an older copy of the
same node right then was waved through and overwrote the newer one.

Older copies are not exotic. A part of the system that is being recycled still holds
the node as it looked when it started up, and the save it flushes on the way out is
exactly that older copy. When it landed inside the moment above, the stored node went
back to the version it had at creation — and, worse, stayed there quietly: the next
edit was then counted from the rolled-back revision, so several acknowledged saves
could disappear with nothing reporting an error.

Backward writes are now checked against what is actually stored, in every case. A
stale copy is refused and the newer content is kept, so an edit that reported success
stays saved.
