---
Name: A release can no longer ship modules that disagree about the platform
Category: Feature
Description: Every plugin module a release builds records which platform build it was compiled against, and your portal compares that record to decide whether an update is really new. The release pipeline now checks the records themselves — a release whose modules name different platform builds is stopped, instead of shipping bundles no portal can recognise.
Icon: Checkmark
Order: -20260905
---

# A release can no longer ship modules that disagree about the platform

Plugins reach your portal as **modules** — bundles the portal downloads and loads on its own,
separately from the portal image. Each bundle records which build of the platform it was compiled
against, and your portal uses that record to answer one question: *is this bundle actually newer
than what I already have?* A version number alone cannot answer it, because the same source rebuilt
against a newer platform republishes under the same version.

That record only works if it is **true**. A release builds one platform and many modules, so every
module from one release must name the same platform build. When they disagree, at least one of them
names a platform that never existed — and your portal, unable to match it, quietly answers "I
already have this" on every check, forever. Nothing looks wrong from the outside: the bundles are
well-formed, the records are present, and the release reports success.

That happened twice. Both times it was fixed at the source — where the record comes from — and both
times the checks that were added asked the pipeline *how* it was configured, never *what it
produced*. A configuration check cannot see the result go wrong by a route nobody anticipated.

## What changes

The release pipeline now checks the outcome. Every module bundle a release builds hands its recorded
platform build forward, and the step that confirms a release is complete refuses one whose modules
disagree — naming each module and the platform it claims. A bundle that cannot state its platform at
all is refused separately and by name, rather than counted as "agrees": a record nobody wrote must
never read as a record everyone matches.

For you this means the safeguard now fails on the thing that actually harms you — a release that
would leave your portal unable to recognise its own updates — and not merely on the one route that
caused it the last two times.
