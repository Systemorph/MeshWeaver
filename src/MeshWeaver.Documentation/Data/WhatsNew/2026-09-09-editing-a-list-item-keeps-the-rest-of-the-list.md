---
Name: Editing one item in a list keeps the rest of the list
Category: Fix
Description: A node-bound field inside a list saves that item without discarding its siblings, and an edit that cannot be placed is refused instead of reshaping the data.
Icon: CheckCircle
Order: -20260909
---

# Editing one item in a list keeps the rest of the list

Editing a field inside a list — a note on one course, one row of a table — could replace the whole
list with a single object. The other entries were discarded, and because what remained no longer
had the shape of a list, the page failed to load it back: the edit and everything beside it
disappeared together.

Fields inside a list now read and save in place. Editing one entry leaves every other entry, and
every other field of the same entry, exactly as it was.

An edit that cannot be placed — an index that is not there, or a path that runs through a plain
value — is now refused and reported, instead of quietly reshaping the surrounding data to make
room for it.
