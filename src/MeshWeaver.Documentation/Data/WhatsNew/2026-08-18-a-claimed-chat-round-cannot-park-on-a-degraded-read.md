---
Name: A claimed chat round cannot park on a degraded read
Category: Fix
Description: The dispatcher that starts a chat round now reads the thread's content once instead of twice, so a round can no longer be claimed and then silently abandoned.
Icon: ShieldCheckmark
Order: -20260818
---

# A claimed chat round cannot park on a degraded read

Starting a chat round is a two-step handshake: the thread is first *claimed*, and a moment later the
claim is *committed* into a real round with a visible answer cell. The code that performs the second
step used to read the thread's stored content twice — once to decide the round was ready to start,
and again to actually start it. The two reads did not use the same rule, so in the case where the
stored content arrives in a degraded form the first read succeeded and the second one gave up.

When that happened the thread was left claimed but never started: the message stayed queued, no
answer cell appeared, and nothing recovered it until the thread was next opened from cold. To the
person who pressed send, the conversation simply sat there.

The dispatcher now reads the content once and carries that single result through, so the two halves
of the decision cannot disagree and the abandoning branch no longer exists.
