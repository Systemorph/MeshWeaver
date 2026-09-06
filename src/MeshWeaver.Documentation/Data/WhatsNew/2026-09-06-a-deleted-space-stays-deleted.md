---
Name: A deleted space stays deleted
Category: Fix
Description: A space you deleted could quietly come back as an empty shell nobody could delete again, and an installed package whose space you removed kept trying to write into it on every restart. Both are closed.
Icon: Delete
Order: -20260906
---

# A deleted space stays deleted

Deleting a space is meant to be final. On production portals it sometimes was not: a space would
reappear a little later as an empty shell — carrying its old name, none of its content, and a fresh
access policy that granted delete rights to nobody, including the person who had just deleted it. It
did not show up as damage in any listing, and it could not be removed again through the ordinary
interface.

What brought it back was the platform's own self-repair. When something is written into a space whose
top-level entry has gone missing, the platform recreates that entry so the space stays reachable —
a repair that exists because a half-finished creation used to leave spaces unusable. The repair also
made sure the space's underlying storage existed, and *that* is what made it a resurrection rather
than a repair: a write arriving while a deletion was still running found storage that was genuinely
still there for another moment, and the repair put it back after the deletion had removed it.

Repair and creation are now separate things. The self-repair can still restore a missing top-level
entry over a space that exists, which is what it is for — but it can no longer create a space's
storage, so it has nothing to bring back. On top of that, the platform now remembers, for as long as
a deletion is running and for a short window after it, that a given space was deleted; a repair for a
space in that state is refused outright, and so is the access grant that used to come with it.

## Installed packages let go of spaces that are gone

The second half was quieter. Installing a package records the installation centrally, not inside the
space it installs into — so deleting that space left the record behind, pointing at nothing. On every
restart the platform re-applied the package's access settings to a space that no longer existed:
a failed write and an error in the log, every boot, for every space anyone had ever deleted.

Now the record goes with the space: deleting a space removes the installation records that name it,
so the package correctly reads as not installed. Records that were already orphaned before this
change are no longer written to — the platform names each one in the log once per restart, with the
remedy, and administrators can clear them from the catalog's orphaned-records list.

Nothing changes for ordinary use. Deleting a space and creating a new one with the same name works
exactly as before, and a space whose top-level entry really did go missing is still repaired on the
next write into it.
