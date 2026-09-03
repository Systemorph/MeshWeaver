---
Name: A platform release reaches plugin repositories through memex
Category: Feature
Description: When the platform promotes a build, the plugin repositories learn about it from memex — one event, emitted by the registry, to the repositories its deployment records name. Core's CD publishes the build fact and finishes; it no longer dispatches into any other repository's CI.
Icon: Send
Order: -20260903
---

# A platform release reaches plugin repositories through memex

When the platform promotes a new build, the repositories that ship plugins — Plugins, Education,
Reinsurance, SocialMedia — have to rebuild their bundles against it, or every portal that
self-updates to it reads `FrameworkDeclined` and adopts nothing. How they are *told* is the one
place where the top-level repositories used to reach into each other.

**The rule now (maintainer, 2026-09-03):** none of the top-level repositories depends on another.
The integration is an event. **memex issues the event that something has a new version; the GitHub
repositories subscribe to it and trigger their builds. Core publishes an event and finishes.**

## What changed

- **Core's CD no longer dispatches into any other repository.** Its `notify-dependents` job — a
  GitHub→GitHub fan-out that read other repositories to discover who to wake — is gone, together
  with the reusable workflow behind it and the package-release variant. The one thing core emits is
  the signed build fact it already POSTs into the registry's `Hosting/PlatformBuilds` inbox. A guard
  (`PlatformReleaseNotifyGuard.CoreDispatchesToNoRepository`) fails the build if a
  `repository_dispatch` sender returns to any workflow core runs on its own behalf.
- **memex emits the wave.** The Hosting module's inbox watcher verifies the build fact, bumps the
  repositories' platform pins, and broadcasts `meshweaver-framework-released` to every subscribed
  repository with the GitHub App the registry already holds. (Until now that broadcaster existed and
  was called by nothing — every dispatch the repositories received came from core's CI.)
- **Who is subscribed is data in the mesh.** A repository is subscribed because a `Hosting/Deployment`
  record on the registry names it as a registry source (`pluginRepos[]` with `isRegistrySource`).
  The record that makes a repository part of the fleet is the record that subscribes it, so the two
  cannot drift. The `FrameworkBroadcast__Subscribers__N` configuration slots are retired; an empty
  set on the registry is a warning that names the records.

Each repository's own `schedule` poll remains the fallback for a lost dispatch, so a missed event
costs one delayed rebuild wave — never a fleet held on stale bundles under a green tick.

Full reference: the "release EVENT" section of `Doc/Architecture/ContinuousDeliveryContract` and
`Doc/Architecture/RepositoryDependencyDirection`.
