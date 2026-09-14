---
Name: A listing taken while the in-memory store re-indexes is complete
Category: Fix
Description: On an in-memory mesh — every test mesh, and the gate that checks a repository's content before publishing — a directory listing taken while the store was rebuilding its index could come back short, and a live query that took its first snapshot from that listing kept the short answer for good. One NodeType then failed to compile on files that were right there, and no plugin publication sealed for two days.
Icon: Checkmark
Order: -20260914
---

# A listing taken while the in-memory store re-indexes is complete

The in-memory store keeps an index of which files sit under which folder, so that listing a folder
does not mean scanning everything. When that index was rebuilt, it was cleared first and refilled
in place — and a write landing in the middle could declare it consistent before the refill was
done. A listing taken at that moment came back with part of the folder.

That would have been a passing glitch, except for what sits on top of it. A live query seeds itself
from one listing and then follows changes; a file written **before** the query existed never
produces a change, so a short first listing of a folder nobody writes to again stays short for as
long as the query lives. On the plugin gate that query is the one a NodeType's compile reads its
sources from. It answered ten of the eighteen files in a shared folder that had been written three
minutes earlier, the prepared assembly was refused against that short set, the rebuild from source
read the same short set and failed on the eight files it could not see, and the publication never
sealed.

**The index is now never cleared in place.** A rebuild assembles a fresh index and swaps it in
whole, so a listing always reads a complete one — the old until the swap, the new after. Writes and
the index move together, so the store can no longer look out of step with it, and a write that
lands during a rebuild goes into the new index too instead of waiting for it. Running portals use
Postgres and were never affected; every in-memory mesh was.
