---
Name: Hub shutdown during initialization no longer reported as failure
Category: Fix
Description: Transient probe hubs disposed mid-initialization no longer log fail-level errors or leave FAILED-state residue — teardown during init is now a recognized shutdown outcome.
Icon: Sparkle
Order: -20260810
---

# Hub shutdown during initialization no longer reported as failure

Short-lived probe hubs (the ones the platform spins up to read a node type's data model and
dispose again) could race their own teardown: when a probe was disposed while still
initializing, its sub-hubs reported "initialization failed — Hub is now in FAILED state" as
errors, and the initialization watchdog fired a second error up to two minutes after the hub
was already gone. Both were routine teardown misreported as failures — noise in the error
logs with nothing to act on.

Initialization that ends because the hub (or its parent) is shutting down is now recognized
as a normal shutdown: no error logs, no failed-state residue, and the watchdog is disarmed
when the hub is disposed. A hub that genuinely cannot initialize still fails fast and
legibly, exactly as before.
