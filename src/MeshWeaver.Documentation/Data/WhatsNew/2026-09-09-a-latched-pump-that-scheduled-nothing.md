---
Name: A latched pump that scheduled nothing
Category: Fix
Description: A hub's message pump marks itself busy before handing a turn to its task scheduler. If that hand-off threw — a torn-down Orleans activation, a completed scheduler pair — the busy flag stayed set with nothing running, and the hub silently stopped processing messages for the rest of its life while its own stall report blamed a scheduler that had never been asked. The flag is now released, and the stall report measures which of the three mechanisms it is instead of asserting one.
Icon: Wrench
Order: -20260909
---

Each hub processes its messages one at a time. A flag says "a turn is running or queued"; while it
is set, an arriving message just joins the queue instead of starting a second turn. The flag is set
first, and the turn is then handed to the hub's task scheduler.

If that hand-off **threw**, the flag stayed set. Nothing was running, nothing was queued, and every
later arrival looked at the flag, concluded someone else was already draining, and returned. The hub
went dark — permanently, and without a word.

A real scheduler does throw here. Under Orleans a hub's turns run on its grain's activation
scheduler, and an activation torn down underneath the hub stops accepting work; a completed
scheduler pair does the same. This is the shape of a shutdown, which is exactly when it hurts most:
the message the pump then never processes is the hub's own shutdown request.

## What it does now

A failed hand-off releases the flag and logs an error naming the scheduler and how many turns are
waiting. The next message tries again and reports again, instead of the hub falling silent.

🚨 It does **not** re-route the turn to another scheduler. That scheduler is the hub's ordering
guarantee — under Orleans it is what keeps a grain's work on the grain — so borrowing a different
thread to keep a queue moving would trade a stalled hub for a corrupted one. Releasing the flag is
the honest recovery.

## And the stall report stopped guessing

When a hub is stuck at shutdown the platform prints a verdict. For this state it used to say *"the
drain flag is latched (a drain **is** scheduled on this hub's TaskScheduler)"* and send the reader
to that scheduler — a sentence nothing measured. Two quite different situations read identically:
the scheduler accepted a turn and is not running it, or nothing was ever handed over at all. The
first is the scheduler's problem; the second is the platform's.

The report now counts both — turns handed over, and turns actually begun — and names the mechanism
from the difference:

- a turn is running but blocked before it takes work off the queue;
- the scheduler **accepted** turns and has not run them (on Orleans: a wedged or deactivated
  activation);
- nothing is outstanding at all while the flag is set — which is the bug above, and is now
  unreachable.

Forty-seven of these reports were filed in one shutdown, all pointing at the wrong place. See
[Reading a Disposal Stall Verdict](@/Doc/Architecture/DisposalStallVerdicts).
