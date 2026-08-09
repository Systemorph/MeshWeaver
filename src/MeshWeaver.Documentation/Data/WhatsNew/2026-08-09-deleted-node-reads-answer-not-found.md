---
Name: Reading a deleted node answers "not found" straight away
Category: Fix
Description: A read that arrived while a just-deleted node was being torn down used to hang for its whole budget and report "unavailable" instead of "not found".
Icon: Sparkle
Order: -20260809
---

# Reading a deleted node answers "not found" straight away

Deleting a node and immediately reading it back could leave the read hanging for its entire
waiting budget and then reporting that the node's existence was *unknown*, rather than simply
saying it was gone. Agents saw this as "Unavailable — retry shortly" for a node that had
provably just been deleted, which is the one answer that helps nobody: it invites a retry loop
instead of moving on.

The cause was a timing race with the node's own shutdown. A node that is being torn down tells
callers "I am going away, ask again in a moment" — the right answer when it is only restarting,
and the wrong one when it has been deleted and is never coming back. The mesh now tells those
two cases apart and answers a deleted address definitively, so the read finishes in
milliseconds with a clear "no node found" instead of waiting in silence.

As a bonus, streams left behind by such a read no longer keep pinging an address that no longer
exists.
