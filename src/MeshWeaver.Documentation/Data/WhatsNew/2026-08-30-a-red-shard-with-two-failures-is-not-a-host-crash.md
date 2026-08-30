---
title: A red shard with two failing tests is no longer reported as a host crash
category: Fix
icon: Bug
date: 2026-08-30
---

CI's test harness assumed xunit v3 exits with the number of failing tests, so a shard whose host
completed normally with two recorded failures (exit code 1) was classified as "host crashed after
streaming results" and a synthetic `HOST_CRASHED` failure was written into the results — hiding the
two real failures behind a crash that never happened. xunit v3 exits 1 for any number of failures.
The rule now reads: failures recorded and an exit code below the signal range means the host
completed and the run is a plain test failure; a crash is a signal exit, a missing results file, or
a non-zero exit with nothing recorded.
