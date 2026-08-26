---
Name: Shutdown no longer parks on background I/O clean-up
Category: Fix
Description: A mesh shutdown could park indefinitely while tearing down background I/O subscriptions, silently stalling a restart or a whole test run.
Icon: Sparkle
Order: -20260826
---

# Shutdown no longer parks on background I/O clean-up

When a mesh shuts down — a portal instance rolling to a new version, or a test tearing down its
own isolated mesh — it cancels the background I/O it still has running and waits for it to stop.
Cancelling ran each background subscription's own clean-up inline on the very thread performing
the shutdown, one after another, and with no time limit over it: the limit that exists covers only
the step that follows. A single clean-up that could not finish therefore parked the whole shutdown
for as long as anyone was willing to wait, and wrote nothing anywhere to say where it had stopped.

Clean-up now runs on its own thread and is joined under the same budget as the rest of the drain,
so shutdown either finishes or reports exactly what is still running. Test teardown also records
each of its three shutdown phases, so if this part ever stalls again the log names it instead of
leaving an unexplained silence.
