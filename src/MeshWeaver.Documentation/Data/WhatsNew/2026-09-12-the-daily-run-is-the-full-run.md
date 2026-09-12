---
Name: The daily run is the full run — node repositories no longer rebuild on every platform build
Category: Feature
Description: Since 2026-09-12 a platform build no longer wakes every node repository. Each repository builds and tests against the newest sealed platform set on its own pushes and once a day, and that daily run is what validates a platform build. The per-build release wave is off by default and reserved for a MAJOR bump.
Icon: CalendarClock
Order: -20260912
---

# The daily run is the full run — node repositories no longer rebuild on every platform build

Until today every platform build core's CD promoted — tens of them a day — reached the control
instance as a signed build fact and fanned `meshweaver-framework-released` out to every registered
node repository, so every catalogue re-ran its full lane against a platform that differed from the
previous one by a commit inside a compatible line. Measured over 2026-09-05..12, those dispatch runs
were about 15,000 billed Actions minutes a day — 30 % of the fleet's bill — and most of them were
superseded by the next dispatch before they finished.

## What changed for an operator

- **A merge to core `main` still builds, promotes, bakes and seals a set and posts one build fact to
  the control instance.** That fact is now *consumed* — verified, counted, logged — and dispatches
  nobody. The switch is `Hosting:PlatformBuilds:BroadcastFrameworkReleases` on the control
  instance, default off (MeshWeaver.Plugins#1707).
- **A node repository builds and tests against the newest sealed set on its own pushes and once a
  day.** The daily `schedule` (staggered 03:03 → 03:56 UTC across the Systemorph repositories) is
  the full run: every module packed, every NodeType compiled, every `Tests` area executed, the
  publication baked and sealed. The satellites no longer list `meshweaver-framework-released` at
  all; a satellite's own publication still wakes exactly its declared dependents
  (`meshweaver-upstream-published`).
- **"Validated" means the daily runs stayed green on the set.** There is no tag for it and none is
  planned: a set is sealed by its CD run's job conclusions and its `_releases/<version>` markers,
  which every reader re-derives. The clean `X.Y.Z` of a release remains the only tag-shaped
  promotion.
- **A bundle for a new framework identity now arrives with the next daily run**, not minutes after
  the merge. An installation keeps serving the previous sealed publication meanwhile
  (`Modules:VersionStrictness` `Family` admits a sealed set of the same line), so "no bundle yet
  for this identity" is the ordinary state for up to a day.
- **A MAJOR platform bump is the exception.** Prove it with each satellite's `workflow_dispatch`
  (gates, publishes nothing), or turn the broadcast on for that one build and add the event type
  back to the receivers in the same change set.

## The assumption, and what it does not yet cover

This is the recommended setup because the platform is backwards compatible within a MAJOR. That
holds for the platform's own surface; it is **not** checked for third-party assemblies, in either
direction — the 2026-09-11 YamlDotNet 18-vs-16 crash loop (#4083, Systemorph/Memex#281) went
through a link probe that compares type names only and has no `FileLoadException` state. The
per-core-build compatibility gate and the probe extension that close this are planned, not built.

The pipeline end to end, what runs when and on whose minutes, and the MAJOR-bump procedure are in
the Hosting plugin's page `Hosting/BuildAndReleaseProcess` (`get Hosting/BuildAndReleaseProcess` on
the memex MCP); the corrected core pages are
[Release to Production](/Doc/Architecture/ReleaseToProductionPipeline),
[Release Process](/Doc/Architecture/ReleaseProcess),
[Self-Update Target Selection](/Doc/Architecture/SelfUpdateTargetSelection),
[The Image Tag Contract](/Doc/Architecture/ImageTagContract) and
[Module Build Architecture](/Doc/Architecture/ModuleBuildArchitecture).
