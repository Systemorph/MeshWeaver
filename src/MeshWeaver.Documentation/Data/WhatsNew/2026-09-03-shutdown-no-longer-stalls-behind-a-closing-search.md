---
Name: Shutting down no longer stalls for 30 seconds behind a closing background search
Category: Fix
Description: When the portal shut down at the very moment a background search was finishing, shutdown could wait out a 30-second budget and then report work left running; the two can no longer block each other.
Icon: Sparkle
Order: -20260903
---

# Shutting down no longer stalls for 30 seconds behind a closing background search

Code completion keeps a small background search running to learn which names your code actually
uses, so suggestions can be ranked by likelihood instead of alphabetically. If the portal began
shutting down at the very moment that search was wrapping up, the two could end up waiting on each
other: shutdown waited its full 30-second budget for the search to stop, then reported that work had
been left running — and the search itself was never told that its owner had gone away. Both halves
are fixed: a closing search can no longer hold up shutdown, and the search is stopped with the
component that started it, so shutting down is once again as quick as it should be.
