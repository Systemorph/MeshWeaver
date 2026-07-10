---
Name: Plugins
Category: Architecture
Description: How MeshWeaver ships dynamic content — node types, modules, docs, AI content — from a git repo of node repos, installed via GitSync and compiled live on the mesh. No package format, no NuGet. A plugin is just mesh nodes.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z"/><path d="M3.27 6.96 12 12.01l8.73-5.05"/><path d="M12 22.08V12"/></svg>
---

# Plugins

A **plugin is not a package** — it is a repo of ordinary **mesh nodes**. MeshWeaver ships dynamic
content (node types, whole modules, docs, sample data, AI content) from a git repository of **node
repos**, installed with the framework's existing **GitSync** and compiled live on the mesh. There is
no manifest format and **no NuGet** — the node *is* the manifest, and the mesh versions it.

This is deliberately the framework replicated by nothing: it reuses [Static Repo
Import](/Doc/Architecture/StaticRepoImport), [Node Type
Compilation](/Doc/Architecture/NodeTypeCompilation), and the mesh's node/version machinery.

## A plugin is mesh nodes

A node repo is exactly the on-disk shape the sample partitions use — a `*.json` per node plus its
`Source/` (and `Test/`) C# — e.g. the Slide type:

```text
Slides.json                       the plugin's top-level Space node (the "package node")
Slides/Slide.json                 a NodeType node (Content = NodeTypeDefinition{ configuration })
Slides/Slide/Source/*.cs          the content type + layout areas — compiled live
Slides/Slide/Test/*.cs            the type's own tests — compiled together with Source
```

| Concept | Where it lives (node-native) |
|---|---|
| The "manifest" | the plugin's **top-level Space node** — its Content carries e.g. `minMeshVersion` |
| The "kind" (content vs code) | the child's **NodeType** — a NodeType-with-`Source/` vs plain content |
| The "version" | the **node's version**, mesh-tracked, bumped on every change — nobody hand-bumps a field |
| The "installer" | **GitSync** — `GitHubSyncService.ImportFromGitHub` / `StaticRepoImporter` |
| "what to install" | the **StaticRepoSync partition list** |

## Install = GitSync

Installing a plugin is importing its node repo into the mesh. `ImportFromGitHub(repo, ref, space, …)`
fetches the git folder and parses each file through `FileFormatParserRegistry` — which handles the
`.json` node files **and** the `.cs` Source, keying each node's type off the parsed `nodeType`. A
NodeType node + its `Source/*.cs` land as a NodeType with Code children, and the mesh's first-build
compile makes the type live. No app rebuild, no NuGet.

The version is the node's version, so "update available" is a git diff, not a hand-edited number.

## The registry — one credentialed hub, many consumers

GitSync needs credentials for a private plugins repo — and you don't want *every* installation to
hold them. So one MeshWeaver instance (memex) is the **registry**: it alone holds the source
credential, syncs the plugins repo into its mesh, and re-serves plugins over HTTP. Every other
installation pulls from the registry, never from git — the credential is **encapsulated in the
registry**, exactly like npm / NuGet (the registry has source access; clients just speak HTTP).

The surface is two **public** endpoints on the registry (`PluginRegistryEndpoints`), backed by its
configured git source (the plugins repo):

| Verb | Returns |
|---|---|
| `GET /api/plugins` | `{ packages:[PackageManifest…] }` — the curated modules from the configured source (node-native `<Plugin>.json` Space roots by default) |
| `POST /api/plugins/files` `{id}` | `{ files:[{relativePath, content}…] }` — the files that plugin (by id) ships |

A consuming instance browses this from its **Plugin Catalog** admin tab (Settings ▸ Administration,
platform admins only) and installs on click: the package's files are parsed into nodes and upserted
locally — a **Code** package synthesizes its `NodeType` + `Source` Code and compiles live; a
**Content** package imports its folder. The registry ships the **capability**, never data *instances*
— and no GitHub credential lives on the consumer at all. Full reference: [Plugin
Registry](/Doc/Architecture/PluginRegistry).

## Dynamic node types — a module that compiles itself

A **NodeType node** whose `Source/*.cs` defines its content type + layout areas is a **dynamic node
type**: the mesh compiles it with Roslyn on install and serves instances immediately. Proven examples
shipped this way: **Slide**, and the education types **Edu/Course**, **Edu/Module**, **Edu/Exercise**
(each compiles live and renders — verified by `EduNodeTypesCompileTest`).

Rules the runtime compile enforces (each load-bearing when migrating a compiled module):

- **Explicit usings** — implicit/global usings aren't injected; each Source file imports what it uses.
- **Public surface only** — an `internal` helper in a framework assembly is invisible to the compiled
  node-type assembly (inline an equivalent).
- **Identity is the install path** — a dynamic type's instances carry the NodeType node's path
  (`Edu/Course`), so logic that matches "my type" derives it from the node, never a hardcoded name.
- **Read foreign content untyped** — a sibling type compiled in another assembly resolves to a
  `JsonElement` here; read its fields off the JSON rather than referencing the foreign type.
- **`Test/*.cs` is plain C#** — the compile references only the runtime + loaded MeshWeaver
  assemblies, so in-node tests can't use a test framework (xUnit); they assert by throwing.

## What is (and isn't) a dynamic node type

A dynamic node type is **compiled content + layout areas, per node**. That covers a large class —
anything self-contained (Slide, the Edu course pages). It cannot, on its own, host:

- **cross-hub type registration** (registering sibling content types on the *mesh* hub for polymorphic
  routing), or
- a **control-plane service** (e.g. the exercise-attempt validation watcher).

Modules that need those (the exercise fork/validate control plane, AI agent execution) keep their
service layer compiled while their content + self-contained types ship as node repos. Turning the
service layer itself into git-delivered plugins is a separate capability (a boot-time module loader).

## The migration this enables

The same primitive — a node repo in a git source, imported into a running mesh — is how the static
node repos (Doc, Agent, Skill, samples) and module content move off the image and onto git delivery:
their **content** exports to the plugins repo (GitSync), and their **self-contained types** become
dynamic node types. Everything from git, versioned by the node, no NuGet.
