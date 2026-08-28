---
Name: MCP patch confirms your own field, not just any write
Category: Fix
Description: Patching a node that another process also writes to (like the auto-update setting) no longer reports success unless your specific change actually landed.
Icon: Sparkle
Order: -20260828
---

# MCP patch confirms your own field, not just any write

Editing a node through an AI tool ("patch") used to confirm success as soon as the node's version
number moved forward — even if that version bump came from a different write than the one you
asked for. On a node that something else keeps updating in the background (the platform's
auto-update setting, for example, which the system itself checks every few minutes), that meant a
patch could answer "done" while your specific field never actually changed, with nothing to tell
you otherwise.

A patch now checks the field you actually asked to change, not just whether the node moved at all.
If your change cannot be confirmed, the tool now says so instead of reporting a false success.
