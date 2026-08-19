---
Name: Agent rounds stop cleanly when the portal shuts down
Category: Fix
Description: A chat round that was mid-answer during a restart no longer keeps a worker busy in the background.
Icon: Sparkle
Order: -20260819
---

# Agent rounds stop cleanly when the portal shuts down

When the portal restarted while an agent was still writing an answer, that round
could keep running in the background instead of stopping with everything else. It
held on to one of the slots reserved for AI work, and only gave up much later.

Rounds now stop as soon as shutdown begins, so a restart releases its AI capacity
immediately and the next start-up begins with a clean slate.
