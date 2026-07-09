---
Name: Plugin Registry
Category: Architecture
Description: One MeshWeaver instance acts as the plugin registry — it holds the source credential, syncs plugins from git, and re-serves them over REST so any installation pulls plugins without its own GitHub access. The credential is encapsulated in the registry, npm/NuGet-style.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 3h18v4H3z"/><path d="M5 7v14h14V7"/><path d="M10 12h4"/></svg>
---

# Plugin Registry

[Plugins](/Doc/Architecture/Plugins) install by importing a repo of mesh nodes over
[GitSync](/Doc/Architecture/StaticRepoImport). That works, but GitSync needs **credentials** for a
private plugins repo — and you do not want *every* installation to hold GitHub credentials just to
receive a plugin.

The registry solves that. **One** MeshWeaver instance (memex) is the registry: it alone holds the
source credential, syncs the plugins repo into its mesh, and **re-serves plugins over HTTP**. Every
other installation pulls from the registry, never from git. The credential is **encapsulated in the
registry** — exactly like npm or NuGet, where the registry has source access and clients just speak
HTTP.

```text
  Systemorph/MeshWeaver.Plugins (git, private)
                │  GitSync — ONE credentialed sync
                ▼
        ┌───────────────┐        POST /api/mesh/catalog            ┌──────────────┐
        │    memex       │◀───────────────────────────────────────│ installation │
        │  (registry)    │  POST /api/mesh/catalog/download {plugin}│  (consumer)  │
        │ holds the cred │────────────────────────────────────────▶│ no GitHub    │
        └───────────────┘         {nodes:[…]}  ─── /api/mesh/update │  credential  │
                                                                    └──────────────┘
```

## The surface

Two verbs on the mesh REST API (`/api/mesh/*`, the transport-mirror of the MCP tools). They live on
`MeshOperations` — the shared core behind BOTH REST and MCP — so the two transports cannot drift.

| Verb | Body | Returns |
|---|---|---|
| `POST /api/mesh/catalog` | — | `{count, plugins:[{name, typeCount, types:[path]}]}` |
| `POST /api/mesh/catalog/download` | `{plugin}` | `{name, nodeCount, nodes:[MeshNode…]}` |

Both are gated by the same `Authorization: Bearer mw_…` token as the rest of `/api/mesh/*` — a
consumer authenticates to the registry with a mesh token (the same token
[instance sync](/Doc/Architecture/InstanceSync) uses), never with GitHub credentials.

### `catalog` — discovery

Lists every **partition that ships NodeTypes** as an installable plugin. A partition with one or
more `nodeType:NodeType` nodes is, by definition, a plugin (it ships a capability).

```json
{
  "count": 3,
  "plugins": [
    { "name": "Edu",      "typeCount": 4,  "types": ["Edu/Course", "Edu/Exercise", "Edu/Module", "Edu/Quiz"] },
    { "name": "LinkedIn", "typeCount": 13, "types": ["LinkedIn/Connections", "LinkedIn/Education", "…"] },
    { "name": "Slides",   "typeCount": 1,  "types": ["Slides/Slide"] }
  ]
}
```

### `catalog/download` — the package

Returns a plugin's **definition** — the installable capability, ready to import:

- **Ships**: the Space root, the `NodeType` nodes, their `Source/` and `Test/` **Code** (the C# that
  compiles the types), and `Markdown` docs.
- **Does NOT ship**: data **instances** of those types, nor runtime satellites (`/_Activity` compile
  logs, `/Release/` snapshots, notifications). The registry distributes the *capability*, not the
  data — so downloading the `LinkedIn` plugin gives you the 13 types and their code, never anyone's
  personal LinkedIn records.

The `Source`/`Test` Code nodes are queried **explicitly** (`namespace:{plugin} scope:subtree
nodeType:Code`) because they live in a separate schema a plain subtree query misses — the same reason
the [compiler](/Doc/Architecture/NodeTypeCompilation) queries `namespace:…/Source scope:subtree`.

```json
{
  "name": "Slides",
  "nodeCount": 4,
  "nodes": [
    { "path": "Slides",                              "nodeType": "Space",    "content": { … } },
    { "path": "Slides/Slide",                        "nodeType": "NodeType", "content": { "$type": "NodeTypeDefinition", … } },
    { "path": "Slides/Slide/Source/SlideContent",    "nodeType": "Code",     "content": { "$type": "CodeConfiguration", "code": "…" } },
    { "path": "Slides/Slide/Source/SlideLayoutAreas","nodeType": "Code",     "content": { … } }
  ]
}
```

## Installing from the registry

A consumer installs a plugin with **no GitHub credential** — it pulls from the registry and imports
locally through the existing update verb:

1. `POST /api/mesh/catalog` on the registry → pick a plugin.
2. `POST /api/mesh/catalog/download {plugin}` on the registry → the `{nodes:[…]}` package.
3. `POST /api/mesh/update` **on the consumer** with those `nodes` → the NodeTypes land with their
   Code children and the mesh's first-build [compile](/Doc/Architecture/NodeTypeCompilation) makes
   the types live. No app rebuild, no NuGet.

The nodes carry their paths, so a plugin installs into the same partition names it was published
under (`Slides`, `Edu`, …). Re-running an install is an upsert — `update` replaces by path/version.

## Why this shape

- **Credential encapsulation.** GitHub access lives on exactly one hub. Onboarding a new
  installation is "point it at the registry with a mesh token," not "provision it a GitHub App."
- **No drift.** The verbs are on `MeshOperations`, so the REST surface and the MCP tools share one
  implementation. A change to what a plugin *is* happens once.
- **Capability, not data.** Shipping `NodeType`/`Code`/`Markdown` and excluding instances keeps the
  package small and keeps user data (e.g. LinkedIn records) from leaking through the registry.

## Relationship to GitSync

GitSync is still how the registry itself gets plugins — memex `ImportFromGitHub`s the plugins repo
with its one credential. The registry adds the *fan-out*: git → registry (credentialed, once) →
many installations (credential-free, over HTTP). See [Plugins](/Doc/Architecture/Plugins) for the
node-native plugin model and [Static Repo Import](/Doc/Architecture/StaticRepoImport) for the import
pipeline both paths share.
