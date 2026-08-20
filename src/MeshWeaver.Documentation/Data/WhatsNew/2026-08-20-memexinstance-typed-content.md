---
Name: Saved mesh connections resolve reliably
Category: Fix
Description: A saved mesh connection's details now resolve on every hub, so clients that read them as typed content no longer see them as empty.
Icon: Sparkle
Order: -20260820
---

# Saved mesh connections resolve reliably

The details of a saved mesh connection (a Memex Instance) could arrive as untyped data on hubs
that stream or query those nodes, which made typed readers — such as a client reconnecting to your
remembered meshes — see nothing at all. The type is now registered mesh-wide, so every reader
resolves it.
