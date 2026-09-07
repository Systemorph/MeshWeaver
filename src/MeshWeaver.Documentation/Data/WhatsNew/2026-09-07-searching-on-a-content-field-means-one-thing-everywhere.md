---
Name: Searching on a content field means one thing everywhere
Category: Fix
Description: Filtering on a field that lives inside a node's content — status, email, compilationStatus — worked when the search ran against the database and quietly matched nothing when the same filter ran in memory. Live results that should have refreshed did not, sorting on such a field did nothing, and a "not this" filter returned exactly what it was meant to exclude.
Icon: Search
Order: -20260907
---

# Searching on a content field means one thing everywhere

A mesh query filters on fields. Some of them belong to the node itself — its name, its type, when
it was last modified — and some live inside the node's content, which is where most of what you
actually care about sits: a story's `status`, a person's `email`, a type's `compilationStatus`.

The search box has always accepted both, and the hint under it says so: `nodeType:Story
status:Open`. What it did with the second half depended on who was answering.

Most searches go to the database, and there `status:Open` did the right thing. But the platform
answers some questions without going to the database — deciding whether a result already on your
screen still belongs there when something changes, and putting a merged set of results back into
order. Those answers were produced by a second piece of code that only knew about the node's own
fields. Asked about a content field, it did not say "I don't know". It said "no match".

Three things followed, and none of them looked like a fault.

**Live results stopped keeping up.** A list filtered on a content field would not notice a node
that had just come to match it, or one that had stopped. Nothing errored; the list simply sat
there, correct as of when you opened it.

**Sorting by a content field did nothing.** `sort:CreatedAt-desc` — the ordering behind the
notification bell — asked for a field that half of the machinery could not see, so the sort was a
no-op at the last step and the order you got was whatever order the results arrived in.

**A "not this" filter returned exactly what it excluded.** `-status:Decommissioned` reads as
"everything except the decommissioned ones". Because the field could not be seen, no node was ever
judged to match `Decommissioned`, so none was excluded — and the filter returned the whole list,
including the very things it was written to leave out.

The same gap made a filter on a content field return nothing at all on a development machine or in
a test run, where the database is not involved. So a search that worked when you tried it on the
live portal could come back empty locally, with no way to tell that from "there is nothing to
find".

Both halves of the platform now read a field the same way: the node's own field if it has one, and
otherwise the content field of that name — which is exactly what the database has always done. A
field that exists on the node but happens to be empty still reads as empty rather than quietly
reaching into the content, and where a node and its content both carry a field of the same name,
the node's own still wins. Writing `content.status` instead of `status` remains the clearest way to
say what you mean, and it has always worked everywhere.
