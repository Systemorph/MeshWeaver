---
Name: Deleting a folder no longer fails because part of it was already deleted
Category: Fix
Description: Deleting something with contents refused the whole operation if a single item inside it had just been removed by someone else — telling you it could not delete a node that was already gone, and leaving everything else untouched. The delete now accounts for that item and carries on, and still refuses when an item is genuinely still there.
Icon: Delete
Order: -20260918
---

# Deleting a folder no longer fails because part of it was already deleted

Deleting something that has contents — a space, a folder, a document with attachments — is not one
removal but many. The platform first takes a list of everything inside, then checks every item, and
only then removes them. That list is a snapshot, and in a shared workspace the world does not hold
still while it is being acted on.

**If anybody removed one of the listed items in that moment, the whole delete was refused**, with a
message saying that a node could not be deleted because no node could be found at its path. Nothing
was removed. The most ordinary way to hit it was the most frustrating one: two people tidying the
same space, or a cleanup script running while someone worked — and the remedy was to try again and
hope the timing was kinder.

That is a contradiction of the same shape the platform already fixed for a single node. Asking for
something to be gone and being told it cannot be removed *because it is already gone* is not a
failure — it is the request having been granted by somebody else first.

**A recursive delete now accounts for an item that disappeared while it was working and carries
on.** Everything else in the subtree is removed, the operation succeeds, and the record of what it
removed names only what this delete actually took away — an item somebody else removed is not
claimed as its own work.

**What has not changed is the protection that makes a delete all-or-nothing.** The refusal that
used to fire here is worded ambiguously: the same sentence appears when an item is missing *and*
when it is still there but cannot be opened. Those are opposite situations, and waving the second
one through is exactly how a half-destroyed folder happens. So the platform now asks the store
itself which one it is, and only an item the store confirms is gone is passed over. An item that is
still stored still stops the delete, with the same message as before and nothing removed — and if
the store cannot answer at all, the delete is refused rather than guessed at.
