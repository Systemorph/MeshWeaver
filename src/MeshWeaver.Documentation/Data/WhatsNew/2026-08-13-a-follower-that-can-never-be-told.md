---
Name: A rollout no longer waits on a message that cannot arrive
Category: Fix
Description: A portal that lets another process do the startup compile now works out for itself that the work finished — or takes it over when the other process stopped — instead of waiting for an announcement it could never receive.
Icon: ArrowSync
Order: -20260813
---

# A rollout no longer waits on a message that cannot arrive

When several processes start on a new version, one of them does the compile and the rest wait for
it to finish. Waiting was the problem: they waited to be *told*, and the announcement is delivered
only inside the process group that made it. A compile running in a separate group — the dedicated
build job, or a portal in another cluster sharing the same database — finished, announced, and the
waiting processes were never reached. Nothing ended the wait.

The same gap swallowed the other outcome too. A process that gives another one the job stays in the
queue for it, so when the builder **stopped** — crashed, evicted mid-rollout — the job was handed
straight to a process that had stopped listening. It held the job without doing it, and because it
was alive and healthy nothing would ever take it away again. The next version could then never
claim the build at all, and every portal on it stayed out of rotation waiting for a completion
signal that no longer had an owner.

Waiting has been replaced by looking. The completion signal is a durable record, so a follower now
**reads** it — the same record the build's own arbitration already decides on — instead of hoping to
be notified. And it keeps watching for the job: being handed the build is now a real event that ends
the wait. On being handed it, the follower re-reads the record — finished means stand down without
repeating a compile that already happened, not finished means the previous builder went away and
this one takes over. A follower that reaches its answer any other way now gives the job back rather
than sitting on it.

No timer and no deadline were added. A deadline would end the wait by guessing, and a process that
guesses "the compile finished" would report a version ready that it never checked.
