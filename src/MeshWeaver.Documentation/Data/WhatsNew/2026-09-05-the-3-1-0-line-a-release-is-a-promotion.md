---
Name: The 3.1.0 line — a release is a promotion, not a rebuild
Category: Feature
Description: Every continuous build is now 3.1.0-ci.<n> and the next release is the clean 3.1.0, cut by promoting a sealed continuous set rather than building again. The rc line is closed and NuGet publication retired — modules compile against the image, never a package feed.
Icon: Rocket
Order: -20260905
---

# The 3.1.0 line — a release is a promotion, not a rebuild

The version you see under **Settings → About** changes shape today: continuous builds read
`3.1.0-ci.<n>`, and the release that follows will be a plain `3.1.0`. The `-rc` labels that ran
from `3.0.0-rc1` to `3.0.0-rc13` are gone, and so is the reason they existed.

## What changes for an install

- **Continuous installs** roll forward onto the first `3.1.0-ci.<n>` build exactly as before —
  `3.1.0` outranks every `3.0.0-*` by its minor number, so nothing is held or rolled back.
- **Stable installs** wait for the clean `3.1.0`. When it lands it is the *same bytes* as the
  continuous build it was cut from: the release lane no longer compiles anything. It resolves the
  tagged commit's already-promoted, already-sealed set, retags the images with the clean version,
  copies the release marker under that name, publishes the GitHub Release from the committed
  release notes, and opens the pull request that moves the line to `3.2.0`.
- **Module authors** compile against the platform image, as they have since bundles replaced
  packages. NuGet publication is retired with the rc line; the packages already on nuget.org stay
  listed as history and nothing in the fleet restores from them.

## Why

A release built twice is two releases. The old tag lane rebuilt the portal from whatever the plugin
repository's `main` happened to be at that minute, stamped no framework identity, and published no
sealed content bake — so an install that took the clean tag would have recompiled every module at
boot, and the self-updater's availability gate would rightly have refused it. Promotion removes the
second producer: what is tagged is what was tested, baked and sealed.

The rc labels had a smaller problem with a large effect. SemVer compares pre-release identifiers as
text, so `rc13` sorts *below* `rc2`, and nuget.org listed `rc9` as the newest pre-release for the
whole run. A clean line has no such edge: `3.1.0-ci.7900 < 3.1.0 < 3.2.0-ci.1`, and the build number
is the only pre-release identifier left.

Full mechanics: [Release Process & Versioning](/Doc/Architecture/ReleaseProcess) and
[Release & Self-Update Strategy](/Doc/Architecture/ReleaseStrategy).
