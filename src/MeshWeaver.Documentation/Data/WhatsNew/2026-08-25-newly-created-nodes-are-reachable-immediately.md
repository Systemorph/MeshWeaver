---
Name: Newly created nodes are reachable immediately
Category: Fix
Description: Fixes nodes that existed in the database but could not be opened — a freshly imported space or a newly created node could stay "not found" until the server restarted.
Icon: Sparkle
Order: -20260825
---

# Newly created nodes are reachable immediately

Some nodes were saved correctly and then stayed invisible to the running server. Opening them
reported that the page could not be found, even though the content was in the database the whole
time, and only restarting the server made them appear. It showed up most often right after importing
a new space or installing new content: one item — sometimes the space's own home page — would simply
refuse to open, while everything imported alongside it worked.

The cause was an internal announcement that two save paths were skipping. When a node is created,
the rest of the server has to be told, or the earlier answer — "there is nothing at this address" —
stays remembered. Both paths now announce their creations like every other save, so a node is
openable the moment it is written.

If you hit this before, the affected content was never lost; it is reachable again without any
action on your part.
