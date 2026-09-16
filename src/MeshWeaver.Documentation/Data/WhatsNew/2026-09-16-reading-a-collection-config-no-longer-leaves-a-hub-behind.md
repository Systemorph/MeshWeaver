---
Name: Reading a collection config no longer leaves a hub behind
Category: Fix
Description: Every read of a content collection's configuration missed the stream cache and left a permanent sync/ hub on the node hub — one measured node hub on production had accumulated eighteen of them for a single collection name. The reference that identifies the read now has value equality, so the reads share one stream.
Icon: Key
Order: -20260916
---

# Reading a collection config no longer leaves a hub behind

Reading which content collections a node has — what the file browser, the content areas and the
`collection:` path all resolve through — went through a `ContentCollectionReference`, and the
workspace caches one stream per reference so repeated reads share it. That cache was never hit.

`ContentCollectionReference` is a record whose only member is a list of collection names, and a
record with a collection member does not get value equality from the compiler: it compares the
**list object**, not its contents. Two reads of the same collection therefore produced two
references that were never equal, the cache missed every time, and each miss built a fresh
synchronization stream with its own hosted `sync/` hub — about 390 KB of DI scope, type registry and
serializer options — that lived until the node hub itself died.

**The reference now compares by collection name**, so the reads share one stream and the population
is bounded by how many distinct collections a hub has instead of by how many times anyone looked.
Nothing about what a read returns has changed. The two sibling references with the same shape,
`AggregateWorkspaceReference` and `CombinedStreamReference`, were given the same treatment.

It was found on a running portal. A live in-process census attributed all 311 of a replica's `sync/`
hubs to the stream that minted each one, found 63 duplicates of a value-identical
`(host, reference)` pair, and then separated the two possible causes by comparing the reference
objects already on the heap: 21 of the 22 duplicate groups compared equal — those are caller-specific
streams that retire with their caller — and one did not. That one was eighteen copies of
`(Doc/Architecture, collection/content)`.

A guard now refuses any workspace reference whose record equality would compare an array, a
non-string sequence or a `Lazy<>` by reference without a hand-written `Equals`. The full
measurement, and the census itself so it is not lost a third time, are in
[A Reference That Cannot Be a Key](/Doc/Architecture/AReferenceThatCannotBeAKey).
