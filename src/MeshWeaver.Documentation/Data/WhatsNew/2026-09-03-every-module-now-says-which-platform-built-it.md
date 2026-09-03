---
Name: Every module now says which platform build made it
Category: Fix
Description: Module bundles were shipping without recording the platform build behind their bytes, so the update check that compares it had nothing to compare and every module answered "already up to date". Bundles now state it, and one that cannot is refused instead of published.
Icon: ArrowSync
Order: -20260903
---

# Every module now says which platform build made it

A module's version describes its **source**, so a rebuild of unchanged source against a new
platform publishes again under the same version. The update check was taught to see through that: it
compares the platform build the bytes were made against, not the version alone, so a genuine rebuild
reaches your installation instead of being skipped as a no-op.

It compares a value the bundles were not carrying.

Every bundle the fleet published — all thirty-four of them, on both build paths — recorded
`built-against MVID (unrecorded)`. The packer looked for the platform's identity file *next to the
module*, and modules are no longer built that way: the platform is a container image, so the file
sits in the image, not beside the module's output. Nothing was red, because the field was optional.
The result was an update check that could only ever answer "already up to date — but the platform
build could not be checked", for every module, on every installation.

Two things change:

- **The bundle states it.** The build lane hands the packer the exact platform assemblies the module
  was compiled against, and that build's identity is written into the bundle.
- **A bundle that cannot state it is not published.** The packer refuses to write one, the build
  step refuses to pack without naming the platform, and — the one that matters — the hand-over to
  the registry refuses to upload bytes whose manifest is silent about it. Each failure says what is
  missing and how to supply it.

The reason for refusing rather than publishing-and-warning is that this particular unknown cannot be
repaired later. An installation that does not yet know what it landed learns it the next time it
fetches. A registry serving a bundle that never said what it was built against will not say next
time either — so every installation pointed at it stays in "could not be checked" indefinitely.

Nothing about *which* modules may be installed changed: the platform floor is still the only gate on
whether a bundle can land.

Where the design decisions live:
[Module Build Architecture](/Doc/Architecture/ModuleBuildArchitecture) → "A bundle states what it was
built against", and [Modules](/Doc/Architecture/Modules) → "Already landed means this content against
this FRAMEWORK".
