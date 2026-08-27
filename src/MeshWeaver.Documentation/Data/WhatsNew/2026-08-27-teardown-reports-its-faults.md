---
Name: Shutdown says what went wrong
Category: Fix
Description: A fault while a mesh shuts down is now reported instead of being silently discarded.
Icon: Sparkle
Order: -20260827
---

# Shutdown says what went wrong

When a mesh shuts down, the code that waits for it to finish used to convert the wait into a
one-shot handle that could only ever report one thing. Anything that went wrong after that
handle had already settled — a hub whose teardown failed a moment later, a background job that
faulted on its way to idle — reached nobody at all: no log line, no error, nothing to search for
afterwards.

Every one of those waits now stays subscribed for as long as the shutdown can still say
something, so a late failure is written to the log with the address it came from. The shutdown
report also carries two facts it previously threw away: whether the shutdown itself faulted, and
whether running activities actually finished before the mesh was torn down.

Nothing about a healthy shutdown changes. What changes is that an unhealthy one is now visible
instead of looking identical to a clean one.
