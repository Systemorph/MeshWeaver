---
Name: A search that excludes a folder no longer searches only that folder
Category: Fix
Description: Writing -path:Something in a query means "everywhere except there". The query was being read as the exact opposite — the excluded location became the only place searched — so the results were either rows from precisely the folder you excluded, or nothing at all, with no error either way.
Icon: Search
Order: -20260914
---

# A search that excludes a folder no longer searches only that folder

A query can exclude a location by prefixing it with a minus: `-path:Archive` means *everywhere
except Archive*, and `-path:Archive|Drafts` means *except either of those*.

**The exclusion was being read as its opposite.** The minus was dropped and the location kept, so
the query was treated as though it had asked for that location — and because a location is also what
decides *which store to look in*, the search was sent to the very partition it had been told to skip.
The result was one of two wrong answers: rows from exactly the folder you excluded, or nothing at
all. Neither raised an error, so a query that returned nothing read as "there is nothing there".

**A negated location is now a filter, not a destination.** `-path:Archive` narrows the rows —
"every row whose path is not Archive" — and the query looks wherever the rest of it says to look. A
query whose only location was the excluded one now says it is not specific enough, and asks for a
place to search or an explicit request to search everywhere, instead of silently searching the one
place that was ruled out.

Ordered comparisons on a location (`path:>"m"`, used for paging) were already treated this way for
the same reason. Ordinary positive forms — `path:Acme/Docs`, `path:A|B` — are unchanged.
