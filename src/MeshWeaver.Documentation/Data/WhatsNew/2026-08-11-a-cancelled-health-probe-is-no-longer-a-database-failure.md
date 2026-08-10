---
Name: A cancelled health probe is no longer a database failure
Category: Fix
Description: When a routine health probe was cancelled mid-query (probe deadline, shutdown), the portal reported it as a database failure at error level and auto-filed an incident. Cancellations are now handled as the routine events they are.
Icon: Sparkle
Order: -20260811
---

# A cancelled health probe is no longer a database failure

The portal answers regular health probes so the platform can tell a healthy instance from a
broken one. One of these probes checks that the database schema is fully migrated. When such a
probe was cancelled while its query was still running — because the probe's deadline expired,
the caller disconnected, or the portal was shutting down — the check reported "db_version check
threw" as an error-level database failure, and the incident watcher dutifully filed a
production issue for it. Nothing was wrong with the database; the probe had simply been called
off.

A cancelled probe is now handed back to the health-check framework, which knows the difference:
a probe the caller abandoned ends quietly, and a check that genuinely ran out of time reports a
clear "timeout" reason instead of an opaque failure. Real database problems — unreachable
server, missing schema, incomplete migration — are still reported exactly as before. The same
correction applies at startup: a portal that is asked to shut down while its database gate is
still checking no longer logs a critical "DB version check failed unexpectedly" on its way out.
