---
Name: Startup no longer stalls on large meshes
Category: Fix
Description: The boot-time permission self-heal now runs in small batches, so large installations start reliably instead of timing out during migration.
Icon: Sparkle
Order: -20260824
---

# Startup no longer stalls on large meshes

The permission self-heal that runs at every startup used to sweep all partitions in
one long operation. On large installations that single operation could outlast the
database timeout, leaving the migration stuck in a restart loop while smaller
installations sailed through.

The sweep now runs as a series of small batches, each finishing quickly and
releasing its lock before the next begins. Startup time no longer grows into a
timeout as your mesh grows, and several servers booting at once no longer queue
behind one long sweep.
