---
Name: Routine shutdown no longer reports itself as an error
Category: Fix
Description: Background watchers that stop because their node is shutting down now say so quietly instead of filling the error log and arming a retry against a node that is already gone.
Icon: Sparkle
Order: -20260809
---

# Routine shutdown no longer reports itself as an error

Every node in the portal runs small background watchers that keep it up to date — they notice when
its code needs rebuilding, when a rebuild has been requested, and when its source files change. When
the node itself shuts down, which happens routinely after a rebuild, after a period of inactivity,
or whenever a page briefly opens a node to read its shape, those watchers naturally stop with it.

They were treating that as a failure. Each shutdown produced a burst of red "faulted — re-establishing"
entries in the error log and armed a one-second retry against a node that no longer existed — steady
noise that made the error log harder to read and kept a shutting-down node in memory a little longer
than it needed to be.

A watcher now recognises its own node shutting down for what it is: the end of its job, not a fault.
It stops quietly, nothing is retried, and the replacement watcher starts with the node's next
activation. A watcher whose node is genuinely in trouble still reports it exactly as loudly as before.
