---
Name: Anchored edits are now atomic
Category: Fix
Description: edit_content verifies its anchor against the live document at write time, so concurrent edits no longer clobber each other and a lost write reports an error instead of success.
Icon: Sparkle
Order: -20260817
---

# Anchored edits are now atomic

The `edit_content` tool used to check its text anchor against a snapshot read before the write, so an edit landing in between could be silently overwritten — and a write that never applied could still report "Edited". The check-and-replace now runs against the live document inside the owning hub's write turn: two people (or agents) editing different parts of the same document at once both keep their changes, an anchor that was edited away is refused with a clear message, and the success message is only reported once the edit has provably landed.
