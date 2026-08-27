---
Name: Recursive delete no longer runs through the router
Category: Fix
Description: Deleting a space or folder with children no longer routes its per-node traffic through the mesh router, so large deletes stop competing with everything else the portal is doing.
Icon: Sparkle
Order: -20260827
---

# Recursive delete no longer runs through the router

Deleting a node with children checks every descendant first and then removes them one by one. Both of
those rounds of traffic used to be sent from — and answered back to — the mesh's router, the one
component every other message in the portal has to pass through. On a large subtree that meant the
delete competed with ordinary page loads and subscriptions, and it got worse the more children you
were deleting.

That work now runs on the mesh's dedicated node-operation hub instead, so a big delete no longer
slows down the rest of the portal. Nothing about what a delete is allowed to do has changed: a delete
you are not permitted to make is still refused up front, with the same message, before anything is
removed.
