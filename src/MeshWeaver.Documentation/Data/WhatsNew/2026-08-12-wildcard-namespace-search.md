---
Name: Wildcard namespace searches find their results everywhere
Category: Fix
Description: A search like `namespace:*/Source` now returns the same results whichever storage backend answers it, and live views built on one refresh again when matching content is added.
Icon: Sparkle
Order: -20260812
---

# Wildcard namespace searches find their results everywhere

A search that uses a wildcard in its namespace — for example `namespace:*/Source scope:subtree`,
which looks for content in a particular kind of folder across every space — could quietly come back
empty, or miss newly added items, depending on which store happened to answer it.

The cause was a mismatch in how the wildcard was written down internally: the search text was
translated into a database-specific form that the non-database parts of the platform did not
recognise, so they matched nothing at all rather than reporting a problem. Searches that span
several levels of folders were affected twice over, because only the first wildcard in a pattern was
being honoured.

Wildcards now mean one and the same thing everywhere, so these searches return identical results no
matter which backend serves them. Live views built on such a search also start updating again when
new matching content appears — previously they could stay stale indefinitely.
