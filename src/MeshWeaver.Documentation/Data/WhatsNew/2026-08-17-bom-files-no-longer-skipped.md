---
Name: Files saved with a byte-order mark now load
Category: Fix
Description: A node file written by an editor that adds a UTF-8 byte-order mark was skipped on install — one sample package silently lost 62 of its 72 files.
Icon: Sparkle
Order: -20260817
---

# Files saved with a byte-order mark now load

Some editors — Windows ones especially — save UTF-8 files with an invisible marker at the start
called a byte-order mark. Files saved that way were unreadable to the installer: it skipped them,
installed whatever was left, and reported success. There was nothing on screen to say anything had
gone missing.

One sample package lost **62 of its 72 files** this way and contributed no types at all, for years,
while every install of it looked healthy.

Both halves are fixed. The marker is now handled wherever node files are read, so a file saved with
one installs exactly like a file saved without one. And a package that skips files now says so in
one summary line — how many were skipped, out of how many, and how many installed — rather than
burying one line per file where nobody would see it.

Skipped files still do not fail an install: content the platform cannot read is left out and the
rest is installed, which is the long-standing behaviour. The change is that you can now tell it
happened.
