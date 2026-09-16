---
Name: A subscription that arrives as the pool dies is refused, not torn down
Category: Fix
Description: A subscribe that reached an I/O pool just after teardown cancelled it ran that subscription's whole downstream clean-up on the thread that subscribed, which can be a hub turn. The clean-up registration is now in place before there is anything to clean up, so a subscribe arriving that late is simply refused and nothing is opened for it.
Icon: ArrowSync
Order: -20260916
---

# A subscription that arrives as the pool dies is refused, not torn down

Every long-lived subscription the mesh routes through an I/O pool is terminated at teardown by one
cancellation, and that cancellation runs each subscription's clean-up *synchronously on whoever
issues it*. The pool has never issued it on the caller — a dedicated thread does — because that
clean-up is arbitrary application code, and a hub turn or a grain turn running arbitrary code is
unbounded by construction.

There was a second way onto that thread, and it was not the cancel: **registering a clean-up callback
on a token that is already cancelled runs the callback immediately, on the registering thread**. A
subscribe that got as far as registering just after teardown had cancelled therefore ran its own
subscription's clean-up on the thread that subscribed. Disposal is the reachable case, because
disposal gives in-flight work no grace period — it cancels and returns.

**The registration is now established before the subscription it would tear down exists.** A
callback that fires during registration can then only find a subscription that has not started, so
what runs on the subscriber's thread is a *refusal* — the same answer the pool already gives anyone
who subscribes to it after teardown — and never a live pipeline's clean-up. A subscribe that lands
that late now also opens nothing at all: no provider, no hub, for a subscription that was over
before it began. Clean-up for a subscription that really did start is still run by the pool's own
thread, because the registration that runs it was in place before the subscription was.

Ordering is the entire fix: checking whether the token is cancelled before registering would only
narrow the window, since the cancel can land between the check and the registration. The
measurement — widening that window in place until the defect is deterministic, and the counter that
pins the order with no widening at all — is in
[Controlled I/O Pooling](/Doc/Architecture/ControlledIoPooling).
