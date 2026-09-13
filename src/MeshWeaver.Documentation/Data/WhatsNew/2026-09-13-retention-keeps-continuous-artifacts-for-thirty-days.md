---
Name: Retention keeps continuous artifacts for thirty days, and re-enabling cleanup is interlocked
Category: Fix
Description: The registry's recorded purge now states the decided policy — at least 30 days by age with no build-count quota — a gate holds every recorded step to it on every pull request, and turning the paused cleanup back on refuses while the pause that stands in front of it is still in force.
Icon: ShieldLock
Order: -20260913
---

# Retention keeps continuous artifacts for thirty days, and re-enabling cleanup is interlocked

The retention policy this platform decided is one sentence: **retain unreferenced continuous
artifacts for at least 30 days by age, without a build-count quota.** Two of the three stores that
implement it were clamped to it in code — the prebuilt-bundle store and the assembly cache, both of
which raise a shorter configured window up to 30 days rather than trusting it. The third store is
the container registry, and it was left out in as many words.

So the recorded purge kept a **seven-day window with a ten-build quota** over five repositories that
are republished many times a day, one of which is the image both production portals run. Nothing
compared that to the rule the other two stores obey.

## What changed

**The record states the decided policy, and a gate holds it there.** Every recorded purge step must
retain for at least 30 days by age and must carry no `--keep` build-count quota. The check needs no
credential and runs on every pull request, over **every** recorded step rather than only the enabled
ones — the record is what gets pushed to the registry when cleanup is re-enabled, so a disabled task
carrying a short window is a short window one command away from running. A duration the check cannot
parse is a failure, not a default: a window nobody could read is a window nobody checked.

**The quota is removed rather than raised, and that is the point.** `--keep N` counts *newer builds*,
so the more often a repository is republished the faster its older images become eligible for
deletion — which is exactly how a stable, deliberately long-lived reference gets collected. A
generous age window beside a build-count quota still deletes something ten builds old on the day it
is written. Both halves, or neither is the policy.

**Turning cleanup back on is interlocked.** Cleanup is currently paused, on purpose, while artifact
protection is being completed — and the record carries the condition that would end the pause. The
one command that can re-enable the schedule now **refuses** while that pause stands, naming the
condition, and refuses before its confirmation prompt so the prompt cannot be mistaken for the gate.
Lifting the pause is a reviewed change to the record that says what satisfied the condition, not a
prompt somebody answered. The record cannot quietly contradict itself either: declaring a pause
while also recording a running cleanup task is a failure, and so is every task being stopped with
nothing explaining why — a retention that halted and a record that went stale otherwise read exactly
the same.

**A deliberate gap between the record and the registry is declared rather than remembered.** The
registry has not been changed; the record is ahead of it, and it says so. The verification command
prints that gap instead of reporting it as an incident — a drift report that is always red is one
nobody reads — and fails once the gap is closed, so the declaration expires by being checked. The
command that re-captures the record from the registry now refuses without an explicit confirmation,
because re-capturing would silently restore the short window.

## What this does not change

Nothing was deleted, no cleanup was run, and the registry was not modified. Cleanup remains paused.
The nightly protection run and the nightly deletion are still two independent schedules, so a given
night's deletion is still not downstream of that night's protection result; that remains open, and
it needs a registry-side mechanism rather than a wider window.
