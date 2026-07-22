---
Name: Interactive kernels now release their memory
Category: What's New
Description: Script assemblies from executed code cells unload when their session ends — long interactive sessions and bulk course runs no longer degrade the portal.
Icon: Memory
---

# Interactive kernels now release their memory

Every executed code cell compiles to an in-memory assembly. Until now those assemblies could
never be unloaded — a portal running many interactive pages (a workshop, a course walkthrough,
a long authoring session) accumulated them for its whole lifetime, slowing down progressively
until navigations timed out.

Kernel sessions now load their script assemblies into a collectible context that is released
when the session ends, and dynamically compiled node types were hardened the same way. Memory
returns to baseline after your notebook pages close; long and busy sessions stay fast.
