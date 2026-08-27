---
Name: Deployments can require more modules
Category: Fix
Description: A deployment can now declare up to ten required modules; an index past the ceiling fails the build instead of silently reaching no container.
Icon: Sparkle
Order: -20260827
---

# Deployments can require more modules

A deployment states the modules it refuses to run without, and the chart used to carry only five
of those slots. A sixth entry was accepted everywhere it was written and then reached no container
at all — which is how one portal's MCP endpoint kept answering 404 while its configuration said it
was switched on. There is room for ten now, and an entry past that limit fails the build with the
limit named, rather than disappearing quietly.
