---
Name: Stopping a thread that is waiting on a delegated agent now works
Category: Fix
Description: Stop — and a portal restart — now end a round that had handed work to another agent, instead of leaving it waiting on a sub-agent that will never answer.
Icon: Timer
Order: -20260819
---

# Stopping a thread that is waiting on a delegated agent now works

When an agent hands part of your request to another agent, it waits for that
sub-agent's answer before carrying on. Until now that wait listened to nothing:
pressing **Stop** while the delegation was in flight changed the thread's state
but did not end the round, and neither did a portal restart. The round sat there
holding one of the slots reserved for AI work, and let go only when the sub-agent
happened to finish — or, if it never did, after a ten-minute backstop.

Stop now ends the wait immediately. The thread settles as **Cancelled**, its AI
slot is released, and a restart that lands mid-delegation shuts the round down
with everything else rather than leaving it running in the background.

This completes the shutdown fix shipped earlier the same day: rounds already
stopped promptly when they were waiting on the model itself, but not when they
were waiting on a delegated agent.
