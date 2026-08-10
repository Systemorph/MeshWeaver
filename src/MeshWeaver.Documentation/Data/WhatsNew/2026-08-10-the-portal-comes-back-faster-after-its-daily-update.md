---
Name: The portal comes back faster after its daily update
Category: Fix
Description: After an update, the portal rebuilds every dynamic type — and its own progress reports were clogging the router that the rebuild itself depends on. Rebuilds no longer fight their own bookkeeping, so startup no longer stretches from minutes into hours.
Icon: TopSpeed
Order: -20260810
---

# The portal comes back faster after its daily update

Every time the portal updates itself it rebuilds all of its dynamic types — the
compiled node types your spaces are made of. That rebuild is designed to take
about ten minutes in the background. Lately it was taking two hours, and while it
ran the portal was slow for everyone: pages waited, messages queued, and the
router kept reporting that it was falling behind.

The cause was a feedback loop between the rebuild and its own progress reports.

Each compile writes a running log — every line an update to a small activity
record, every update a message routed to that record's home. To route a message,
the portal looks up where the path lives, and it keeps a cache of those answers
so the lookup is instant. But every write to a record also *invalidated* the
cached answer for that record — so the very next progress line had to look the
path up in the database again, from scratch. During a rebuild, with hundreds of
compiles each writing dozens of lines, the router spent nearly all its time
re-answering a question whose answer never changes: an update cannot move a
record. The router saturated, the rebuild's own status checks timed out behind
it, each timeout burned its full budget, and a ten-minute rebuild became a
two-hour one — which kept the storm alive that much longer.

Now the router distinguishes the two things that can happen to a record. When a
record is *created or deleted*, the cached route is discarded — those are the
changes that can actually move things. When a record is merely *updated*, the
route stays warm, because the record is exactly where it was; only the cached
copy of its content is marked stale for the readers that need fresh content.

Routing to a busy record is a dictionary lookup again, the rebuild's status
checks come back promptly, and the update window shrinks back to the minutes it
was designed to take.
