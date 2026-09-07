---
Name: A rollout can no longer be left holding a build nobody will run
Category: Fix
Description: When several processes started at once, one of them could end up recorded as the owner of a build it had already finished with — and because that process was still running and healthy, nothing would ever take the build back off it. The next image then had no builder, no result, and no process that could report itself ready.
Icon: Wrench
Order: -20260907
---

# A rollout can no longer be left holding a build nobody will run

When a new version rolls out, every process that starts up asks the same question: *has this
version already been prepared, and if not, who prepares it?* One of them is handed the job, the
rest wait for its result and then stand down.

There was a narrow window in which standing down did not take. A process could finish waiting — its
result had arrived, it had nothing left to do — at the same moment it was being handed the job it
was just about to stop asking for. It said "I'm done"; the handover said "you're up"; and the two
statements crossed. The record was left naming it as the owner of a build it had already finished
with.

## Why that did not simply sort itself out

Ownership is taken back when the owner is **gone** — deliberately, and only then. A process that
holds the job but has stopped reporting progress is far more often busy or starved than dead, and
taking work away from a healthy process means two of them doing it at once, which is exactly what
having a single owner is meant to prevent.

Here, though, the recorded owner was neither gone nor working. It was alive, healthy and idle,
holding a job it had already finished. So the protection worked exactly as designed and defended
it — forever. Whoever asked next was queued behind an owner that would never move, and the next
version had no one preparing it: no result to wait for, and no process able to report itself
ready.

## What changes

A process standing down now **says so** as a plain statement of fact, rather than trying to work
out for itself whether it also needs to hand the job back. Working that out required knowing
whether it had been handed the job in the first place — and in this window it could not yet know.

Whoever hands out the job reads that statement, and it is the one participant that always sees the
current state, so it can see both halves at once. If the job was handed to someone who has since
stood down, it takes it straight back and gives it to whoever is next — in the same step, with
nothing waiting on a timer and nothing retrying in the background.

A process that is genuinely **in the middle of** preparing the version is never disturbed by this.
Only a job that was handed over and not yet started can be taken back, which is the same line that
was already drawn everywhere else this record is written.
