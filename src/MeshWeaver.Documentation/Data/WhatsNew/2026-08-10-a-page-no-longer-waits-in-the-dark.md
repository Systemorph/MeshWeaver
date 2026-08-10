---
Name: A page no longer waits two minutes in the dark when its node restarts
Category: Fix
Description: Opening a page at the exact moment its node was restarting could leave it dark for two minutes. The page now notices and reconnects by itself within a second or two.
Icon: ArrowSync
Order: -20260810
---

# A page no longer waits two minutes in the dark when its node restarts

Nodes restart routinely — right after a plugin install, after their code is
rebuilt, after a recycle. A page opened in exactly that moment used to connect
to the node that was on its way down, and then simply wait: nothing rendered,
no error, no retry, until the page gave up minutes later.

The restarting node already sends an honest answer — "I am coming back, ask
again" — but the page never acted on it. It now does: on that answer it re-asks
after a short pause, and the re-ask itself is what brings the fresh node up. A
page caught in a restart window therefore comes alive within a second or two
instead of sitting dark.
