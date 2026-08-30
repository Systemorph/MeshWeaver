---
Name: A red shard with two failing tests is no longer reported as a host crash
Category: Fix
Description: CI's harness assumed xUnit v3 exits with the number of failing tests; a shard that completed with two failures was reported as a host crash, hiding the real failures.
Order: -20260830
Icon: Bug
---

# A red shard with two failing tests is no longer reported as a host crash

CI's test harness assumed xUnit v3 exits with the number of failing tests, so a shard whose host
completed normally with two recorded failures (exit code 1) was classified as "host crashed after
streaming results" and a synthetic `HOST_CRASHED` failure was written into the results — hiding the
two real failures behind a crash that never happened. xUnit v3 exits 1 for any number of failures.
The rule now reads: failures recorded and an exit code below the signal range means the host
completed and the run is a plain test failure; a crash is a signal exit, a missing results file, or
a non-zero exit with nothing recorded.
