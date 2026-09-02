---
Name: A rebuilt module reaches your installation
Category: Fix
Description: A module rebuilt against a new platform used to republish under the same version, so installations holding the old build answered "already up to date" and never fetched it. The update check now compares the framework the bytes were built against, not just the version.
Icon: ArrowSync
Order: -20260902
---

# A rebuilt module reaches your installation

A module's version describes its **source**. Rebuild the very same source against a new platform
build and it publishes again under the *same* version — different bytes, identical label. Until now
an installation's update check compared the version alone, so it answered "already landed", fetched
nothing, and no later check ever looked again. The installation kept running a build made for a
platform it no longer runs.

That is not a theoretical gap. After one platform release the updater landed the dozen modules whose
versions had moved and then went quiet: one AI provider module had been rebuilt but not renumbered,
so nobody pulled it, and rolling the image anyway crash-looped the portal — the older build could no
longer find a service the new platform had moved. The fleet sat on the previous image until it was
sorted out by hand.

The registry now states, per module bundle, which platform build produced its bytes, and the update
check compares that alongside the version. Concretely:

- **Same version, different platform build** → the module is fetched and lands, and the log line
  names both builds instead of saying "already landed".
- **Same version, same platform build** → still skipped without downloading anything, exactly as
  before.
- **An installation whose record predates this** → it lands once and records the build, then settles.
- **A registry too old to state a build** → nothing is downloaded on speculation, and the log says
  the build could not be checked rather than implying everything matched.

Nothing about *which* modules may be installed changed: the platform floor
(`minMeshVersion`) is still the only gate on whether a bundle can land. The framework identity only
answers a different question — whether there is anything new to land at all.

Where the design decisions live: [Modules](/Doc/Architecture/Modules) → "Already landed means this
content against this FRAMEWORK".
