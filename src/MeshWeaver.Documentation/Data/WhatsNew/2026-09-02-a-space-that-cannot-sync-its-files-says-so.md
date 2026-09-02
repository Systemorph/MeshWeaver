---
Name: A space that cannot sync its files now says so
Category: Fix
Description: When a space's images and videos were too large to transfer, the sync reported success and skipped that space on every attempt afterwards. It now reports the refusal — and keeps trying.
Icon: CloudArrowUp
Order: -20260902
---

# A space that cannot sync its files now says so

A space keeps its pages in step with a git repository, and that includes the files the pages point
at — images, posters, course videos. Those files travel with the sync.

Some spaces have a lot of them. When a transfer was too large to carry, it was refused — correctly,
by a guard that exists to stop one oversized transfer taking a server down with it. The trouble was
what happened next: **the sync counted the refused files as "nothing to do" and reported success.**

Zero files transferred is exactly what a space with no files reports. So there was no way, from the
outside, to tell a space that was perfectly in sync from one whose entire media library had been
refused on every attempt.

It got worse from there. A successful sync is *remembered* — the platform records "this space
already matches this version of the repository" and, on every later run, skips it without looking.
So one refusal did not just go unreported: it was written down as success, and from then on nothing
re-tried. The space stayed frozen exactly as it was, indefinitely.

The person who eventually found out was a learner opening a lesson with a missing video.

**A refused transfer is now recorded as what it is.** The sync reports it, names the spaces whose
files did not arrive, and — the part that actually matters — **does not write down a success it did
not have**, so the next run tries again instead of skipping. When the underlying problem is fixed,
the files arrive on the next sync with nobody having to remember to force one.

This does not, on its own, make very large media libraries transferable; that is a separate piece of
work. What it changes is that a space in that state now tells you, instead of looking identical to
one that is fine.
