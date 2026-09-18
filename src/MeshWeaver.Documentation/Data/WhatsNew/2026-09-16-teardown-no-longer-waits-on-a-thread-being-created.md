---
Name: Teardown no longer waits on a thread being created
Category: Fix
Description: Draining or disposing an I/O pool started a brand-new OS thread each time, and nothing a pooled subscription feeds was terminated until that thread existed and was first scheduled — inside the one method whose contract is that it must never block. The thread is now started with the pool and parked, so teardown wakes it instead of minting it.
Icon: Timer
Order: -20260916
---

# Teardown no longer waits on a thread being created

Every long-lived subscription the mesh routes through an I/O pool — query change feeds, layout
renders, silo-side routing — is terminated at teardown by one cancellation, and that cancellation
runs each subscription's whole downstream clean-up *synchronously on whoever issues it*. So the pool
has never issued it on the caller: the thread calling `Drain()`/`Dispose()` is the mesh-teardown
thread, and running arbitrary application clean-up there once parked a whole test assembly silently
for eight minutes.

It issued the cancel on a **brand-new OS thread, created per call**. That put thread creation on the
critical path of every terminal: nothing is delivered until the thread exists *and* the OS has
scheduled it for the first time. `Thread.Start()` is itself a call that can block — it takes the
runtime's thread store lock and can queue behind a garbage collection — so `Dispose()`, whose own
contract is that it must return immediately, was making one. And the latency it added was invisible
to every in-process reading: disposal returns in 0 ms, no pool slot moves, the queue depth is zero,
and the subscription is simply not terminated yet.

**The canceller is now started with the pool and parked**, so teardown raises a signal instead of
minting a thread, and the thread exits once the cancel it exists for has run. Pools are created on
first use of a resource class, so the cost is one idle stack per class actually in use.

It surfaced as a merge-queue failure in which a pooled subscription's terminal was still absent five
seconds after disposal on an otherwise idle shard. Widening the window in place separates the two
latencies that were confused: making thread *creation* cost six seconds fails the old code and
passes the new one, while making the OS withhold a timeslice for six seconds fails both — that
second one reproduces the original failure exactly and is not something any design can remove, since
the cancel must not run on the caller. The measurement, and the reading of the CI log it corrects,
are in [Controlled I/O Pooling](/Doc/Architecture/ControlledIoPooling).
