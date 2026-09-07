---
Name: A portal can now be pointed at one publication
Category: Fix
Description: Two build lanes publish to the same shelf, and when they overlap the result is one seal covering some bytes from each — self-consistent to every reader, and wrong. The readers now understand a layout in which that cannot happen; the publishers move to it next.
Icon: Layers
Order: -20260907
---

# A portal can now be pointed at one publication

When CI bakes a repository's content, it puts the result on a shared shelf: a directory of bundles
with a small file at the end that says *this set is complete*. Portals seed from it, gates install
from it, and the completeness marker is what makes the whole thing safe to read.

Replacing what is on that shelf means removing the marker, writing the new files over the old ones,
and putting the marker back. That is deliberate — without it, a reader could pick up half of one
publication and half of another under a marker that claims neither.

## The problem

**The same shelf has more than one publisher.** When two of them overlap, both remove the marker,
both write, and whichever finishes last puts a marker over a directory holding *some bytes from
each*. Nothing about the result looks wrong. Every file the marker lists is present, every read is
self-consistent, and the first symptom is a portal that starts and renders nothing.

A check added earlier catches most of this: a publisher now re-reads every file it uploaded, right
before it writes the marker, and refuses if any of the bytes are not its own. That converts a silent
mix into a visible, red build. It is not the same thing as making the mix impossible — it checks
*after* writing rather than preventing two writers from overlapping at all.

## What changed here

The fix for that is to stop replacing anything in place. Each publication gets **its own directory**,
and a one-line pointer says which directory currently applies. Two publishers then never write the
same files, so a mixture cannot be produced at all — and a reader that arrives mid-swap gets the
previous publication, whole, rather than a torn one.

This release lands **the reading half**. Portals and the registry now follow that pointer wherever
it exists, and read exactly as before wherever it does not — which is everywhere, today, because no
publisher writes one yet. It has to arrive in this order: a portal reads the shelf for as long as it
runs, so every deployment has to understand the new layout before anything starts producing it.

The publishers move next. Until they do, the check described above is still what stands between an
overlap and a portal that renders nothing — so a build that goes red saying it refused to seal is
doing its job, and is not something to work around.
