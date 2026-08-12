---
Name: A live list can no longer flash a node you cannot open
Category: Fix
Description: A live search or catalog list could briefly show a node the viewer has no permission to read — including its content — when someone else edited it, before dropping it again on the next update.
Icon: Sparkle
Order: -20260812
---

# A live list can no longer flash a node you cannot open

A list that stays live — a search page, a catalog, a folder view — is built twice over: once when
the page loads, from the full answer to your query, and then continuously, as changes come through.
The load side has always applied your permissions, so a node you may not read simply is not in it.

The live side did not. It admitted whatever had just been written under the area you were watching,
checking only whether it matched your search words and filters — never whether you were allowed to
see it. So when someone edited a node you have no access to, and that node sat under an area your
list covered, it appeared in your list as a new row, carrying its name and its content. The next
update took it back out again, because that frame was rebuilt from the permission-checked answer.
A row you were never entitled to, visible for as long as nothing else changed.

The live side now shows only what the permission-checked read returns, for every update, not just
the first one. Nothing else about live lists changes: an edit to something you *can* see still
arrives immediately.

This affected portals backed by the in-memory, SQLite, or file-system stores. On PostgreSQL — which
every hosted portal uses — the main content queries are served by a different, unaffected component,
so the exposure there was limited to scoped queries over attached records.
