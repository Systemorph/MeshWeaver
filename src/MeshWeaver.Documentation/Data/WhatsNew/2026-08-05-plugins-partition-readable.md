---
Name: Installed plugins visible on a fresh mesh
Category: Fix
Description: The plugin catalog's install records now ship with a read-only public access policy, so catalog views can show what is installed on a freshly bootstrapped mesh.
Icon: Sparkle
Order: -20260805
---

# Installed plugins visible on a fresh mesh

The plugin catalog's install records (the `Plugins` partition) now ship with a read-only public
access policy, the same shape as the agent, skill and model catalogs. On a freshly bootstrapped
mesh, the catalog's installed-state queries now return the installed plugins for real users
instead of being denied. The partition stays read-only — install records are still written
exclusively by the platform itself during installs.
