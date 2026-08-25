---
Name: Rollouts no longer wait on a pod that never started
Category: Fix
Description: Fixes a rollout that could stall for tens of minutes when a server was deleted while still booting — the other servers now take over its startup compilation instead of waiting for it forever.
Icon: Sparkle
Order: -20260825
---

# Rollouts no longer wait on a pod that never started

When a server is deleted while it is still starting up, the cluster keeps a record of it that no
health check ever revisits — nothing is watching a member that never finished joining. Startup
compilation reads that record to decide whether the server currently responsible for the build is
still working, and it read "still starting" as "still running". So if the responsible server
disappeared mid-boot, every other server waited for a build that would never happen: a rollout could
sit for 25 minutes or more, with no error anywhere to explain it.

This is fixed. A member that never finished joining is now treated as "no information" rather than
"running", which hands the decision back to the liveness stamp the build already writes — so the
work is picked up by the next server as soon as that stamp goes stale, and the rollout completes.
A server that is genuinely mid-start and working keeps its build, exactly as before.

The same change also starts the liveness stamp earlier, at the beginning of the build rather than
after its planning phase, so a long build can no longer look abandoned while it is running.
