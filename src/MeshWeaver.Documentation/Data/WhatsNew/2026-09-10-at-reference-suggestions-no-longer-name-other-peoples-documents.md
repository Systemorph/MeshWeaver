---
Name: "@-reference suggestions no longer name other people's documents"
Category: Fix
Description: Typing @ and drilling into a space used to suggest nodes — with their titles — from spaces you have no access to, even though opening any of them was refused. Suggestions now show only what you are allowed to read.
Icon: ShieldCheckmark
Order: -20260910
---

# @-reference suggestions no longer name other people's documents

When you typed `@` in a chat box — or asked an assistant to complete a reference — and drilled into
a space by name, the suggestion list was built **without checking who you are**. Every node under
that space came back: its path, its type, and its **display name**.

Opening any of them was refused, correctly. But the list had already told you that
*"Pricing Comparison (Internal)"* and *"E-Mail-Entwurf an Thomas"* exist, who they sit under, and
what kind of thing they are. For a space you are not a member of, that is the table of contents of
someone else's workspace.

This affected the drill-down for **any signed-in person** — there was no admin gate on it, and no
setting that turned it off.

## What changes

**A suggestion is now filtered exactly like opening the node is.** The same check that decides
whether you may read a node decides whether it may be suggested to you. The two answers come from
the same place, so they can no longer disagree: if the portal would refuse to open it, it will not
offer it.

**Nothing you are entitled to has been taken away.** Your own space, spaces shared with you, and
anything public are suggested exactly as before — including their names. The filter is per node,
not per space, so a single document shared with you inside a space you otherwise cannot see still
appears.

**Nothing else about autocomplete changed.** Ranking, the `@`-shapes it understands, how fast the
list fills in, the list of node types kept out of suggestions — all unchanged.

## What this does not change

**It was never possible to read a node's contents this way.** The leak was the path, the name, the
type and the icon — never the document itself, and never anything you could open.

**Suggestions of the built-in catalogs are unaffected.** Roles, type definitions and the portal's
own shipped entries are catalog content, published to everyone on purpose, and are still suggested
to everyone.

**One diagnostic trick stops working, deliberately.** Because the old behaviour named nodes a
`Not found` was hiding, people had started using it to tell *"you may not read this"* apart from
*"this was deleted"*. It was a disclosure surface being used as an instrument. The supported way to
answer that question is now the portal's own system-side health report, which sees every space
regardless of who is asking — or the space owner.
