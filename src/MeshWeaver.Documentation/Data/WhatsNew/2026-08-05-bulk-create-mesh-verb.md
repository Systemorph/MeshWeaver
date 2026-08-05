---
Name: One round-trip to create many nodes — the bulk create verb
Category: What's New
Description: Plugins and services can now create a whole plan of nodes in a single validated, permission-checked request instead of one round-trip per node.
Icon: Sparkle
---

# One round-trip to create many nodes

Anything that lands a plan of nodes — installing a course's exercises into your own space,
importing a module, seeding a workspace — used to issue one create request per node, each
paying its own round-trip through the mesh. Copying a course of a few dozen subtrees took
minutes for that reason alone.

The mesh now has a bulk create verb: one request carries the whole plan, every node is
validated and permission-checked up front (a refusal writes nothing at all), and the batch
lands in one ordered storage write with every downstream reader notified node by node, in
order. Nodes you already have are skipped and reported, never overwritten — and everything
that deliberately stays per-node (access grants, satellites, updates) keeps its careful path.
