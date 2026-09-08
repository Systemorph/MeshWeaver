---
Name: A pinned instance keeps its precompiled bundles
Category: Fix
Description: The clean-up of old build bundles on the shared volume now also keeps every build that a deployment record pins or a registered instance reports running — an instance held on an older build pulls exactly that build's bundles from the registry, and pruning them would have made it compile everything from source at each start.
Icon: Pin
Order: -20260908
---

# A pinned instance keeps its precompiled bundles

The shared data volume is now cleared of old precompiled bundles automatically (see [the earlier
entry](/Doc/WhatsNew/2026-09-08-the-shared-data-volume-no-longer-fills-up-with-old-builds)). One
kind of reference was missing from what that clean-up counts as "still in use": on the registry
instance, every *other* instance pulls the bundles of the exact build it runs — and an instance
deliberately held on an older build (a deployment record with a pinned image tag) runs a build far
older than the ten most recent. Its bundles would have been removed, and it would have compiled
every page type from source at each start instead of loading them.

**A build that any deployment record pins, or that any registered instance reports running, is now
kept** however old it is, together with the release marker that connects the version to its
bundles. If a pinned version has no such marker — nothing published bundles for it — nothing can
be kept for it, and the clean-up's ledger says so by name rather than silently keeping nothing.
