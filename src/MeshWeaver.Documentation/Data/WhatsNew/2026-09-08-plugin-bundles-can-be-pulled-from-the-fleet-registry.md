---
Name: Plugin bundles can be pulled from the fleet's registry
Category: Feature
Description: An installation can now fetch a plugin's compiled bundle from the fleet's own container registry, by digest, with every byte verified — using the same instance credential it already presents for the plugin registry and for its images. The registry's indexes name where each bundle lives; bundles the registry has not pushed keep arriving over HTTP exactly as before.
Icon: Box
Order: -20260908
---

# Plugin bundles can be pulled from the fleet's registry

A plugin's compiled bundle — its prebuilt assemblies and its module — used to reach an installation
one way only: the plugin registry served the bytes over HTTP, and the installation trusted what
arrived. The fleet's own container registry, `cr.meshweaver.cloud`, now holds the same bundles as
OCI artifacts, addressed by digest, and an installation can pull them from there.

Both of the plugin registry's indexes — the bundle index and the catalog — now carry an `artifact`
per package: the digest-addressed reference of that bundle in the container registry, or nothing
when the registry has not pushed it. When the reference is there, the installation fetches the
bundle's manifest by that digest and the bundle itself by the digest the manifest names, and
refuses anything that does not hash to what was asked for — so what lands is provably the archive
the publication sealed, not merely something served under its name. When the reference is absent,
the HTTP route is taken exactly as before; nothing about an existing registry changes until it
starts recording what it pushes.

The credential is the one the installation already holds. Its plugin-registry token is what it
presents at the container registry, the same way its self-updater already presents it to list
images — one client, one credential, for images and bundles alike. The bytes then enter the same
landing path as before: the link probe at placement still decides whether a module may load, and
nothing about the identity rule for prebuilt assemblies changes.

See [Plugin Registry — Bundle bytes from the registry](/Doc/Architecture/PluginRegistry) for the
field and the consumer's rule, and [Plugin Bundles in the Registry](/Doc/Architecture/PluginBundlesInTheRegistry)
for the design.
