---
Name: A momentary database blip no longer blanks a page, fails a delete, or drops a package
Category: Fix
Description: Transient Postgres conditions — a dropped connection, a read timeout, a deadlock, a concurrent schema creation — are now retried where they occur instead of surfacing as an error panel on a dashboard area, a delete that refuses with no explanation, or a package that silently never installs.
Icon: Bug
Order: -20260825
---

# A momentary database blip no longer blanks a page, fails a delete, or drops a package

Some database conditions are momentary by design. PostgreSQL picks a "victim" when two operations
deadlock, rolls it back cleanly, and expects it to be tried again; a connection can drop mid-read
during a failover; two servers creating the same thing at the same instant means one of them simply
arrives second. None of these are faults — but three places in the platform treated them as if they
were, and each one showed up as something a user could see.

**Dashboard areas no longer fail on a blip.** A cross-partition search that hit a dropped
connection, a read timeout, or a deadlock used to fault the whole area — Timeline, Comments,
Preview and Catalog all put an error panel where their content should be. Those conditions are now
retried briefly before anything is reported. A genuine failure still surfaces as one: the area says
so rather than quietly rendering as if there were no comments, no timeline entries, and nothing in
the catalog.

**Deleting a page or folder no longer loses a race.** Working out everything inside a subtree runs
alongside whatever else is writing to it, so it can be chosen as the deadlock victim. That aborted
the delete with nothing removed and no hint that simply trying again would have worked. It is now
retried, so the delete just happens.

**Packages install even when several start at once.** When a server started up and installed
several packages together, two of them creating their storage at the same moment could collide, and
the loser was skipped — the server came up looking healthy while quietly missing that package until
the next restart or a manual install. Different servers in the same deployment could end up with
different sets of packages. That collision is now recognised for what it is and retried, so every
package finishes installing.
