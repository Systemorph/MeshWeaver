---
Name: In-mesh test cases no longer continue on the portal's own threads
Category: Fix
Description: A case in a NodeType's Tests area that waited for a value went on running on whichever portal thread produced it — a hub's own worker — and it never noticed when its time ran out, so it was reported as having ignored its deadline. Both are fixed, and a check now stops the shape coming back.
Icon: Bug
Order: -20260918
---

# In-mesh test cases no longer continue on the portal's own threads

Tests written for a NodeType run **inside the portal** — that is the point of them. A case asks the
mesh for something, waits, and checks what came back. The waiting is where this went wrong.

## What was happening

When a case waited for a value, everything it did **after** the value arrived carried on running on
the thread that produced it. Inside the portal that is not a spare thread: it is one of the workers
the portal uses to process messages. So a case would finish its wait and then do the rest of its
work — more reads, more waits, its own assertions — while standing on a worker the portal needed
back.

Where the case went on to wait for something *that same worker* had to deliver, nothing could
arrive. The case sat there until its time ran out and was reported as a timeout, with no hint that
the wait itself was what blocked the answer.

The same wait also never watched the clock. When a case's deadline passed, the portal cancelled it —
and the wait did not notice, so the run reported the case as having **ignored its cancellation**,
which reads like a fault in the test rather than in the helper it called.

## What changed

The wait now hands the value back through the platform's own waiting helper, which releases the
producing thread before the case continues. The case resumes on an ordinary worker, the portal gets
its thread back immediately, and nothing downstream is standing on the queue it is waiting for. The
wait also observes the case's deadline now, so a case that runs out of time ends **with** its
verdict instead of outliving it.

Nothing about what a case *sees* changed: the same value comes back, and a wait for something that
never arrives still fails the same way.

## Why it is worth a note

The shape that caused this — waiting on a stream directly — reads like the careful thing to do, and
four of our own written guides recommended it while four others warned against it. Those four are
corrected, and a check now runs on every build that refuses the shape in platform code and holds the
remaining places to a list that can only get shorter.

**Nothing to do.** If you have an in-mesh test that timed out for no reason you could see, or one
reported as ignoring its deadline, it is worth running again.
