---
Name: Database connection exhaustion under load
Category: Fix
Description: Portal operations no longer fail with database timeouts when many pages and users are active at once.
Icon: Sparkle
Order: -20260812
---

# Database connection exhaustion under load

Under a burst of activity the portal could run out of database connections, and whatever happened to
be running at that moment failed with a timeout — a page that would not finish onboarding, an
activity that was not recorded, a permission check that could not reach a verdict, or a code node
whose sources briefly could not be listed.

The cause was a missing limit rather than a slow database. The portal is designed to keep a small,
fixed number of database operations in flight at once, but that limit was only ever applied to part
of the work: the most frequent operations of all — loading a single node, resolving a URL to a node,
and checking whether a node exists — were not counted against it. Under load they could all pile up
together and use every available connection, after which everything else had to wait and eventually
gave up.

Those operations now run under the same limit as the rest, so the portal's total demand on the
database stays inside a fixed budget no matter how many people are active. Nothing about
capacity or timeouts changed — the work is simply paced instead of unbounded.
