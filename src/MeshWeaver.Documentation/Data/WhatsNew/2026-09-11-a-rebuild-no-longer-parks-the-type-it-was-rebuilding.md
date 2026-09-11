---
Name: A rebuild no longer parks the type it was rebuilding
Category: Fix
Description: When a type was rebuilt while something else was still reading the previous build, the two could cancel each other out and the type was marked broken — even though its code was fine. Reading a build is now just reading; only a rebuild replaces one.
Icon: Checkmark
Order: -20260911
---

# A rebuild no longer parks the type it was rebuilding

When a type in your space is rebuilt, the new build replaces the old one — and for a moment both
exist, because whatever was already using the old build has to finish first. That handover was
fine on its own. What was not fine is that **merely reading a build counted as replacing it**.

So if a rebuild landed while something else was still reading the previous build — an app that
ships its own compiled code being checked, a code cell picking up a package's functions — the two
took turns replacing each other's build. After a few rounds the rebuild gave up and the type was
marked **broken**, with a red *compile failed* against code that was perfectly correct. Nothing in
the type's source had anything to do with it: it depended entirely on what else happened to be
running at that second.

## What changes

**Reading a build is now just reading.** Only an actual rebuild replaces the previous one. Two
things looking at two different builds of the same type no longer knock each other over, so the
handover finishes the way it was always meant to.

**A retry never takes something else down with it.** When a read has to be retried because the
build it asked for is being retired, that retry now leaves everything else alone — a recovery that
breaks its neighbours is what turned a momentary overlap into a stuck type.

**A type is no longer marked broken for a reason that is not in its code.** The failure this fixes
was permanent until someone rebuilt the type by hand: once marked broken, further use served the
stored error instead of trying again.

## Also in this release

**Code cells stop quietly holding on to memory after a rebuild.** When a package publishes its
functions to code cells, every rebuild of that package used to leave a copy of the previous build's
metadata in memory for as long as the server ran — it was never released, even after the build
itself was retired and its files removed. Each of those copies is held only for as long as a
session that uses it now, and released with it. Long-running servers that rebuild often were the
ones paying for this.

**A code cell can no longer be handed the wrong build of a package.** When two builds of the same
package were briefly in memory, resolving one by name could return either — arbitrarily, with no
way to tell which. It now declines to guess.

## What this does not change

**Rebuilds still replace what they supersede.** A rebuild retires the build it replaces exactly as
before, and the memory that build used is reclaimed as soon as nothing is using it. What changed is
only *who* gets to declare a build retired: the rebuild, never a reader. On a multi-server portal
that means a server which only *received* a new build — rather than producing it — now lets go of
the previous one when the type is next reloaded, instead of at the moment it first reads the new
one. That is at most one extra build held per version, and it is what keeps two copies of the same
build from existing at once.

**A genuine compile error is still a compile error.** Only the failures that came from this overlap
stop happening. A type whose code does not compile still says so, in the same place, with the same
diagnostics.
