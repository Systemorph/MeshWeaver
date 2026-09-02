---
Name: A published type can no longer be left on yesterday's build
Category: Fix
Description: A type could finish compiling your latest code and still hand every page the previous build, because the release that publishes it quietly failed to be written and nothing ever checked. The platform now checks at the end of every compile, and cuts the missing release itself.
Icon: CheckmarkCircle
Order: -20260902
---

# A published type can no longer be left on yesterday's build

Some changes are hard to notice because everything says they worked. This was one of them.

When you press Compile on a type, two things happen: your code is built, and a **release** is written
— a small record that says "this is the build to serve". Pages load the build the release names. The
build and the release are produced together, and until now nothing checked that they agreed.

They could disagree. The build is compiled by the platform itself, so it succeeds even where you
personally cannot write; the release is written **as you**, so that it carries your name. On a
curated or read-only area, that second write can be refused — and it was refused quietly, because a
missing release was never allowed to fail a compile. The type then finished in a state that looks
completely healthy: compiled successfully, sources current, an assembly built, a release present.
The release was simply the previous one, and every page kept loading the previous build.

Worse, there was no way out from the outside. The request had already been marked as handled, so
asking again did nothing: the second request was answered by the build that already existed. On one
type this went unnoticed for a day, while a merged fix sat compiled and unreachable.

**The platform now checks the obvious thing at the end of every compile**: does a release actually
name the build this type is about to advertise? If not — and the check only fires when it can prove
the build moved on — it writes the missing release itself, from the bytes just compiled. Nothing is
recompiled, nothing waits, and the release is written under the platform's own identity, so an area
you cannot write to no longer costs you your release.

And it is no longer quiet. A type that reaches this state is reported as an error naming the type
and the stale release, and the compile's own activity log records what was found and what was done —
including the case where even the repair could not be written, which now says so instead of leaving
you to compare two fields nobody thinks to compare.
