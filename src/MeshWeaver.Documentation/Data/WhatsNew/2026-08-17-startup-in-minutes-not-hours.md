---
Name: Portals start in minutes, adopting the CI-compiled content
Category: Fix
Description: Shipped images silently lacked the API-surface manifest, so every booting portal fell back to recompiling all shipped content instead of adopting the CI-published build — startup took as long as a full recompile. The manifest now ships in every image and boot adopts the published bake.
Icon: Rocket
Order: -20260817
---

# Portals start in minutes, adopting the CI-compiled content

The whole bake-on-CI lane exists so that a booting portal loads content compiled once on CI
instead of recompiling everything itself. The key that connects the two — the API-surface
manifest the portal derives its identity from — was written at build time but silently dropped
from every published output, images included: the publish hook added it after the copy had
already happened. Portals therefore resolved a per-commit identity, never matched the published
bake, and every pod rebuilt all shipped content at boot.

The manifest now ships in every image (verified inside a locally published container). What you
notice: after a platform update, pods seed the CI-published bundles at boot and compile only what
CI did not bake — startup becomes a matter of minutes, not the length of a full content rebuild.
