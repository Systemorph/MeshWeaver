---
Name: Plugin installs are minutes faster — nodes now land in bulk
Category: Feature
Description: Installing a node-repo plugin (a course, a module) now writes its new nodes in a few transactional batches instead of one at a time.
Icon: Sparkle
Order: -20260805
---

# Plugin installs are minutes faster

Installing a plugin from a node repository — a course, a domain module — used to write every
node one at a time, each write waiting on its own round-trip and a 100 ms visibility poll. A
course-sized package of a few hundred nodes paid that tax on every install, on every mesh.

New nodes now land in a handful of transactional bulk batches (compile sources, then types,
then content), and each batch is visible the moment it commits — no polling. Everything that
needs the careful one-at-a-time path keeps it: the package root, access grants and other
satellites, and updates to nodes you already have. Re-installing an unchanged package still
writes nothing.
