---
Name: A click made as a tab closes still reaches its page
Category: Fix
Description: When a browser tab closed or lost its connection while a click was still waiting its turn to leave the portal, closing the tab's connection turned the click away and the action it asked for never ran. The portal now finishes sending the actions it already accepted before it stops routing for the closing tab.
Icon: Sparkle
Order: -20260916
---

# A click made as a tab closes still reaches its page

Every open tab has its own connection hub in the portal, and each page it shows has a small queue
of its own. A click joins that queue and leaves it for the node that owns the button. When the page
queue was busy — applying a large update, say — and the tab closed or dropped its connection at that
moment, the tab's hub stopped routing for its pages in the same step in which it asked them to shut
down. The click was still in the queue, so it was turned away at the tab's own door and the action
never ran.

The tab's hub now keeps carrying what its pages had already accepted until those pages have finished
shutting down, and only then stops routing. Nothing waits longer than the page takes to hand its
queue on, and a click made after the page began closing is still refused rather than sent to a page
that is gone.

The measurement and the other teardown route that was already safe are in
[Refusing a Lost User Action](/Doc/Architecture/RefusingALostUserAction).
