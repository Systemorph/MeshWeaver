---
Name: Pages stop serving a stale page after a deploy
Category: Fix
Description: A write made on one server is now seen by the others straight away, instead of leaving a page stuck on old state until it was recycled by hand.
Icon: Sparkle
Order: -20260820
---

# Pages stop serving a stale page after a deploy

When a portal runs on more than one server, a change saved by one of them was not announced to the others: their copy of the page could stay behind indefinitely, and no amount of reloading fixed it. On 17 August that is what left every course cover on the public site showing an error card for two hours — the servers disagreed about a node, and neither could learn better.

The servers now tell each other about every change as it is committed, and a page that hears about one re-reads it and catches up on its own. There is nothing to do and nothing to click: pages converge within a moment of the change, instead of waiting for a restart.
