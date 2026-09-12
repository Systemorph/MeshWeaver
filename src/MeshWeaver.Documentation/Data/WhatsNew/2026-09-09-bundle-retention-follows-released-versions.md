---
Name: Bundle cleanup preserves released versions
Category: Fix
Description: Bundle cleanup keeps 30 days of history and protects adopted versions while newer releases are prepared.
Icon: Sparkle
Order: -20260909
---

# Bundle cleanup preserves released versions

Publishing many builds no longer removes recent bundles through a build-count limit.
Unused bundles stay for at least 30 days. Adopted versions, official releases and the
latest sealed bundle for each major version remain protected regardless of age.
Cleanup stops when a reported consumer version cannot be resolved.
