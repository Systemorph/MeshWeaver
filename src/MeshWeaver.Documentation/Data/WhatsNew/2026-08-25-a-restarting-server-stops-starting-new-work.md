---
Name: A restarting server stops starting new work on its way out
Category: Fix
Description: A server that had begun shutting down could still spin up brand-new internal workers — including one created purely to be told that something was going away. Those workers outlived the shutdown that made them, which is what made some restarts drag or stall.
Icon: ArrowSync
Order: -20260825
---

# A restarting server stops starting new work on its way out

The platform restarts routinely — a new version ships, a server is moved, capacity changes. A
restart is supposed to be quiet: finish what is in hand, hand everything else to a healthy server,
and go.

One part of that was working against itself. As a server wound down it kept announcing its departure
to the internal components that tracked it — and when one of those had already gone, the
announcement **created a fresh one just so it could be told the news**. The new component then
belonged to the server that was leaving, so it had to be shut down too, which produced another
announcement. Work created by shutting down, in the middle of shutting down.

Nothing was wrong with the announcements themselves; the mistake was making them at all once the
decision to stop had been taken. The platform knows it is going away a little before its clustering
layer does, and in that gap every request for a new component was still being honoured — faithfully,
on the server that was about to disappear.

Now a server that has begun stopping asks for nothing new. Anything already in flight still
completes and is still delivered, and messages that arrive during the window are told the target is
coming back rather than that it failed — so whatever sent them waits and retries instead of giving
up. A healthy server is completely unaffected and still starts components exactly as before.

Both halves are pinned by tests, in both directions, plus a guard that fails the build if a new
place is added that could ask for a component without passing the check.
