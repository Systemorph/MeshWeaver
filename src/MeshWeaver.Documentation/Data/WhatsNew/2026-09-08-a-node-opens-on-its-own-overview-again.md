---
Name: A node opens on its own Overview again
Category: Fix
Description: Opening a node without naming an area could land on a different type's area — and show "Area not found" on a node whose own page was perfectly fine. Which area you got depended on the set of areas installed on the mesh, so installing a plugin could change it. A node now opens on its Overview.
Icon: DocumentBulletList
Order: -20260908
---

Opening a node without naming an area — `/YourSpace/` rather than `/YourSpace/Overview` — is
supposed to show that node's own landing page. On some meshes it showed **"Area not found"**
instead, naming an area that belongs to a different node type entirely, while
`/YourSpace/Overview` rendered the complete page.

Nothing was wrong with the node. The portal was choosing the wrong area to open.

## What was happening

When a node type does not state which area is its default, the portal picked one. The rule it used
came down to "whichever area comes first" — over a dictionary, so the winner was decided by how the
area *names* hash, not by anything anyone configured.

That has an uncomfortable consequence: the answer depends on the **whole set** of areas present on
the mesh. Install a plugin that contributes one more area and the set changes, so the choice can
change too — on every node type, silently, with nothing in the logs to say the landing page moved.
Measured on a real mesh: a `Space` resolved its default to an area no layout on that hub even
registers.

## What it does now

A node whose layout states a default still uses it — nothing changes there, and almost every node
type in the platform states `Overview`.

When no default is stated, the portal now looks for `Overview` and opens that. Only if a layout
genuinely has no Overview — a single-area app view, say — does it fall back to the first area it
finds, so those keep working exactly as before.

The upshot: which page a node opens on is now a property of that node type, not of which plugins
happen to be installed alongside it.
