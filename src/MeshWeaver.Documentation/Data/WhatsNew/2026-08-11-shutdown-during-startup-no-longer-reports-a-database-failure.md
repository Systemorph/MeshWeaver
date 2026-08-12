---
Name: Shutdown during startup no longer reports a database failure
Category: Fix
Description: A portal asked to shut down while its startup database check was still running logged a critical "DB version check failed unexpectedly" for an ordinary shutdown. The interrupted check now stands down quietly.
Icon: Sparkle
Order: -20260811
---

# Shutdown during startup no longer reports a database failure

At startup the portal runs a gate that checks the database schema is fully migrated before it
accepts traffic. When the portal was asked to shut down while that check was still talking to
the database — for example a deployment rollout replacing a pod moments after it started — the
interrupted check was reported as a critical "DB version check failed unexpectedly. Refusing to
start the portal." Nothing had failed: the portal was simply told to stop, and the check was
called off with it.

An interrupted startup check now stands down quietly, the way the earlier fix already taught
the runtime health probe to treat its cancellations. Genuine startup problems — an unreachable
database, a missing schema, an incomplete migration — are still reported at critical level and
still stop the portal from starting, exactly as before.
