---
Name: A save waiting on a shutdown no longer waits for nobody
Category: Fix
Description: When a node is shutting down, its answer to an in-flight save is deliberately deferred to a later step. Nothing checked that anyone was still listening for it — and if the answer then arrived too late, it was dropped without a trace.
Icon: ClockAlarm
Order: -20260903
---

# A save waiting on a shutdown no longer waits for nobody

When a node is shutting down while one of your saves is still in flight, the answer is deliberately
held back and sent from a later step in the shutdown instead. There is a good reason: that later
route still reaches your session, and an answer sent from the earlier one might not.

But nothing checked whether your session was still listening. If it was not — because it had already
been answered, because the wait had run out, or because the save came from a different process
entirely — the answer was held back for a route that would deliver it to nobody, and your save was
left with silence until its own window ran out. **The hold-back now happens only when there is
somewhere for the answer to go.** Otherwise the node answers immediately, on whatever route is still
open.

There was a second, quieter problem behind it. An answer that arrives after the window closes is not
delivered — past that point it cannot be told apart from a stale repeat of an older one, and acting
on it would be worse than ignoring it. That part is unchanged and deliberate. What has changed is
that it used to be **completely silent**, and indistinguishable from an answer that was never
produced at all. Those are two different problems with two different causes, and the logs showed the
same nothing for both. A late answer is now recorded, with how late it was.

Nothing waits longer as a result — no window was widened. A save that could be answered is answered
sooner, and when one genuinely cannot be, the reason is now written down instead of inferred.
