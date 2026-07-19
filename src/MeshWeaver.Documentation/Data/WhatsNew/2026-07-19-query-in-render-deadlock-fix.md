---
Name: Layout areas that query while rendering no longer wedge the portal
Category: What's New
Description: A layout area that fetches mesh data during its render is now safe — the render subscribes off the hub turn, so it can't deadlock the node
Icon: Sparkle
---

# Query-while-rendering is now safe

A layout area whose view fetches mesh data during its render — a `Query`, a workspace read, any hub round-trip — used to be able to deadlock the node that hosts it: the render subscribed on the hub's own turn, and the round-trip needed that same turn to come back. On startup, many such areas rendering at once could starve the thread pool and wedge a whole portal.

The render pipeline now subscribes off the hub turn, so the round-trip is free to route and return. Query-while-rendering is safe, existing pages need no change, and deployed nodes become safe without a recompile. The SocialMedia Post example was also moved to the recommended pattern (loading its data through a virtual data source).
