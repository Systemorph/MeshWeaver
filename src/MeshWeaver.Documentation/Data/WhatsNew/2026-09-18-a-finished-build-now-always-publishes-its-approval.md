---
Name: A finished build now always publishes its approval
Category: Fix
Description: When two builds finished one after another, the second one could report success while writing nothing at all — leaving the new version approved by nobody, and every server waiting to serve it stuck.
Icon: Bug
Order: -20260918
---

# A finished build now always publishes its approval

Before a server will serve a new version of the platform, something has to **build** the pieces that
version needs and then record that the build finished cleanly. That record is the approval every
server waits for: until it exists for the exact version a server is running, that server refuses to
take traffic.

Only one machine builds at a time. It asks for the job, is handed it, does the work, and writes
"done". The next machine in the queue is then handed the job and does the same.

**The "done" could go missing, and everything reported success anyway.**

The machine writing "done" does not own the record — it sends its change to whoever does. Before
sending, it checked one thing for itself: *am I still the one holding this job?* It asked that of its
own local copy of the record, and a local copy can be a moment behind. When the answer came back
"no", the machine quietly changed nothing and sent nothing — and the call reported success. Nothing
failed, nothing was logged, and the only sign was somewhere else entirely: servers that never became
ready, waiting for an approval that was never going to arrive.

The copy it read was usually not merely stale — it was **the previous build's own "done"**. Finishing
a build releases the job, so the state left behind says *nobody is holding this*. That is exactly the
answer that makes the next machine's check fail, which is why it was always the **second** build in a
row that vanished and never the first.

**Now the machine simply states what happened, and the owner decides what it means.** "My build
finished, here is its approval" is written unconditionally, under the reporting machine's own name,
so it always travels. The owner of the record — which is never out of date about itself — reads that,
checks whether the reporter really is the machine holding the job, and publishes the approval,
releases the job and hands it to the next machine in one step.

Nothing is lost in the move. A machine whose job was taken away from it mid-build still cannot
publish an approval or disturb its replacement's work; that check now simply happens where the
answer is reliable. Failures are reported the same way and still block the version from being
served, which is the safe direction. The same treatment covers the smaller per-chunk sign-offs a
build produces along the way.
