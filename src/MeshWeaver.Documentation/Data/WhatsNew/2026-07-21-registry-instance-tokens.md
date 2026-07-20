---
Name: Plugin registry is now gated to registered instances
Category: What's New
Description: The /api/plugins registry surface requires an issued instance token — anonymous access is reserved for local-dev registries with no tokens configured.
Icon: ShieldKeyhole
---

# Plugin registry is now gated to registered instances

The plugin registry no longer serves its catalog publicly. An installation registers with the
registry and receives an **instance token**; the Plugin Catalog sends it on every request, and the
registry answers only requests carrying a valid token.

For operators: on the registry, list the issued tokens under `PluginCatalog:RegistryTokens`; on each
consuming installation, set `PluginCatalog:RegistryToken` (or `Registries:N:Token` per registry) to
the token it was issued. A registry with **no** tokens configured keeps answering anonymously — the
local-dev and test-stub mode — so nothing changes for development setups.
