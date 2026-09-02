---
Name: Deleting a node takes its bookkeeping with it
Category: Fix
Description: A delete could be refused because of the run records, comments and other bookkeeping filed beside the node — even for someone allowed to edit that node, which is the same permission those records are created under. The refusal is gone; a delete you may not do is still refused, and says so.
Icon: Delete
Order: -20260902
---

# Deleting a node takes its bookkeeping with it

Most nodes carry small records filed beside them: what happened on the last run, the comments
people left, the log of a build. You never create those by hand — they appear because you edited
the node, and permission to edit the node is what creates them.

Deleting the node should therefore take them with it, and the platform has always been written that
way. One check on the way in disagreed: it asked for delete rights on each individual record rather
than edit rights on the node they belong to. Anyone with edit access but not delete access hit a
wall — able to create the records, unable to remove them — and because that check runs before
everything else, its answer was the one that counted.

Concretely, on 2 September a course type on the production portal could not be removed at all: the
delete was refused once for each of its seventy-two run records, naming a permission that the
records' own rule says is not the one required. The type was already broken and deleting it was the
repair, so the repair was unavailable.

The check now asks each record's own type what it requires, exactly as the deletion itself already
did. Nothing widened beyond that: a record whose type states no rule is governed by the ordinary
delete permission as before, and where a type's rule says no, the delete is still refused — with the
reason, not with silence.
