---
Name: Modules install and update from the Store
Category: Feature
Description: A Store package can now carry a compiled module — it installs through the normal funnel, and updates itself automatically under your instance's existing update policy (Continuous by default).
Icon: Sparkle
Order: -20260816
---

# Modules install and update from the Store

A Store package can now deliver a compiled module alongside its content — one product, one card,
one install. Installing such a package lands the module next to the portal, and it starts working
at the next restart (the portal tells you a restart is pending).

From then on the module keeps itself current: whenever your instance checks its registry, a newer
build of an installed module is picked up automatically — under the same update policy that
already governs platform updates (Settings → Updates). Continuous, the default, updates modules
unattended; Stable or Manual leaves them untouched until you update from the catalog yourself. A
module built for a different platform version than yours is never installed — it simply waits
until your platform has rolled forward, then arrives on its own.
