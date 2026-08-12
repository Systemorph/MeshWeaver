---
Name: Version history no longer lists a version it cannot open
Category: Fix
Description: A snapshot became visible in a node's version history a moment before its contents were written, so opening the newest version right after a save could fail. A version now appears only once it is complete.
Icon: History
Order: -20260813
---

# Version history no longer lists a version it cannot open

Every save of a node keeps a snapshot, and the version list is built by looking at which
snapshots exist. The snapshot file was being created first and filled in immediately
afterwards — a gap of milliseconds, but a real one. Open the history in that gap and the
newest version was already listed while its contents were still empty, so asking for it
failed outright rather than showing the saved state.

It went unnoticed because the gap is only reachable by looking at a version the instant it
is written — which is exactly what happens when you save and then immediately compare
against the previous version, or when a busy server interleaves the two.

A snapshot is now written aside and moved into place in one step, so it becomes visible only
once it is complete. There is no longer a moment in which the history offers a version that
cannot be read.
