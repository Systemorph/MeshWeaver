---
Name: Queued work is no longer cancelled while it is still finishing
Category: Fix
Description: At shutdown the I/O pool gives in-flight work a grace period to finish, restarted by every completion. Work that reached the pool while that grace was running cancelled the reading out one for one, so the grace quietly became a single budget for the whole queue and the last items were killed seconds from the end of work they were going to complete. The drain now counts completions instead of inferring them.
Icon: Timer
Order: -20260917
---

# Queued work is no longer cancelled while it is still finishing

When the mesh shuts down — or unloads a node's compiled assemblies — every piece of offloaded I/O
still in flight is given a **grace period to finish on its own** before anything is cancelled. The
grace is a stall bound rather than a budget: each completion restarts it, so a burst of ten short
writes drains in ten completions, and a write that would have landed in 50 ms lands.

That promise held only while the pool's admission count moved in one direction, and it does not. The
drain took a baseline of how much work was outstanding and waited for that number to fall below it —
but the number **rises on an arrival exactly as far as it falls on a completion**, and arrivals
during a drain are routine rather than exotic, because the pool hands every operation's start-up to
the thread pool. So a task whose caller had already queued it joined the count after the baseline was
taken, and cancelled out a completion. The reading stopped moving while the pool was finishing one
item after another, and the per-completion grace silently became one total budget for the whole
queue. Whatever was still running when it expired was cancelled — work the pool had accepted, was
making progress on, and had no reason to stop.

In the measured case the pool completed four items in the window and the drain called it a stall,
killing the last one 400 ms from the end of an 800 ms operation.

**The drain now counts completions instead of inferring them from the outstanding total.** Two
questions, two counters: whether anything is still outstanding decides whether to keep waiting, and
whether anything has actually finished decides whether the grace restarts. Work that reaches the pool
mid-drain now extends the drain instead of consuming the grace of the item that finished before it.

Nothing about the cancel changed. Work that outlives a whole grace with nothing finishing is still
stopped, and the teardown still names it — patience for accepted work was never patience for work
that does not end.

It was found as a one-in-five failure of the pool's own grace test on a heavily loaded machine. Load
was the condition and not the cause, so the fix is not a longer grace or a wider margin — those buy
slack against load and leave the defect in place. The reproduction runs on an idle machine and
arranges the ordering deliberately, which is what made the mechanism provable rather than suspected.
The mechanism, the reproduction and the one window this does *not* close are written up in
[Controlled I/O Pooling](/Doc/Architecture/ControlledIoPooling).
