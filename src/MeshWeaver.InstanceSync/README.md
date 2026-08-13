# MeshWeaver.InstanceSync

Cross-instance synchronization for MeshWeaver portals. Pairs two portals via OAuth and keeps
selected partitions in sync between them — the mechanism behind mirroring content across
deployments.

## Features

- `InstanceOAuthService` — pairing and token exchange between portals
- `InstanceSyncCoordinator` — drives sync runs over the configured partitions
- `InstanceSyncPartitionSyncSourceProvider` — partition content as a sync source
- `InstanceSyncLayoutArea` + menu integration — configure and monitor sync in the portal

## Links

- [MeshWeaver repository](https://github.com/Systemorph/MeshWeaver)
- [Documentation](https://memex.meshweaver.cloud/Doc)
