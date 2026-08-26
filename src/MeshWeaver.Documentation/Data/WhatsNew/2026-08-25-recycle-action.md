---
Name: Recycle is one click in the node menu
Category: Feature
Description: The ♻️ Recycle entry now recycles the node straight away, with a confirmation on the page instead of a separate confirmation page.
Icon: Sparkle
Order: -20260825
---

# Recycle is one click in the node menu

Recycling a node used to take two page loads: the ♻️ menu entry navigated to a confirmation
page, and that page held the button that did the work. Now the menu entry *is* the action —
picking it asks you to confirm right where you are and then recycles the node.

The page refreshes once, as a page, when the node is genuinely back. Previously each part of
the landing page rediscovered the restart on its own and you watched the screen reassemble
itself piece by piece.

The old `/{node}/Recycle` link still works — the "a newer build is available" banner and any
bookmark you kept now run exactly the same one-click flow.
