---
Name: Shutting down no longer logs a warning for every view that had not yet drawn
Category: Fix
Description: When a host shut down, every view it was serving that had not yet drawn reported a warning — one per view, on every shutdown. Shutting down is routine, so those are now recorded quietly, while a view abandoned for any other reason still warns.
Icon: Sparkle
Order: -20260831
---

# Shutting down no longer logs a warning for every view that had not yet drawn

A view that goes away before it ever draws anything is worth noticing: somebody was shown a spinner
and then nothing. So the platform records a warning naming the view, which is what makes that
situation findable instead of appearing as an unexplained delay.

But it recorded the same warning when the **host itself was shutting down** — and that is not the same
event. When a host goes away, every view it serves is torn down at once, and any that had not yet
drawn necessarily reports. A single ordinary shutdown therefore produced a burst of warnings, one per
view, none of which indicated anything wrong.

Warnings are meant to be read. A recurring burst that never means anything trains people to skip
them, which costs far more than the noise itself — the next real one is skipped too.

A view torn down because its host is shutting down is now recorded quietly, as routine lifecycle. A
view abandoned for any other reason still warns, still names itself, and is as findable as before.
This matches how the neighbouring case was already handled: a view that fails *while* its host is
shutting down has been treated as routine for the same reason.
