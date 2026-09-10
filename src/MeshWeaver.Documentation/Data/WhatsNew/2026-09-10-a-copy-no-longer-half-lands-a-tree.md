---
Name: A copy no longer half-lands a tree
Category: Fix
Description: Copying a folder, package or node type could quietly leave part of it behind — and still report how many nodes it copied.
Icon: Sparkle
Order: -20260910
---

# A copy no longer half-lands a tree

Copying a subtree — from the Copy dialog, from "copy to home", from an import, or through an agent's
`copy` tool — used to report the number of nodes it managed to write, and that number said nothing
about whether the copy was whole.

Two things could make it short. If part of the source was **not visible to you** — a branch someone
else has not shared, or the gated part of an installed package — the copy simply did not see those
nodes, carried the rest, and reported success. And if one write in the middle was **refused**, the
copy stopped there but the nodes already in flight landed anyway, so the result could contain a file
sitting under a folder that never arrived. Either way you were handed something that looked like a
finished copy and was not. One node type reached a personal workspace this way without any of its
source files, and then failed to build every time the portal restarted, for four days, complaining
about symbols that were simply missing.

A copy now checks itself, before and after. Before it writes anything it establishes how big the
source subtree really is — not just how much of it you can see — and if it cannot carry all of it, it
**refuses and writes nothing at all**, telling you how many nodes would have been left behind rather
than handing you a broken half. (It deliberately does not name those nodes: they are the ones you are
not entitled to see.) It then writes parents before their children and stops descending at the first
level that fails, so what does land is always a properly rooted tree, never a file under a missing
folder. And it reports every node that did not land — the ones that were refused and the ones it
therefore never attempted — instead of naming one and staying silent about the rest.

Copies that were already complete behave exactly as before, including the count they report.
