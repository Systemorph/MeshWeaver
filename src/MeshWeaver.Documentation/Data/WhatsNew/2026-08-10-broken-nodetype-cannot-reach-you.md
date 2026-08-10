---
Name: A broken NodeType can no longer reach you
Category: Fix
Description: A deploy that would break a working NodeType now stops itself, leaving the previous version serving, instead of shipping the breakage to your pages.
Icon: ShieldCheckmark
Order: -20260810
---

# A broken NodeType can no longer reach you

Every platform update recompiles the types your pages are built on. Until now, if
one of them stopped compiling against the new version, the update shipped anyway
— and you found out when a page hung or came up empty.

The update now checks its own work before taking over. A new version compiles
every type first, and it only starts serving once they are built. If a type that
was working stops working, the update stops there: the previous version keeps
serving your pages, unchanged, and nothing reaches you.

The check is deliberately careful about what counts as broken. If it simply
cannot get an answer about a type in time, that is not treated as a failure —
neither for that type nor for the ones built on top of it — so a slow check never
stalls an update on its own. Only a type that genuinely no longer compiles, and
that was working before, will hold an update back.
