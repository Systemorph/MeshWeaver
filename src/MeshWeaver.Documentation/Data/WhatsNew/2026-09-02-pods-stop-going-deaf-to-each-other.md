---
Name: Portal replicas stop going deaf to each other
Category: Fix
Description: Fixed a fault that could leave one portal replica permanently unable to answer another — showing up as pages that never finish loading, edits that never confirm, and downloads that answer "temporarily unavailable" for hours.
Icon: Sparkle
Order: -20260902
---

# Portal replicas stop going deaf to each other

A portal runs as several replicas, and they answer each other constantly: the replica serving your
page asks whichever replica owns your data, and the answer has to find its way back.

Finding the way back relied on a piece of routing information each replica publishes once, when it
starts. The cluster reorganises that information every time a replica joins or leaves — which
happens on every deploy and every time the portal scales — and if a replica's entry was lost in that
reshuffle, **nothing ever put it back**. From then on, every answer addressed to that replica was
refused, silently, for as long as it kept running. The replica was perfectly healthy; the rest of the
cluster simply could no longer find it.

What that looked like from the outside was maddeningly inconsistent, because it depended on which
replica happened to serve each request: a page that never finishes loading while a reload works
fine, an edit that never confirms, a plugin download that answers *"temporarily unavailable"* on one
attempt and succeeds on the next. On our own cloud portal one replica spent twelve hours in this
state — through a restart — while the others were fine.

Each replica now republishes its routing information whenever the cluster's membership changes,
which is exactly when it can be lost. A replica that misses its slot gets it back at the next change
instead of staying unreachable for the rest of its life.

The refusal message also now names which replica answered it, so the next occurrence of anything in
this family can be told apart from its neighbours in one log line rather than a day of measurement.
