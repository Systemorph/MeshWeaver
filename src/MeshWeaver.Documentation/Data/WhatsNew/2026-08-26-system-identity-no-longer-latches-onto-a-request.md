---
Name: System identity no longer stays behind on the thread that started a background read
Category: Fix
Description: Nineteen places where the platform reads or writes as the system account left that account attached to whatever was running afterwards — including an anonymous page request.
Icon: ShieldTask
Order: -20260826
---

# System identity no longer stays behind on the thread that started a background read

Some of the platform's own housekeeping — checking for updates, warming up compiled types, seeding
the privacy statement, running a sync or indexing job — has to read and write as the system account
rather than as you. That is intended. What was not intended is that the system account could stay
attached to whatever was running next on the same thread, because the switch was made in one place
and undone somewhere else entirely. The most visible case: loading the public privacy page, which
anybody can request without signing in.

Nothing was reported wrong, and that is the point — the effect is silent, and the only trace it
leaves is work that should have been checked against your permissions being checked against the
system account's instead. All nineteen places now switch identity and switch back within the same
step, so the system account cannot outlive the job it was opened for.
