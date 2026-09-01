---
Name: The Store works on installs where content types had no live instances
Category: Fix
Description: A defined NodeType's content type now registers at startup, so Store covers show their Get/Install/Update buttons even on portals where no node of that type exists yet.
Icon: Sparkle
Order: -20260901
---

# The Store works on installs where content types had no live instances

On some installations — a fresh local portal most visibly — every card in the Store rendered
without its action buttons: no Get, no Install, no Update, and the Subscribe page claimed the
product was not for sale. Courses were installed and readable, yet nothing about them could be
managed.

The cause was an accident of timing, not of configuration. The type describing a package's
commercial face registered itself only when a node of its own kind first woke up — and on an
installation that happened to have no such node, it never registered at all. Every screen that
needed the package's price, tier or install actions then received data it could not interpret,
and quietly rendered nothing. Deployments with such nodes escaped by luck, which is what let the
gap hide for so long.

Defined types now register at startup, and recompiled types re-register on the spot — whether or
not a single node of theirs exists. On an affected portal, one restart after this update brings
the Store's buttons back, and with them the normal way of updating installed content.
