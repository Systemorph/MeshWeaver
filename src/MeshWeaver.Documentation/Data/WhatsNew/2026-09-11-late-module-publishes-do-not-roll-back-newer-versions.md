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

The registry now compares published versions before moving its activation head. An older upload is
still kept as warehouse stock, but a newer working generation remains current. A missing or unusable
newer generation does not block recovery: in that case the valid upload still replaces it.
