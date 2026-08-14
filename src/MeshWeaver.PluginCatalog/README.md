# MeshWeaver.PluginCatalog

The MeshWeaver plugin catalog and installer — the mesh's app store. Discovers content
packages from git-based registries, verifies them, installs them into partitions, and keeps
them updated.

## Features

- `GitPackageSource` / `GitHubPackageSource` — package sources over git repositories and registry endpoints
- `InstanceCombo*` — assembly and verification of a deployment's installed-package set
- `InstalledPackageRepairService` — reconciles installed content against the package
- `InstanceAutoRegistrationService` — registers an install with its registry
- `CatalogLayoutAreas` + coupon/admin settings — the store UI in the portal

## Links

- [MeshWeaver repository](https://github.com/Systemorph/MeshWeaver)
- [Documentation](https://memex.meshweaver.cloud/Doc)
