---
Name: Saving no longer hangs silently when a node is slow to load
Category: Fix
Description: A save whose node was still loading could wait forever with no error; it now fails after 30 seconds with a message that says what happened.
Icon: Sparkle
Order: -20260903
---

# Saving no longer hangs silently when a node is slow to load

Before a change is saved, the platform first reads the node's current state so it can work out
exactly what you edited. That read was meant to give up after 30 seconds and report a clear error.
In one case it never gave up at all: if the node kept reporting activity without ever handing over
its content, the 30-second clock was restarted each time and the save simply waited — with no error,
no progress and nothing in the log.

The wait is now measured against what it is actually waiting for, so such a save fails promptly and
says so instead of hanging. Anything that batches many saves together — installing or updating a
package, for instance — no longer stalls as a whole because one of them never finished.
