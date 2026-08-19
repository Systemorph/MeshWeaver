---
Name: A finished script run stays finished
Category: Fix
Description: A code cell that had already completed could flip back to a spinner and lose its end time; the run's own log writes now advance the node's revision, so a late save-echo can no longer overwrite the result.
Icon: CheckmarkCircle
Order: -20260817
---

# A finished script run stays finished

Running a code cell occasionally left the output pane spinning on work that had already
finished, with the run's end time blanked out. Nothing was actually still running — the
finished result had simply been overwritten by an older snapshot of the same run arriving
late from the background save.

The run's own progress writes were not advancing the activity's revision number, which is
what tells the portal which of two snapshots is newer. Every save-echo therefore looked
newer than the live result and was allowed to replace it. The writes now go through the
standard node-update path, so each one is a real revision and a late echo of an earlier
one is recognised and ignored.

The same write path was also rebuilding the activity record from scratch on every update,
which quietly dropped the run's start time, the link back to the code that produced it, and
a pending cancel request. Those now survive, so a finished run keeps its Re-run button and
its true start time.
