---
Name: Every portal replica sees a committed change
Category: Fix
Description: A database change now invalidates the matching caches in every running portal replica, so one request cannot keep seeing an old node after another replica has already loaded the new one.
Icon: ArrowSync
Order: -20260912
---

# Every portal replica sees a committed change

A multi-replica portal could hold two answers for the same node after a successful update. One
replica loaded the new database row while another kept serving its old exact-path cache, sometimes
until that process restarted. This surfaced during plugin publication: the plugin list showed the
current build while an exact lookup still returned the previous one.

Every process now connects its database change listener directly to its local cache invalidation
feed. Older notification payloads remain supported during rollout by reading the committed row once
for its node type and version. A notification handled by one replica no longer has to reach another
replica's process memory through a single cluster grain. Database echoes stay on the cache-only
feed, so actions such as access-grant email and instance synchronization still run once from the
writer's logical event.
