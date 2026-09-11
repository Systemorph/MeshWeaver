---
Name: Late module publishes no longer roll back newer versions
Category: Fix
Description: A slower release lane can still stock an older module, but it no longer makes that version replace a newer working one on the registry.
Icon: ArrowTrendingUp
Order: -20260911
---

# Late module publishes no longer roll back newer versions

The module registry can receive the same module from more than one release lane. A slower lane could
finish after a newer release and make its older upload the active registry version simply because it
arrived last. The next portal restart would then load the older module and silently undo the newer
release.

The registry now compares versions before changing which one it serves. An older upload still
lands, but a newer version whose files are present stays current. The older one is kept as its
fallback when it is the better one, and stays downloadable at its own version. A newer version whose
files have gone missing does not block recovery: the next valid upload replaces it. To go back to an
older version on purpose, publish the fix under a higher version.
