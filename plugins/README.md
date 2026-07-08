# MeshWeaver.Plugins — catalog packages

This folder holds **installable packages** for the MeshWeaver plugin catalog. The catalog
(`MeshWeaver.PluginCatalog`, in core) browses the folders here and installs each into a running
mesh by picking a **git commit + folder** — no rebuild, no NuGet.

Each package folder under `catalog/` carries a `package.json` manifest:

```json
{ "id": "welcome-note", "name": "Welcome Note", "kind": "content",
  "targetPartition": "Welcome", "version": "1.0.0" }
```

- **`kind: content`** — the folder's files (markdown, JSON, agent/skill `.md`) are imported into the
  target partition (incremental upsert, nothing else is touched).
- **`kind: code`** — the manifest's `nodeTypeConfiguration` + `Source/*.cs` become a NodeType that the
  mesh **compiles live** (Roslyn) via its existing compile/release flow — a custom type in a running
  mesh, no rebuild.

## Sample packages (`catalog/`)

| Folder | Kind | Installs |
|---|---|---|
| `welcome-note`, `getting-started` | content | a markdown page into a space |
| `echo-agent` | content | a sample Agent into the `Agent` partition |
| `hello-skill` | content | a sample Skill into the `Skill` partition |
| `hello-widget` | code | a runtime-compiled custom NodeType |

## Configuring the catalog

Point the portal at this folder via `PluginCatalog:SourceRepoPath` (the local git checkout) — the
catalog seeds a `Plugins` space with a `Plugins/catalog` node that browses `catalog/` at `HEAD`. A
remote GitHub repo works too (the catalog uses GitSync's fetch), so a deployed portal with no source
tree can still browse/install.

_Extracted feature modules (Courses, Speech, …) may live here as their own projects later; this PR
ships the catalog + sample packages._
