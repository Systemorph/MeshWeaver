---
Name: Internal parts are no longer built inside each other
Category: Fix
Description: Data-enabled hubs and code-cell hubs no longer construct sub-hubs from inside their own construction, so a shutdown races one thing instead of a tree.
Icon: Sparkle
Order: -20260822
---

# Internal parts are no longer built inside each other

Everything in the mesh runs on a small internal component that is created on demand. Two of those
components were doing part of their setup *during their own construction* — and that setup created
further components, which created further ones again.

Nothing failed visibly, but it made shutdown harder than it needed to be: stopping something while it
was being created meant racing a whole tree of half-built parts rather than a single one, and that
race is the shape behind a long line of shutdown bugs.

The setup now runs immediately after construction instead of inside it. Nothing about the order in
which your pages, data and code cells become ready changes — the component still finishes its setup
before it accepts any work.
