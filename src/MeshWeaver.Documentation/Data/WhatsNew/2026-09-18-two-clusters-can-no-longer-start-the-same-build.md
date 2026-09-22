---
Name: Two clusters can no longer start the same build at once
Category: Fix
Description: The brief pause that lets every arbiter see the same set of would-be builders before one is picked was being skipped on every deployment that has a database — that is, on every deployment where a second builder can actually exist.
Icon: Bug
Order: -20260918
---

# Two clusters can no longer start the same build at once

When a new version of the platform rolls out, several processes wake up at almost the same moment
and each offers to do the work of compiling the new content. Exactly one of them should be picked.
Picking is not a race — every process looks at the same written-down list of volunteers and works
out the same winner — but that only holds if they are all looking at a list that has stopped
changing. So a first-time pick waits a few seconds before deciding, which is long enough for a
volunteer that registered somewhere else to appear on everyone's list.

That pause was not happening. It was being applied by asking "is this the main build?" of the wrong
record: the answer is written on a small separate entry that exists precisely so that nothing else
can overwrite it, and the question was being put to the main record instead. The two never match,
so the answer was always "no", and the pause was skipped — on every deployment that stores its data
in a database, which is every deployment where a second process is around to disagree in the first
place. The one place the pause did still happen was a single-process development machine, where
there is nobody to wait for.

The visible consequence was the one this was built to prevent: on a rollout, two processes could
each conclude they had won and each run the whole content build, doing several minutes of duplicate
work and writing the same results twice.

The question is now asked once, in one place, and recognises the build by either of the two records
that can carry its decision — so the pause happens where it was always meant to. It costs a few
seconds once per build, against a build measured in minutes, and nothing else changed: a build
being taken over from a process that has gone away is still handed on immediately, and the per-part
claims inside a build still never wait.
