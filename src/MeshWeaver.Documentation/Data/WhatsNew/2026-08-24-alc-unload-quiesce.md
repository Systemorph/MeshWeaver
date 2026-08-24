---
Name: Fewer unexplained portal restarts
Category: Fix
Description: Retiring compiled code no longer risks taking the process down with it.
Icon: Sparkle
Order: -20260824
---

# Fewer unexplained portal restarts

When a NodeType is recompiled or a hub is retired, the platform reclaims the memory holding the old
compiled code. Until now it could do that while something was still running that code, which
occasionally killed the whole process outright — no error message and no failing test, just a
restart.

The platform now waits until nothing is using the old code before reclaiming it. If something is
still holding on, it keeps the memory rather than forcing the issue, and logs what it kept and why.
Holding a little memory is always better than dropping everything a portal was in the middle of.
