---
Name: Re-running a failed test shard can now clear the run
Category: Fix
Description: When a single test shard failed and was re-run green, the check that consolidates every shard's results kept reading the failing attempt's results and re-declared the same failure — on that re-run and on every one after it. The only way out was to push a new commit. Each shard's results now carry the attempt that produced them, and the consolidation reads each shard's newest attempt.
Icon: ArrowSync
Order: -20260914
---

# Re-running a failed test shard can now clear the run

The test suite runs in six parallel shards, and one check at the end consolidates all six into the
single verdict that decides whether a change can be merged. When one shard failed — for a reason
that often had nothing to do with the change, an infrastructure hiccup or a test failing on a diff
that cannot reach it — the ordinary remedy was to re-run that shard.

**The re-run worked and the verdict did not change.** The shard came back green, 1454 tests of 1454,
and the consolidation announced the same failing test it had announced before. Re-running the
consolidation itself produced the same answer again. Nothing short of pushing a new commit could
clear that run.

The cause was a matter of which results were being read. A re-run does not replace the previous
attempt's results — it stores them alongside. Both attempts' results sat under the same name, and
the step that fetched them picked by an internal identifier that does not increase with time; on
that run the *older* results held the higher identifier and won. The consolidation was reading the
attempt that had just been superseded, and it did so consistently, so no number of re-runs could
ever get past it.

**Each shard's results now carry the attempt that produced them**, and the consolidation keeps, per
shard, the results of the newest attempt — and prints which attempt it kept for each. A partial
re-run is handled the way it actually happens: the five shards that were not re-run keep their
original results, and only the one that re-ran is updated. The older results are still stored, so a
failure can still be examined after the re-run that cleared it.
