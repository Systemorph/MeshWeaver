---
Name: The build protocol can now drive the bake
Category: Feature
Description: Behind a flag, startup compilation is coordinated by the build nodes end to end — the claim decides who bakes, chunks record what each part produced, and waiting servers complete on the GO signal instead of polling the cache.
Icon: Sparkle
Order: -20260813
---

# The build protocol can now drive the bake

The build coordination nodes introduced alongside this release no longer just describe the
protocol — they can run it. With the flag enabled, startup compilation goes through them end to
end:

- the claim on the build root decides which process bakes — the lease file is not consulted at all;
- one chunk node per content area opens with its own activity, and closes recording exactly which
  release each of its types produced — the build's outputs become browsable mesh state instead of
  log lines;
- every other server subscribes to the build's GO signal and completes the moment it lands,
  replacing the periodic re-polling of the shared cache — and a build that finished but never
  published its GO heals itself on the next start.

Execution itself is unchanged — the same dependency-ordered, store-probing sweep that runs today,
so enabling the protocol changes who decides and how completion is announced, never what gets
built. The flag stays off by default while the remaining pieces land: version-aware readiness fed
from the GO history, and builds running in their own disposable process.
