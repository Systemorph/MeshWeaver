---
Name: A platform fix now reaches the add-on repository it was written for
Category: Fix
Description: An add-on repository records which build of the platform it compiles against, and nothing kept that record current — so a platform fix could be merged, released and still not reach the repository whose failure prompted it.
Icon: ArrowSync
Order: -20260906
---

# A platform fix now reaches the add-on repository it was written for

Every repository that extends MeshWeaver writes down **which build of the platform its own code is
compiled and tested against**. Pinning it that way is deliberate: it means two runs of unchanged
code agree with each other, and a platform regression shows up on the change that moves the pin
rather than landing on whoever happened to push next.

The price of pinning is that somebody has to move the pin — and that half was never automated. The
platform *images* an add-on runs its gates inside were being bumped automatically after every
release. The record of which platform **source** build the add-on compiles against was not being
moved by anything at all.

On 2026-09-06 that cost a morning. A fix was made in the platform specifically for a failure in the
plugin catalog repository, and merged. Twenty-five minutes later that repository was still failing
on exactly the failure the fix had repaired, because it was still compiling against a platform build
from that morning — 49 changes before the fix. The pin was well inside every staleness bound anyone
had written down. Being *roughly current* simply is not the same as *carrying the fix we just merged
for you.*

The pin is now moved on a schedule, as a proposed change that the full test suite runs on before
anyone accepts it, in the repositories where such a pin exists.

## What has deliberately not changed

**The pin still moves as a reviewed change, never automatically.** The whole value of pinning is
that a platform regression arrives attached to the change that adopted it. An automatic update
would turn the pin back into "always the newest thing", which is what pinning exists to prevent.

**The platform image set is untouched by this.** Which released platform build an add-on runs
against is one decision made for the whole fleet at once, not a per-repository schedule.

**A daily schedule is a floor, not a promise of speed.** It guarantees the record is never quietly
days old. It does not answer "did the fix from twenty minutes ago arrive" — for that, the update can
still be requested by hand at any time, and the repository-health board now also reports an add-on
repository whose main branch is failing, instead of that being noticed hours later.
