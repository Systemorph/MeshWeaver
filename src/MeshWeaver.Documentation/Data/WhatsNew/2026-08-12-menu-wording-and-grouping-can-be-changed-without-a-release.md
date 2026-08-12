---
Name: Menu wording and grouping can be changed without a release
Category: Feature
Description: The node menu's text, icons, order, grouping and visibility now live in an editable node, so renaming or tidying a menu entry takes effect immediately instead of waiting for a full build and rollout.
Icon: Sparkle
Order: -20260812
---

# Menu wording and grouping can be changed without a release

Renaming a menu entry used to be a code change. The label lived in a compiled provider — or in a
translation file baked into the application itself — so a one-word fix meant a full build, a new
image and a rollout before anyone saw it. Half an hour to rename a button is enough friction that
menus quietly accumulate entries nobody prunes and wording nobody fixes.

**The menu's presentation is now content you can edit.** A menu catalog node holds, for each entry,
its text in every language, its icon, its position, which sub-menu it groups under, and whether it
appears at all. Change it and the next render picks it up — for the web portal, the React clients
and the native app at once, because all of them read the same menu.

What did **not** move is who may see each entry. Whether an action is offered at all — whether you
have permission for it, whether it applies to this kind of node, whether synchronization is set up —
is still decided by the application, and the catalog cannot override that. It re-words, re-orders,
groups and hides; it can never add an entry or reveal one you were not already entitled to. So
editing the menu stays a presentation decision and can't quietly become a permissions one.

A mistake in the catalog costs you that one entry, never the menu. An entry naming an action that
does not exist is ignored; a grouping that points nowhere leaves the entry where it was; unreadable
content falls back to the built-in menu. Each of those is written to the log naming exactly what was
skipped and why, rather than disappearing silently — and if there is no catalog at all, you get the
standard menu, which is what every portal starts with today.
