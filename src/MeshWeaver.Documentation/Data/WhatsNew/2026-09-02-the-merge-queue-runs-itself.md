---
Name: The merge queue runs itself
Category: Feature
Description: Changes to the platform now land through a merge queue that a steward looks after — a flaky test or a stuck build no longer needs a person to notice and put the change back in line.
Icon: TaskListSquareLtr
Order: -20260902
---

# The merge queue runs itself

Every change to the platform is tested before it lands. Until now, two changes that were each fine
on their own could still break things when they landed together, because nothing had ever built
the combination. A **merge queue** fixes that: changes wait in line and each one is built on top of
the ones ahead of it, so what lands is exactly what was tested.

The queue's first outing, at the end of August, needed constant attention. A test that fails only
occasionally would knock a change out of the line, and someone had to notice and put it back. And
with several changes being built at once, every arrival or departure restarted all the builds — for
over an hour nothing landed and nothing failed either.

Both are addressed. The queue now builds one change at a time, so there is nothing to restart. And
a **steward** watches every change that leaves the queue without landing: if the build timed out, or
failed only on a test that is already known to be unreliable, or died on a machine problem before it
could reach a verdict, the steward puts the change back in line by itself — at most twice, and it
says what it did on the change. If the failure is real, it leaves the change out, names the failing
test and the build, and marks it so a person can pick it up.

For anyone contributing: mark a change ready and it lands when it is green. The queue and its
steward are documented under Architecture → The Merge Queue.
