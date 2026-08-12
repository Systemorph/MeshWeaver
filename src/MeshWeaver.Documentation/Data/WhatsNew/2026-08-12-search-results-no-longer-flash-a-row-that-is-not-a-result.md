---
Name: Search results no longer flash a row that is not a result
Category: Fix
Description: A live search list could briefly show — and then drop again — an entry that the search itself never matched, such as an internal record attached to one of the results, redrawing the whole list twice for something that was never in it.
Icon: Sparkle
Order: -20260812
---

# Search results no longer flash a row that is not a result

A search page stays live: you leave `nodeType:Thread` open, other people keep working, and the list
updates itself as things change. That list is built twice over, by two different pieces of code —
once when the page loads, from the full answer to your query, and then continuously, from the
stream of changes as they happen.

The two did not agree on what counts as a result. The load side knows that some records are
**attached to** a node rather than being content of their own — an activity log, an access grant, a
comment thread — and leaves them out, exactly as you would expect: you searched for pages, not for
the bookkeeping hanging off them. The live side had no such rule. Any change under the area you were
searching was admitted, and for the plainest kind of search — "everything under here", with no
search word and no filter — that meant *every* change, bookkeeping included.

So a record like that would appear as a new row in your results, and then vanish again on the next
change that came through, because the moment the list was rebuilt from the full answer it was
correctly excluded once more. Two redraws of the entire list, a row that flickers in and out, and
nothing you did explains either one. On a busy area it happened repeatedly.

Both sides now apply the same rule, so the live list can only ever show what the search actually
matched. The change had a second victim worth naming: our own test for search-list redraws measures
an exact redraw count under load, and these phantom rows were adding two — which made the test fail
at random and, because it runs on every proposed change, reported unrelated work as broken.
