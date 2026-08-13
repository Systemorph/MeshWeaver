---
Name: Activity status no longer misses errors
Category: Fix
Description: An activity's final status is now rolled up from counters as it runs, so an error can no longer be swallowed by a silenced log entry.
Icon: CheckmarkCircle
Order: -20260813
---

# What's New — 13 August 2026

## Activity status no longer misses errors

An activity's final status — Succeeded, Warning, Failed — was worked out at the end by scanning its whole message list for the worst severity. That let a single entry logged at the "None" level, which means *no logging* rather than *worse than critical*, come out as the highest severity in the list and drop the entire activity into Succeeded, hiding real errors logged beside it.

Severity is now rolled up as each message is recorded, so the outcome is decided by a counter rather than by a scan, and a silenced entry can no longer outrank a genuine error. "Has errors" now also reports critical-level messages, matching the error list that has always included them.
