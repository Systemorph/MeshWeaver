---
Name: Imported content no longer arrives before its type
Category: Fix
Description: A repo or catalog that ships an instance of a type it introduces now lands in a single pass — the NodeType is written before anything that names it, instead of being refused and retried with the same ordering forever.
Icon: ArrowSyncCheckmark
Order: -20260901
---

A Space, catalog or plugin repo that ships both a **NodeType and instances of it** now imports in
one pass. The import writes the type node — and the type's `Source`/`Test` files — before anything
that names it, so the content simply lands.

Previously the nodes were written in whatever order the source happened to enumerate them, so an
instance that came first was refused with *"NodeType 'X' is not registered"* and never appeared.
Worse, the refusal was sticky: because a node had failed to land, the Space's sync baseline was
deliberately held so a later pass would retry — and the retry re-ran the identical ordering and hit
the identical refusal. On the affected partitions the same node was refused **forty times in two
hours**, the content stayed missing, its views rendered empty, and every *later* commit to that repo
was blocked too, because the sync could never move past the commit whose node would not import.

A type that genuinely cannot be resolved — one defined by a source that is not installed — is now
reported instead of retried in silence. The import names the node and the missing type in its
activity log, finishes as a warning rather than a failure, and the Space's sync carries on with
everything else; the moment the source defining that type is installed, the node lands on the next
import. Cycles (two nodes that type each other) are named the same way rather than failing the whole
partition.

Full detail — the ordering rules, the cycle policy, and what happens to a type that comes from
another partition — is in [Import Write Ordering](/Doc/Architecture/ImportWriteOrdering).
