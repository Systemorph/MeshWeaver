---
Name: A save the owner already judged no longer times out
Category: Fix
Description: Three ways a save could wait out its full half-minute and then report the owner unreachable — when the owner had accepted it, was still holding it, or had already decided. Each one now answers.
Icon: ClipboardCheckmark
Order: -20260903
---

# A save the owner already judged no longer times out

When you save something, the node that owns it decides what happened and tells your session. That
answer is what turns the spinner into a saved state, releases the next queued edit, and lets a retry
know whether it is safe. If it never arrives, your session waits out its full confirmation window —
about half a minute — and then reports that the owner could not be reached.

Three separate paths could produce exactly that, and in each of them **the owner had not gone
anywhere**.

**It was still waiting for its own data.** One of the two save paths began by reading the node's
current state and had no time limit on that read at all. If the source never produced anything —
which can happen to an activation that has not finished loading — the save sat there indefinitely.
It now gives up after ten seconds and says so, and because nothing was written in that time the
answer is the retryable "the owner has not loaded yet" rather than "unreachable".

**It was waiting for an activation that had already gone.** When a save arrives at a node that is
still warming up, it waits for the load to finish and then applies. That wait was bounded, but if
the underlying source *ended* instead of producing data, the bound was cancelled along with it and
that branch fell silent. It now answers "the owner's store ended before it finished loading — the
save was not applied, safe to retry".

**It had already decided, and the answer was dropped.** During a shutdown, the message carrying the
verdict can be refused. Nothing checked whether it had been accepted, and the act of sending it
closed off the one remaining route that still works at that point. The verdict now falls back to
that route when the message is refused, and if neither route can reach anyone, that is written to
the log rather than passed over in silence.

None of these changes what a save does or how long a healthy one takes. They change what happens
when something goes wrong: an answer, promptly, instead of half a minute of silence followed by a
misleading one.
