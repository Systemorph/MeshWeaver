---
Name: A recompiled type stops serving its old code
Category: Fix
Description: A type could finish compiling, report success, and still hand every page the previous build — because the release the type pointed at was never advanced. It now always points at a release that names the build it just compiled.
Icon: Checkmark
Order: -20260906
---

# A recompiled type stops serving its old code

You edit a type's code, it compiles, the compile reports success — and the pages that use it keep
running the **previous** build. Nothing looks wrong: the type says it compiled, its sources are
current, an assembly exists, and it names a release. Only the release it names is the one from
before your change.

That could happen until now, and the only thing that cleared it was a restart of the portal — the
banner on the affected pages said as much, and it was right: nothing in the platform would fix it on
its own.

## What was happening

Publishing a release is written twice when the first write's *outcome* is not observed — a normal
thing, because the compile refuses to hold its own completion open waiting for a bookkeeping write.
The second attempt was supposed to be a retry. It was not: it made up a fresh name for the release
each time, stamped from the clock. So it either published the same build twice under two names, or —
when both attempts fell inside the same second — asked for a name it had itself just used, was
refused, and gave up quietly. The type was then left pointing at the older release, and every page
kept binding the older assembly.

## What changes

A release now belongs to the compile that produced it, not to the second an attempt happens to run
in. A retry addresses the same release the first attempt addressed and simply adopts it if it is
already there. Either way the type ends up pointing at a release that names the build it just
compiled.

Two things follow that you can see:

- **A recompiled type serves the code you just compiled.** If a release write has to be retried, it
  no longer matters when the retry lands.
- **One build, one release.** The release list for a type no longer shows the same build twice a
  second or two apart.

Nothing you do changes. There is no new setting, and existing releases are untouched — the duplicates
already recorded stay as history.
