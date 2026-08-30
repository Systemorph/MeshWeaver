---
Name: Two test gates stop resuming their waiters inline
Category: Fix
Description: A pool test parked twenty workers on a signal that resumed every one of them on the releasing thread, and released it outside any finally — so an assertion that failed first left the workers waiting forever and the failure was reported as a hang instead of as the assertion.
Icon: PlugConnected
Order: -20260830
---

# Two test gates stop resuming their waiters inline

The test tree reached zero hand-woven concurrency gates. The ratchet that keeps it there matches the
primitives that park a thread **by name** — semaphores, reset events, `Monitor.Wait` — and cannot
match a `TaskCompletionSource`, because that type has a hundred legitimate uses and a name match
would cry wolf on all of them.

Two gates in the I/O-pool tests were the shape the rule is about, and the ratchet could not see them.

The first is the one that mattered. Twenty pooled workers parked on a single signal, and that signal
was created **without** `RunContinuationsAsynchronously` — so releasing it resumed *every* waiting
worker inline, on the releasing thread, which is precisely the inline-resumption defect the bridge
guard exists to prevent.

It was also released outside any `finally`. An assertion that failed *before* the release left all
twenty workers waiting on a signal that would never arrive, so the test host hung — and the real
failure, an ordinary assertion with a perfectly good message, was reported as a timeout instead.

Both now use the sanctioned bridge, which sets the flag itself, and both release in a `finally`.
Measured: with an assertion deliberately made to fail before the release, the test now reports it in
**22 milliseconds** instead of hanging.
