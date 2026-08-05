---
Name: Installed plugins visible on a fresh mesh
Category: What's New
Description: The plugin catalog's install records are now readable by every signed-in user, so the Plugin Catalog settings tab shows what is installed on a freshly bootstrapped mesh.
Icon: Sparkle
---

# Installed plugins visible on a fresh mesh

The plugin catalog's install records (the `Plugins` partition) now ship with a read-only public
access policy, the same shape as the agent, skill and model catalogs. On a freshly bootstrapped
mesh, a platform admin opening the Plugin Catalog settings tab now sees the installed plugins
instead of an access denial. The partition stays read-only — install records are still written
exclusively by the platform itself during installs.
