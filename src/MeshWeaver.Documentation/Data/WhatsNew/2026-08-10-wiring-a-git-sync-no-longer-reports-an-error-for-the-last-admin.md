---
Name: Wiring a Git sync no longer reports an error for the last admin
Category: Fix
Description: Making a space system-owned now keeps its last administrator deliberately instead of failing an impossible removal.
Icon: Sparkle
Order: -20260810
---

# Wiring a Git sync no longer reports an error for the last admin

When you wire a GitHub sync onto a space, the space becomes system-owned and existing
write grants are retracted. If the only administrator of the space was among them, the
platform correctly refused to remove the last admin — but the sweep reported that refusal
as an error, as if something had gone wrong.

The sweep now recognises this case up front: the space's last administrator is kept on
purpose, the log states clearly what was kept and why, and no error is raised. If the
space has other admins, everyone but the earliest-granted one is still retracted as
before; once another admin exists, re-wiring the sync converges the rest.
