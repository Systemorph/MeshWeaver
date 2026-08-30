---
Name: A retry test no longer reads a pause as a regression
Category: Fix
Description: The Orleans stream-attach retry test decided the attach had "settled" when its attempt counter stopped moving for 25 ms — on a loaded runner that is a scheduler hop, and the count it then read was exactly the regression signature it exists to detect.
Icon: Timer
Order: -20260830
---

# A retry test no longer reads a pause as a regression

`StreamAttachTransientRetryTest` proves that a transient grain-directory rejection is **retried**
rather than latching a hub into permanently disabled cross-process routing — the defect behind a
hub going silent on every rolling deploy.

It measured that by polling the attempt counter and calling the attach *settled* once two readings
25 ms apart were equal. But the retry hops through the thread-pool scheduler between attempts, and
on a loaded CI shard that hop takes longer than 25 ms. The poll then read the count **during a
pause**, declared it final, and reported `1`.

`1` is precisely the value the regression produces. So a saturated runner manufactured a red that
spelled out the regression's own signature — the worst possible false positive, because the correct
response to it is to go looking for a bug that is not there.

"Unchanged for 25 ms" was never the condition under test. The condition is *the attach stopped
attempting*, and the routing service already tracks exactly that: the task it stores as the outbound
gate completes when the attach has attached, given up, or been cancelled. The test now waits on that
task and reads the counter once, afterwards. No polling, no window, and the suite runs in
milliseconds instead of racing a timer.

## Its sibling, found by the merge queue

The same poll had been copied once. `PodHubClaimLifetimeTest` — which proves a process that cannot
host a grain stops claiming instead of spinning for ever — waited the same way, and it failed a
merge-queue entry with the same words: *expected 6, found 1*. A queue entry builds on a loaded
runner, which is exactly the condition that makes a pause look like a verdict.

It now waits on the claim's own terminal (`PodHubClaimSettled`), armed before the claim is
subscribed and completed when the claim lands or hits the one terminal that is impossibility rather
than a budget. A claim that is *still retrying* completes nothing, because it has not stopped —
that case keeps the positive "reached at least N attempts" wait it already had.

Both siblings ship together on purpose: each was ejecting the other from the merge queue, since an
entry is built on the entries ahead of it and neither fix was on the branch the other stood on.

