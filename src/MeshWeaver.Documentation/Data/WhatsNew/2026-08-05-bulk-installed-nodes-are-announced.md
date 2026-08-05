---
Name: A freshly installed plugin is fully there — no restart needed
Category: What's New
Description: Fixes an install landing a node that the running mesh could not see — the page reported "No node found" and the type never compiled until the portal restarted.
Icon: Sparkle
---

# A freshly installed plugin is fully there

Installing a plugin could leave one of its nodes written to storage but invisible to the mesh
that just installed it. The page for that node answered *"No node found"*, and if the node was
a type, nothing ever compiled it — so every plugin or course built on that type showed the
missing-type error instead of its content. Restarting the portal fixed it, which is a poor thing
to have to know.

The cause was an announcement that never went out. When a node is written one at a time, the
mesh publishes a change event and every cache that tracks where nodes live updates itself. The
faster bulk install path wrote its batches straight to storage and skipped that event, so any
path already remembered as "nothing here" stayed that way for the life of the process.

Bulk writes now announce every node they land, exactly as single writes do. A fresh install is
usable immediately, and the rule is enforced in one place, so no future bulk path can quietly
skip it.
