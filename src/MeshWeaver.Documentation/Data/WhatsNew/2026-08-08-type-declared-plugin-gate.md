---
Name: A plugin's paywall is now part of its type
Category: Feature
Description: Gated plugins declare their public cover, marketing page and checkout once on the node type instead of having a policy and a deny row written for every child.
Icon: LockClosed
Order: -20260808
---

# A plugin's paywall is now part of its type

A plugin that sells something has always needed the same shape: the cover, the marketing page and
the checkout stay open to everyone, everything else waits for a purchase. Until now that shape was
written out per plugin — an access policy node plus a deny row for each protected child, re-derived
every time the plugin synced.

That bookkeeping had a habit of going wrong quietly. The policy nodes were rewritten so often their
version counters reached six figures; what the gate wrote and what it later read back had drifted
apart; and because the gating keyed off a price field, two plugins that shipped without one were
left completely open — every page readable by anyone who guessed the address.

A plugin now declares its public surfaces and its "no access, go here" target **once, on its type**.
Nothing is written per plugin, so there is nothing to churn, nothing to drift, and no pass that can
fail to run for one plugin and not another. A plugin with no price is protected for the same reason
every other one is.

Buying still works exactly as before, and is now the only thing that opens the content: one
entitlement record on the plugin, and the whole plugin opens. Existing gated content keeps its
current setup untouched — the new declaration only ever opens the surfaces it names, so it cannot
close anything that is open today.
