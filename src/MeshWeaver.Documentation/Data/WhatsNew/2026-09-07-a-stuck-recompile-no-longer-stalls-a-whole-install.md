---
Name: A stuck recompile no longer stalls the whole installation
Category: Fix
Description: Installing an app or a plugin asks each of the code types it ships to rebuild. If one of those requests never came back, the installation simply stopped — for ten minutes, saying nothing, and then failed. It now finishes, and names the one type that did not answer.
Icon: Checkmark
Order: -20260907
---

# A stuck recompile no longer stalls the whole installation

Installing an app or a plugin does two things: it writes the content, and then it asks each of the
code types that content ships to rebuild itself. Those requests go out together and the install waits
for all of them before it finishes.

If **one** of them never came back — not refused, not failed, just never answered — the install
waited for it forever. Everything else had already succeeded: the content was written, the other
types had rebuilt, there was nothing left to do but finish. Instead the whole installation sat there
for ten minutes, produced no message of any kind, and then failed with a timeout that named the
package and nothing else.

## What changes

**A request that never answers is now an answer.** Each rebuild request has its own deadline, well
beyond the longest a real one can take. If it passes, that one type is reported as *not rebuilt*, the
installation completes, and everything that did succeed stays succeeded.

**The message names the type.** Before, the only thing that ever surfaced was "this package timed
out" — which is true of the package and useless about the cause. Now the report says which type did
not answer, so the next step is obvious instead of being an investigation.

**Nothing was made faster or more patient.** No existing deadline moved, nothing is retried, and a
rebuild that is genuinely slow still gets all the time it always had. The new deadline sits far above
the slowest legitimate rebuild and far below the installation's own — it can only fire for a request
that was never going to answer at all.

## What it does not do

**A type that did not rebuild has not rebuilt.** It keeps the code it was already running, which is
exactly the state it would have been in had the install failed — the difference is that the rest of
the install now stands, and you are told which type to look at. Asking it to rebuild again (the
Compile action on the type) is the remedy.

**It does not explain why a request stopped answering.** That stays a defect to find. What changed is
that finding it no longer costs a ten-minute stall and a full log read.
