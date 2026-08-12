---
Name: The portal stops growing every time code is rebuilt
Category: Fix
Description: Each rebuild of a piece of in-portal code left memory behind that never came back, so a busy morning of edits could bloat a portal until it restarted itself. It now hands most of that memory back.
Icon: Sparkle
Order: -20260813
---

# The portal stops growing every time code is rebuilt

Code that lives inside the portal is compiled by the portal itself, and it is recompiled whenever
it changes — when you edit it, and again for everything a repository sync brings in. Every one of
those rebuilds used to leave a permanent footprint behind: roughly twenty megabytes that no amount
of tidying could reclaim.

On a quiet day nobody noticed. On a busy one — a morning of merges, each triggering a sync, each
sync rebuilding the pieces it touched — a portal could grow by hundreds of megabytes a minute until
it hit its ceiling and restarted, taking every open page with it.

The memory was not the compiled code, which is tiny. It was bookkeeping. Reading or writing any
single item asks the platform for a live view of it, and each of those requests was quietly
building a *new* view rather than reusing the one already open — and each new view brings a small
private world with it that only goes away when the whole item does. A single rebuild opened about
sixty of them; nothing ever closed one.

Views of items belonging to *other* parts of the mesh were already shared this way. Views of an
item's own data now are too. A rebuild costs less than half of what it used to, and — a pleasant
side effect — a portal at rest now carries roughly a quarter of the internal machinery it used to,
because the same waste was being paid on ordinary page loads and saves, not only on rebuilds.

Some growth remains, from a second cause with the same shape, and it is being tracked separately.
The measurement that found this one now runs on every build, so the number cannot creep back up
unnoticed.
