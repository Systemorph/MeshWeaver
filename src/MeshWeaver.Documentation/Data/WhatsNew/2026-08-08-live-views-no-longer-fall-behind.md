---
Name: Live views no longer quietly fall behind under heavy editing
Category: What's New
Description: A view watching a node that many people (or agents) were writing to at once could silently stop showing some of the newest entries; it now stays in step with the node.
Icon: Sparkle
---

# Live views no longer quietly fall behind under heavy editing

When a lot of writes landed on the same node at once — a busy chat inbox, an
import, several agents editing in parallel — a view watching that node could
quietly lose a few of the entries that were being added. Nothing failed: every
write was accepted and safely stored, the page kept updating, and later entries
kept appearing. The view simply stayed a few entries short of the truth, and
stayed that way until it was reopened.

The cause was the recovery step a view takes when it notices it has fallen
behind: it asks the node to re-send its current state, and that re-send could
carry a slightly older snapshot than the view had already applied — quietly
erasing whatever arrived in between. The re-send now always carries the node's
state as of the moment it is sent, so catching up can only ever move a view
forward.

Your stored data was never affected: the node itself always held every write.
This only affected what an already-open view was showing.
