---
Name: Rollouts no longer stall on deleted course content
Category: Fix
Description: A course whose source files were removed can no longer block platform updates, and pages stay fast while an update is being prepared.
Icon: Sparkle
Order: -20260811
---

# Rollouts no longer stall on deleted course content

When a course or package was re-installed under a new name, its old type definitions could be
left behind without their source files. The platform's update safety-check mistook those
leftovers for damage caused by the new version and refused to finish the update — and while it
waited, pages across the portal could become very slow to open.

Both parts are fixed: content that is broken because its sources were deleted is now reported
separately and never blocks an update, and new page activations now stay on the healthy side
of the platform while an update is being prepared, so browsing stays fast during updates.
