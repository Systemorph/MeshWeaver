---
Name: A release that was already everywhere is no longer passed over
Category: Fix
Description: When a build found that every destination already held exactly the content it was about to publish, it correctly published nothing — and its report of that was indistinguishable from a publication that had reached nowhere. The component that picks which release to build against then skipped those releases as unverifiable. Such a build now says so in its own words, and the release is taken.
Icon: Box
Order: -20260914
---

# A release that was already everywhere is no longer passed over

When two builds of the same commit run close together, the first one publishes the content and the
second finds every destination already holding exactly it — sealed, from the same commit. The right
thing then is to publish nothing, and that is what happens.

**What it could not do was say so.** The end-of-run report counted destinations it had written to,
destinations a sibling build had written to while it worked, and destinations where something newer
had since appeared — but there was no count for *"this one already had it"*. A build that found the
content everywhere therefore reported the same numbers as a build that had reached nowhere at all,
and two different readers drew the wrong conclusion from it on the same pair of builds: one was
reported as having recorded a release for a publication that reached nothing, and — the part that
cost something — the component that chooses which released platform to build against treats a
publication that reached nothing as unverifiable, and passed both of those perfectly good releases
over in favour of older ones.

**A destination that already holds the publication is now counted as one the publication reached**,
separately from the ones this build wrote, and reported on every build including when it is zero.
The release selector reads it and takes such a release instead of skipping it, while a publication
that genuinely reached nowhere is still passed over exactly as before. Reports produced before this
change are read the way they always were, so nothing re-interprets history.
