---
Name: Sorting and paging a search across the whole mesh
Category: Fix
Description: A search that spans every partition failed outright on "sort:path", and its paging cursor "path:>…" matched nothing at all — so a walk over everything returned the first page and reported success. Both halves now work.
Icon: Search
Order: -20260825
---

# Sorting and paging a search across the whole mesh

A query with no path or namespace to pin it — `nodeType:User`, say — searches every partition at
once. Two things you would reach for immediately did not work there.

`sort:path` failed with a database error. The union that gathers rows from every partition simply
did not carry the `path` column, so there was nothing to sort by, however the underlying tables were
built.

The second half was worse because it was silent. `path:>"…"` is how you ask for "the next page after
this one", and the query language was folding it into the *anchor* — reading it as "the node whose
path is exactly `…`", which nothing is. The answer came back empty, with no error. A routine that
walks everything page by page therefore returned page one, saw an empty page two, and reported that
it had finished. Two maintenance sweeps did exactly that in production before anyone noticed.

The cross-partition union now exposes the columns you can sort by, and a comparison on `path` stays
a comparison instead of being mistaken for a location. Paging over the whole mesh works, and a walk
that ends is a walk that really reached the end.
