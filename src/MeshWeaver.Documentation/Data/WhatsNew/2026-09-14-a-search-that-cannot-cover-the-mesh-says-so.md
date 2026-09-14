---
Name: A search that cannot cover the mesh says so, instead of answering zero
Category: Fix
Description: The search tool could answer "0 results" for nodes that were there — the query simply had not said which part of the mesh to look in, and the store answered from the part it happened to enumerate. Such a query is now refused with the two ways to fix it, and every answer says which partitions it covered.
Icon: Search
Order: -20260914
---

# A search that cannot cover the mesh says so, instead of answering zero

Ask the search tool for every node of some type — `nodeType:LogIncident`, say — and until now you
could get back a clean **0 results**, while the same nodes appeared the moment the query named the
space they live in. The store had not looked everywhere: a mesh is partitioned, some of those
partitions are never part of a mesh-wide read by design, and the ones you cannot read are left out
before the query even runs. The answer was honest about what it found and silent about where it had
looked — which reads exactly like "there are none".

That silence had a cost. The one check the platform runs before a deploy — *are any of the mesh's
types broken?* — was written in that very form, and its zero could mean "none are broken" or "none
in the places this query happened to reach".

**Two things change.**

A query that does not say where to look is now **refused**, and the refusal names both fixes: anchor
it to a partition (`namespace:Admin scope:descendants …`, or pass `basePath`), or declare that you
mean every partition you can read (`partitions:all`). A refusal is an answer you can act on; a zero
you cannot tell from "none exist" is not.

Every answer now carries **`coverage`**: whether the read was anchored, declared or routed, and the
list of partitions it covered — so a zero is always read against the places that were searched.
Where the store cannot yet say what it read, that list is explicitly `null`, never an empty list
that would read as "all".

And `basePath` now means what it says: searching *from* a path searches everything under it, not
only its immediate children.
