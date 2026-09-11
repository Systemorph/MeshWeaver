---
Name: A page no longer goes dark on the first view after a restart
Category: Fix
Description: A portal replica could stop being reachable from its peers after a cluster change, so a content read timed out and the page came back empty until the next change repaired it.
Icon: Sparkle
Order: -20260911
---

# A page no longer goes dark on the first view after a restart

Each portal replica announces which addresses it serves, so its peers can reach it directly. That
announcement is republished whenever the cluster's shape moves — a replica starting, stopping or
being rescheduled — because moving the cluster is exactly what can lose it.

Two narrow windows let one of those republish signals go missing. Both fell inside the moments right
after a replica starts, which is when the cluster's shape is most likely to move, and in both the
signal was dropped rather than delayed: nothing retried it and nothing recorded it. The replica then
kept an announcement that no longer matched where its peers thought it lived, and nothing would
repair that until the cluster happened to move again.

What you could see when it happened: a read that should be instant instead ran to its time limit and
came back as an error, so an image or a document was blank on first view and fine on a refresh —
because the refresh took a different route. It looked like a slow network and was not.

Both windows are closed, and the announcement is now made on every cluster change including the ones
that arrive while an announcement is already in progress — still exactly once per change, so nothing
announces more often than before.
