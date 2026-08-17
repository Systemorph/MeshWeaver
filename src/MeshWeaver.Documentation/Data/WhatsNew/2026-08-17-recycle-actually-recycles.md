---
Name: Recycle actually recycles now
Category: Fix
Description: The Recycle action asks before it acts and then works — it used to fire on page load and race itself, so it appeared to do nothing.
Icon: ArrowSync
Order: -20260817
---

# Recycle actually recycles now

Recycling a node's hub — the way to make it pick up a newly compiled build, or to clear a stuck
one — looked like it did nothing. You picked **Recycle**, the page said *"Recycling hub…"*, and
there it stayed.

Two things were wrong, and they compounded. Opening the Recycle page **was** the recycle: the
teardown fired while the page was still rendering, with no confirmation, so simply navigating there
acted, and any refresh acted again. And the page then had to deliver its own "all done, back to the
Overview" message *through the very hub it had just told to shut down* — a race it usually lost.

Recycle is now a confirmation. The page explains what will happen and offers **Recycle** and
**Cancel**, and nothing is torn down until you click. When you do, the redirect is sent first and
the teardown second, so nothing has to survive the shutdown and the page always comes back. The
next visit re-activates the hub against the latest compiled build.

The confirmation also states plainly that nothing is deleted and no content changes — recycling
restarts the node's hub, it does not touch the node.
