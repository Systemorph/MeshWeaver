---
Name: Superseded bundle publications no longer pile up on the share
Category: Fix
Description: The prebuilt-bundle cleanup only ever removed whole framework builds, so once a build stayed in use its superseded publications accumulated inside it for ever. It now removes those too — never the one in use, never anything recent, and never anything at all when it cannot tell.
Icon: Broom
Order: -20260910
---

# Superseded bundle publications no longer pile up on the share

Your installation keeps a store of **prebuilt bundles**: code that CI compiled ahead of time so
your portal does not have to compile it at every start. The store is shared storage, and it is
finite. When it fills up, writes to it stop working — quietly. That has happened: a portal reached
3 MiB free, and from then on every recompile landed as corrupt bytes and every publication was
written half-way, with nothing reporting a fault until someone looked at the free space.

Cleanup for that store already existed, and its rule is the right one: **keep whatever something
references, remove only what nothing does, and when the references cannot be read, remove nothing.**
It kept the build you are running, every released build, everything a deployment pins, everything an
installation reports, and everything younger than 30 days.

But it worked at one level only — a whole framework build, kept or removed as a unit. Inside a build
that something *does* reference, it removed nothing at all. That is fine today, and it stops being
fine as soon as publications start being written side by side instead of on top of each other, which
is the change that makes a half-written publication impossible. Then a referenced build accumulates
one superseded publication per CI run, indefinitely, and the share fills up on a schedule.

## What changes

**Cleanup now also removes superseded publications inside a build it is keeping** — the same rule,
one level down.

A superseded publication is removed only when **all** of these are true:

- **nothing points at it.** Each source keeps a one-line pointer naming the publication that
  currently applies; the one it names is kept however old it is.
- **the pointer could be read.** If the pointer is missing its target, unreadable, or blank — which
  is what it looks like while it is being replaced — nothing under that source is removed at all.
  Not being able to tell is treated as "it is in use", never as "it is not".
- **its own contents could be read.** A publication that cannot be examined is kept.
- **it is older than 30 days.** As before, this is a floor: a setting can lengthen the window and
  cannot shorten it.

**A publication in flight is safe by arithmetic, not by timing.** It is protected from the moment
its first byte lands, because it is far newer than the window. There is no lock and no coordination
to get wrong, and nothing being written can be caught mid-write.

**Anything the cleanup cannot positively identify as a publication is left alone.** It removes a
directory because it recognises it, not because it fails to recognise it — so bookkeeping the
cleanup has never heard of is kept rather than swept up.

**The same safety valves as before.** If anything in the picture cannot be read, the whole pass is
abandoned and nothing is removed. The report-only mode reports what it would have taken without
taking it. Every removal is written to the store's own ledger, naming what went and how much space
came back, and a removal that fails is counted and reconsidered next time rather than ignored.

## What this does not change

**Nothing is removed that your portal could read.** A publication that is not the one being pointed
at is unreachable by every part of the portal that reads this store — and the cleanup asks that
question using the *same code the portal uses to answer it*, rather than a second copy of the rules
that could drift.

**No change to what is kept at the build level.** The running build, released builds, pinned builds,
reported builds, recent builds and unreadable ones are all kept exactly as before.

**Nothing on your installation behaves differently today.** Publications are still written the old
way, so there are no superseded ones to remove yet. This is the cleanup being ready before the
change that would otherwise make the store grow without limit.
