---
Name: Writing the same node again no longer reconnects every time
Category: Fix
Description: Every write to a node owned elsewhere in the portal used to tear down its live connection and build a new one, so a busy node rebuilt it thousands of times — and a rebuild that did not finish in 30 seconds dropped the write, which is how page visits went unrecorded. The connection now stays open, and each write waits until it has seen the latest saved version.
Icon: ArrowSync
Order: -20260915
---

# Writing the same node again no longer reconnects every time

When one part of the portal writes a node that another part owns — recording that you opened a
page, appending a line to a running activity, updating a progress counter — it does so through a
**live connection** to the node's owner: the writer holds a copy of the node, computes its change
against that copy, and sends only the difference.

**That connection used to be thrown away after every single write.** The moment a write was saved,
the portal discarded the writer's copy so that the next write would be sure to start from fresh
state, and the next write paid for a brand-new connection: a new subscription, a new round trip
for the node's current state, new plumbing on both ends. On a node written once, that is invisible.
On a node written constantly it is not — the record of your own logins is written on every cold page
load, and one of them had been rebuilt more than six thousand times.

Each rebuild had to deliver the node within 30 seconds or the write was abandoned. Since 10 August
that happened 414 times on the production portal, always on the busiest nodes: the login record, and
pages people come back to. The visible effect was quiet — a visit that did not count, a
last-accessed time that did not move — and the log said only that no state had arrived in time.

## What changed

The connection now **stays open**. Every saved write announces its version number, and the next
write simply waits until its copy has caught up to that version before computing its change — which
normally takes no time at all, because the owner sends the new version down the connection that is
already open. A copy that is provably behind is still thrown away and rebuilt, once, exactly as
before; a delete, a re-creation or a restart of the owner still resets the connection too.

Nothing about what is written changes: a write still starts from the newest saved state, and two
writers still cannot overwrite each other's changes. What goes away is the rebuild — measured on a
test mesh, six writes to one node used to open five new connections after the first and now open
none.

The details, including why the obvious shortcut (keeping any connection that *looks* healthy) loses
writes, are in [Live Mirrors and the Change Feed](/Doc/Architecture/LiveMirrorsAndTheChangeFeed).
