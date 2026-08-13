# MeshWeaver.GitSync

Git synchronization for MeshWeaver content. Syncs mesh node trees with git repositories in
both directions — pull repository content into the mesh, push mesh edits back as commits —
configured per space via a `_GitSync` node.

## Features

- `GitCli` / `GitCredentials` — credentialed git operations against the configured remote
- `ActivityRunner` — sync runs as activity-control-plane operations with progress and logs
- `AiContentDiskWriter` / `ContentAssetMapper` — maps node content (markdown, code, assets) to repository file layout
- `GitHistoryTab` and sync menu/areas in the portal

## Links

- [MeshWeaver repository](https://github.com/Systemorph/MeshWeaver)
- [Documentation](https://memex.meshweaver.cloud/Doc)
