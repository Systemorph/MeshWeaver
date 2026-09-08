---
Name: A platform update no longer stalls when two builds publish the same content
Category: Fix
Description: Two delivery runs that published the identical content at the same moment both declared failure, so neither produced a sealed set and the platform update behind them did not roll. A run whose content is proved to be on the shelf already, sealed, now reports that instead of failing.
Icon: Cube
Order: -20260908
---

# A platform update no longer stalls when two builds publish the same content

Everything a portal runs at boot without recompiling it — the plugin bundles, the module set, the
description of the platform they were built against — is *published* by the delivery pipeline into
one directory per platform identity, and finished with a single completeness marker written last.
The marker is what makes "published" mean "all of it is here".

Two delivery runs can be in flight at once, and when the platform's public surface has not changed
between them they resolve the **same identity** and publish the **same content** — the ordinary case,
because that identity only changes when the surface does. They then write the same directory. Each
run checks, immediately before writing its marker, that every file it uploaded still holds its own
bytes; a run that finds somebody else's bytes refuses to write the marker, which is what stops a
directory holding half of one build and half of another from ever being declared complete.

That check could not tell two situations apart, and one of them is not a failure at all: **another
run publishing something else**, and **another run publishing exactly what this one was asked to
publish**. Two builds of one commit produce byte-identical descriptions and *non*-identical
compiled bundles — compilation is not reproducible byte-for-byte — so the second case looks, file by
file, like the first.

On 2026-09-08 two delivery runs published the same content to two storage targets. Each won one
target and each reported failure on the other. Both targets ended up sealed with exactly the right
bytes, both runs went red, neither produced a sealed set, and the platform update waiting behind
them did not roll — for a publication that was, in fact, correct and complete.

**A run that is entirely overtaken now checks whether the publication it was asked to make is
actually there.** It reports success only on proof: every file describing *what* was baked is
byte-identical to its own, all the overtaking bytes come from one named publication, and the
completeness marker on the shelf lists this build's own bundle set and was written by that same
publication. Anything short of that is still a failure, with the reason printed — an unsealed
directory, a different source commit, a different bundle set and an unstamped file each keep the run
red. Two builds that genuinely publish *different* content still both refuse, and a directory
holding a mixture is still never declared complete.

The underlying race — two runs replacing one directory in place — is not removed by this, only its
false alarm. Removing it means giving each publication its own directory and moving a pointer last;
see [Sealed Publication Generations](/Doc/Architecture/SealedPublicationGenerations) for that design
and [Sealed Publication Reads](/Doc/Architecture/SealedPublicationReads) for how a publication is
written and read today.
