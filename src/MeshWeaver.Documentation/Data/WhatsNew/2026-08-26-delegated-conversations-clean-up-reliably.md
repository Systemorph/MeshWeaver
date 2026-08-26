---
Name: Delegated conversations clean up reliably
Category: Fix
Description: A rare timing case could leave a delegated sub-conversation's cleanup watch running indefinitely instead of releasing once the sub-conversation finished.
Icon: Sparkle
Order: -20260826
---

# Delegated conversations clean up reliably

When one agent asks another agent for help, the mesh sets up a small watch that waits for that
delegated sub-conversation to finish so it can release its resources. In one narrow timing case,
the watch could fail to recognise that the sub-conversation was actually done, leaving it running
indefinitely instead of releasing it. Left unnoticed for long enough this could accumulate and
slow the whole system down.

The watch now recognises a finished sub-conversation correctly every time, so delegated
conversations always release their resources once they are done.
