---
Name: Deleting a thread mid-answer no longer reports an error
Category: Fix
Description: A chat round interrupted by deleting its thread is now recorded as ended, not failed.
Icon: Sparkle
Order: -20260812
---

# Deleting a thread mid-answer no longer reports an error

Deleting a thread while an agent was still answering in it produced an error report, even though
nothing had actually gone wrong — the answer simply had nowhere left to go. The round is now
recognised as one that ended because its thread was being removed, and it settles quietly.

Nothing about a genuine failure changes. A round that really does fail is still surfaced exactly as
before, so this removes noise without removing a signal.
