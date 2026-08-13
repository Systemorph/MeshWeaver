---
Name: Shutdown waits for its own cleanup to finish
Category: Fix
Description: A restart no longer abandons still-closing hubs after five seconds.
Icon: Sparkle
Order: -20260812
---

# Shutdown waits for its own cleanup to finish

When a portal shut down or restarted, the step that closes a hub's child hubs gave up after a fixed
five seconds and reported a timeout — while those children were still closing normally. Everything
underneath then carried on against services that were already being torn down, which is how a
restart could leave connections and subscriptions half-closed.

Shutdown now waits for each child to report that it has actually finished, which each one is
guaranteed to do. Ordinary shutdowns are unaffected and just as fast; the difference only shows on a
busy one, which now completes cleanly instead of being cut short.
