---
Name: A node can no longer quietly revert to an older version
Category: Fix
Description: Concurrent edits from different servers are merged instead of overwriting each other, and anything that could not be merged is recorded in the activity log.
Icon: Sparkle
Order: -20260809
---

# A node can no longer quietly revert to an older version

When more than one server was handling your portal — during an update, after a restart, or when
extra capacity was added automatically — a save that had been sitting on an out-of-date copy of a
node could overwrite a newer one. The overwrite reported success, so the only sign anything had gone
wrong was a page that had mysteriously reverted to older content.

Saves are now checked against the stored node itself rather than against each server's own memory,
so an out-of-date save can no longer replace newer content. Instead of being rejected, it is merged
into what is already stored: text that only one side had is kept, and where the two genuinely
disagree the newer value wins.

Anything that could not be merged automatically is written to the node's activity log, naming
exactly which field was affected. Nothing is discarded silently any more.
