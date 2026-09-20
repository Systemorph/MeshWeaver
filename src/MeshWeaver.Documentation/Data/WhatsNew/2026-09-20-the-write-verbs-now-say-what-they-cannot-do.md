---
Name: The write verbs now say what they cannot do
Category: Feature
Description: Changing a node can be said four ways — a C# lambda, a JSON patch, the whole entity, or nothing that fits. Which one is right depends on the context, and until now the platform documented the choice nowhere and described one mode that does not exist. The design is written down, the verbs say where their limits are, and the one mode that was advertised but refused now says so.
Icon: Edit
Order: -20260920
---

# The write verbs now say what they cannot do

Every change to a node has to be *expressed* before it can be applied, and there are only four ways
to say it: a C# lambda, a JSON patch, the complete entity, or something that fits none of them.
Which one is correct is a property of the situation, not a preference — and choosing wrong has two
quiet failure modes.

The first is writing the whole entity when you meant to change one field. Every field you did not
mention is still written, so a colleague's concurrent edit is reverted rather than preserved. The
second is a count. Anything of the form *"add one to what is there"* has to be worked out against
the node as its owner currently holds it; computed from a value you read a moment earlier, it loses
an increment as soon as a second writer exists.

## What changed

The design is now written down, as [Expressing a Write](/Doc/Architecture/ExpressingAWrite): the
four shapes, which context may use which, and how the clear one is meant to become the one that can
travel over a network.

The verbs themselves now carry their own limits. The full-replacement update says that omitted
fields are still written. The patch verb says it replaces a text field wholesale and cannot express
a count. The text-edit verb says why it matches on surrounding text rather than on a line and column
— a position is stale the moment anyone inserts a character above it, and a change aimed at a stale
position lands in the wrong place without reporting anything, which is the only silent-corruption
shape in the whole design.

## One mode was advertised and never existed

The platform's combined create-or-update verb offered a patch mode, described as the way to handle
running counts. It was never implemented: every patch sent to it was refused, and nothing in either
repository had ever tried. The description is corrected rather than quietly deleted, because the gap
it points at is real — there is still no way to both create a node when it is missing *and* add one
to a value it already holds, which is the cause of a long-standing failure in activity tracking.
Saying so is more useful than a summary that reads as though the problem were solved.

## Deliberately unchanged

No write behaves differently than it did yesterday. This is a description catching up with the
implementation, plus the design of what should close the remaining gap — deliberately separated, so
that the part which is merely written down is not mistaken for the part that is built.
