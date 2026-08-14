---
Name: Diagrams, math, and code highlighting work on locked-down networks
Category: Fix
Description: Mermaid diagrams, math formulas, code highlighting, and markdown styling no longer load from public CDNs — they ship with the portal, so they render on networks that block external scripts.
Icon: Sparkle
Order: -20260814
---

# Diagrams, math, and code highlighting work on locked-down networks

Mermaid diagrams, math formulas, syntax-highlighted code blocks, and markdown styling used to load
their libraries from public CDNs at view time. On corporate networks that block those hosts, the
content silently rendered without diagrams or highlighting. These libraries now ship with the
portal itself — pinned versions, no external requests — so pages render the same everywhere.
