---
Name: A quiet log now says whether the process was running
Category: Feature
Description: A portal writes one short liveness line at a fixed cadence, on a thread of its own — so a window where nothing was logged can finally be told apart from a window where nothing was running.
Icon: Checkmark
Order: -20260913
---

# A quiet log now says whether the process was running

When a page hangs, the first thing anyone reads is the portal's log around the moment it stopped
responding. Often the log simply **stops** — no error, no warning, nothing at all until the page is
given up on.

That silence has always been read as *"one thing got stuck and everything else carried on"*. It is
one of four possibilities, and the other three have completely different owners: the whole process
may have been paused by the runtime, the worker pool may have been full with nothing finishing, or
the thing doing the reporting may itself have died. Until now all four produced exactly the same
thing in the log — nothing — so the reading was a guess, and a recent investigation spent a session
on the wrong one of the four.

## What changes

**A portal now writes one short line at a fixed cadence, whatever else is happening.** It carries the
time since the previous line, how much of that time the runtime spent paused, how much memory is in
use, and how much work the worker pool has queued and finished. Ten seconds apart by default.

**The reading is the line's PRESENCE, not its contents.** If the line keeps appearing through a
window where nothing else was logged, the process was running and one operation was stuck — a real
hang, and the existing diagnostics name which one. If the line stops together with everything else,
the process itself was stopped, and no amount of investigating the stuck page will find anything.

**It reports its own gaps.** When the line comes back late, it says so, and says how much of the delay
the runtime accounts for — so a pause the process recovered from explains itself where it happened,
instead of being reconstructed afterwards from other tools.

**It runs on a thread of its own, on purpose.** A reporter sharing the worker pool goes quiet exactly
when the pool is the problem, which is one of the four cases it exists to tell apart.

## What this does not change

**Nothing reacts to it.** It does not restart, retry, resubscribe or recover anything, and no decision
anywhere reads it. It is a measurement, and measuring is all it does.

**It can be switched off.** Setting `Diagnostics:LivenessHeartbeatSeconds` to `0` stops it — and the
log says it was switched off, so an installation that publishes nothing is still distinguishable from
one that was never asked to.

**The cost is one line every ten seconds.** Roughly a megabyte and a half per portal per day, which
buys the difference between "the process was stopped" and "one page was stuck" in every log anybody
already keeps.
