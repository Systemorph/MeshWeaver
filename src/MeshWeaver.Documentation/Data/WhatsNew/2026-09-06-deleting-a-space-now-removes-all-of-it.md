---
Name: Deleting a space now removes all of it, whatever kind of space it was
Category: Fix
Description: A deleted space whose root was an installed plugin or course kept its whole database schema — invisible in every listing, and afterwards impossible to delete through the normal route. Removal now follows the shape of what you deleted rather than a list of two node types.
Icon: Checkmark
Order: -20260906
---

# Deleting a space now removes all of it, whatever kind of space it was

Deleting a space is supposed to take the whole thing: the pages, the threads, the comments, the
activity history, the access grants, and the database schema that holds them. For a space you had
created yourself, it did.

For a space that had arrived as an **installed plugin or course**, it did not. The pages went, the
delete reported success, and the space's entire database schema stayed behind — with every thread,
comment and activity still in it. Nothing said so. A listing of your spaces showed it gone.

## Why it was worse than leftover data

The leftover schema was not inert. The next time anything touched that space's name — a scheduled
re-check of your installed packages, for instance — an empty space reappeared at the same address,
carrying a fresh set of permissions that granted nobody the right to delete it. The result was a
space that was:

- **invisible as damage** — a normal listing showed either nothing at all, or an ordinary-looking
  empty space;
- **undeletable** — deleting it was refused, including for the person who had deleted the original;
- **still holding the data you asked to remove.**

Four spaces on our own staff portal were in exactly that state, from a single removal run.

## What changes

Removal is now decided by **what the thing is**, not by which of two node types it happens to
carry. A space is a top-level address, so anything at a top-level address is a space's root — an
installed plugin, an installed course, a customer record, a personal home, or one created by an
add-on that did not exist when the platform was built. Deleting it removes the schema and
de-registers the partition, in that order, so a removal that fails part-way stays visible for a
retry instead of disappearing.

Two things now make the old failure impossible to reintroduce quietly. A portal whose removal path
cannot handle an unfamiliar kind of space **refuses to start** and says why, instead of discovering
it on the next deletion. And the empty-space resurrection is closed at its source: a missing root is
repaired only for a space whose storage is still there, never conjured for one that was deleted.

## What this does not do

**It does not clean up spaces already in that state.** Schemas orphaned before this fix are still on
disk, and removing them is a deliberate, separate decision — the page
[Partition Teardown](/Doc/Architecture/PartitionTeardown) describes how to find them and what to
check before removing anything.

**A deletion that runs while something else is writing into the same space can still leave an empty
shell.** The window is now the length of the deletion rather than forever, and the leftover is an
empty space rather than a full schema, but it is not yet impossible.
