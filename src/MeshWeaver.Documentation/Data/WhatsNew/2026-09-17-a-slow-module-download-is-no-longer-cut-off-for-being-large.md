---
Name: A slow module download is no longer cut off for being large
Category: Fix
Description: Downloading a plugin's compiled module had two minutes per attempt, and the whole archive had to arrive inside that window — so a large module was cut off even while it was still transferring perfectly well, and the portal fell back to compiling it. The download now streams, and it records what it moved.
Icon: CloudArrowDown
Order: -20260917
---

# A slow module download is no longer cut off for being large

When your portal installs or updates a plugin that ships a compiled module, it downloads that
module from its registry. That download had **two minutes per attempt**, and the entire archive had
to arrive within it. A module that was simply big — or a registry on the other side of a slow
link — ran out of budget while the transfer was still going perfectly well.

What you saw when that happened was not an error. The portal quietly fell back to **compiling** the
module instead, which works, but takes longer and happens again on the next pass. On one portal this
repeated for fourteen packages in a row, each abandoned after exactly three minutes, and then again
the following day.

The download now **streams**: the two-minute budget covers whether the registry is *answering*,
rather than how many megabytes it has to send. A transfer that is slow but making progress is
allowed to finish.

## It also says what it did

The more useful half is that this was previously invisible. Every one of those failures left a
single line — *"The operation has timed out"* — with no size and no duration anywhere, so nobody
could tell a large module from an unresponsive registry. They want opposite fixes.

Each transfer now records the bytes it moved, how long it took and the rate; and a transfer that is
cut short records how much had already arrived. Zero bytes after two minutes points at the registry.
Most of a large archive points at the size.

## Honest about what is not fixed

**This does not promise that every module now lands.** If a module genuinely cannot be transferred
in the time available, it will still fall back to compiling — but the log will now say so in terms
you can act on, instead of a bare timeout. Nothing about which modules you get, or which versions,
has changed.
