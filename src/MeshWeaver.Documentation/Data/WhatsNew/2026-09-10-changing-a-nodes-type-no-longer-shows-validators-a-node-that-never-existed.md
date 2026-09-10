---
Name: Changing a node's type no longer shows validators a node that never existed
Category: Fix
Description: An update that changes a node's NodeType used to hand its validators an "existing" content built by reading the node's old bytes as the NEW type — a well-formed record of a state the node was never in. The update pipeline now types the existing snapshot only while the NodeType is unchanged, and says so in the log when it cannot type it at all.
Icon: ShieldCheckmark
Order: -20260910
---

# Changing a node's type no longer shows validators a node that never existed

Renaming a node's type in place is a supported repair — `patch` refuses `nodeType` outright, so a
full `update` is the only way to fix a node that carries the wrong one. Before this change, that
repair quietly corrupted what the update's own validators were looking at.

The update pipeline gives validators the existing node and the proposed one **typed alike**, so a
check written as "the proposed version must not be lower than the existing one" can actually compare
them. It did that by deserialising the existing snapshot as the *proposed* content's type — correct,
and stated as such in the code, **while the NodeType is the same**. On a retype it is not: the
proposal's type describes what the node is moving *to*.

Because the platform's JSON reader skips members it does not recognise, that conversion usually
**succeeded**. A node stored as `{"title":"Original","sequence":5}` retyped towards a record with
`Title` and `Reason` came out as `("Original", null)`: a perfectly well-formed value of the new type,
carrying the old title, silently dropping `sequence`, and inventing a `null` `Reason`. Nothing threw
and nothing was logged. Validators then compared the proposal against that ghost and answered on it.
Where the shapes did not line up the conversion threw instead, the snapshot stayed untyped, and every
typed comparison skipped its own check and answered *valid* — a refusal that never happened.

**Now the pipeline types the existing snapshot only when the NodeType is unchanged.** On a retype it
hands validators the snapshot exactly as it read it, and when that snapshot is untyped it logs a
warning naming both NodeTypes, the node, and the fact that a typed comparison on that side will skip.
Nothing else about the update changes: the retype still lands, and a validator that wants to refuse
one refuses it on the NodeType, which both sides always carry.

There is nothing better the pipeline could offer, and that is the point: by the time it sees the
snapshot, the node-stream read has already tried the exact recovery — the mesh-wide content-type
registry, keyed on the node's **own** NodeType — and has an outstanding wait for that type to become
known. A snapshot still untyped at this point is untyped because nothing in the process can type it.
Saying so is worth more than inventing an answer.

The full mechanism, including the case that made the wrong conversion silent, is in
[Update Validators See Typed Content](/Doc/Architecture/UpdateValidatorsSeeTypedContent).
