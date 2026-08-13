---
Name: Writing to a page no longer leaves a connection behind
Category: Fix
Description: Every save to a busy item used to abandon the internal connection it wrote through and open a fresh one, so the portal accumulated connections for as long as it ran. Abandoned connections are now closed the moment nobody is using them.
Icon: Sparkle
Order: -20260813
---

# Writing to a page no longer leaves a connection behind

When one part of the portal reads or writes something owned by another part, it opens a private
live connection to it — a mirror that stays in step while it is needed. Mirrors are shared, so a
page being read by ten things costs one mirror, not ten.

There is one moment when a mirror has to be replaced rather than reused: the item changed, and the
next writer must diff against what the owner actually holds, not against a snapshot from before.
The portal handled that by retiring the mirror so the next caller would build a fresh one. Retiring
it was correct. What was missing was closing it.

Nothing could close it safely, because nothing knew whether anyone was still reading. Counting
readers does not answer the question — a mirror's own internal plumbing subscribes to it, so the
count never reaches zero even when every real reader has gone. So a retired mirror was simply
parked, and parked mirrors were only ever cleaned up when the item went untouched for ten minutes
— which an item being written to continuously never does.

The result: every save to a busy item retired one mirror and opened another, on both ends of the
connection, and neither of the retired pair ever went away. A single rebuild of in-portal code left
about twenty of these behind; a morning of them left thousands.

Holders now say so. Anything that keeps a mirror past the moment it asked for one — a live view
being read, a save in flight — declares itself, and a retired mirror is closed the instant its last
declared holder lets go. Measured over a rebuild loop: of twenty-four mirrors opened and
twenty-three retired, eighteen are now closed immediately and the survivors are exactly the ones
still being read. A rebuild costs roughly half the internal machinery it did this morning, and the
saving applies to every ordinary save, not only to rebuilds.

Two smaller sources of the same kind of growth remain, both with different causes, and the
measurement that found this one is tightened again so neither can hide.
