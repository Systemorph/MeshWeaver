---
Name: Plugin Registry
Category: Architecture
Description: One MeshWeaver instance acts as the plugin registry — it holds the source credential, syncs plugins from git, and re-serves them over a public REST surface so any installation's platform admin can browse and install plugins without its own GitHub access. The credential is encapsulated in the registry, npm/NuGet-style.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 3h18v4H3z"/><path d="M5 7v14h14V7"/><path d="M10 12h4"/></svg>
---

# Plugin Registry

For the step-by-step how-to (author → publish → install → own registry) see the [Plugin Manual](/Doc/Architecture/PluginAuthoring).

[Plugins](/Doc/Architecture/Plugins) are folders of mesh nodes in a git repo, each carrying a
`package.json` [manifest](/Doc/Architecture/PluginAuthoring). Installing one means importing its
nodes and compiling its node types live. But you do **not** want *every* installation to hold GitHub
credentials for a private plugins repo just to receive a plugin.

The registry solves that. **One** MeshWeaver instance (memex.meshweaver.cloud) is the registry: it
alone holds the source credential, reads the plugins repo, and **re-serves the catalog over a public
HTTP surface**. Every other installation's platform admin browses and installs from the registry,
never from git. The credential is **encapsulated in the registry** — exactly like npm or NuGet, where
the registry has source access and clients just speak HTTP.

```text
  Systemorph/MeshWeaver.Plugins (git, private)
                │  the registry reads it with its ONE GitHub App credential
                ▼
        ┌───────────────┐   GET  /api/plugins            ┌────────────────────────┐
        │   registry     │◀───────────────────────────────│ installation (consumer)│
        │ memex.mesh…    │   POST /api/plugins/files {id}  │  Settings ▸ Admin ▸     │
        │ holds the cred │────────────────────────────────▶│  Plugin Catalog        │
        └───────────────┘     {packages} / {files}         │  (platform admins)     │
                                                            │  no GitHub credential  │
                                                            └────────────────────────┘
```

## The surface — public, curated

Two endpoints, mapped by `PluginRegistryEndpoints` (`memex/Memex.Portal.Shared/Api`). Unlike the
authenticated `/api/mesh/*` surface, these are **anonymous** — a plugin registry is meant to be
pulled by any installation. That is safe because the registry only exposes **curated plugins**:
by default the node-native repos the [`MeshWeaver.Plugins`](/Doc/Architecture/Plugins) repo ships —
`<Plugin>/index.json` **Space** roots carrying a `PluginManifest`, node-per-file — via a
`NodeRepoPackageSource` (`PluginCatalog:SourceFormat=node-repo`, the default). A `package.json`-manifest
repo can be served instead with `SourceFormat=package-json`. Nothing outside a published plugin is
exposed, and the registry's own credential never leaves.

| Verb | Body | Returns |
|---|---|---|
| `GET /api/plugins` | — (`?ref=` advisory) | `{ packages: [PackageManifest…] }` |
| `POST /api/plugins/files` | `{ id }` (`ref` optional/advisory) | `{ files: [{ relativePath, content }…] }` |

A package is addressed by its **id** only; the registry resolves what that plugin ships from its
configured source — the consumer never supplies a folder path.

Both are backed by the registry's configured git [`IPackageSource`](/Doc/Architecture/Plugins) —
`PluginCatalog:SourceRepoPath` (a URL → the plugins repo via GitSync's client, or a local path). The
registry is authoritative on the git ref (`PluginCatalog:SourceRef`); a consumer's `ref` is advisory.
The wire shapes are produced by `PluginRegistryPayloads` and parsed by `RegistryPackageSource`, one
place each, so producer and consumer cannot drift.

## The consumer — a platform-admin tab, not a Space

On every installation the catalog is a **Settings tab** — `PluginCatalogSettingsTab`, grouped under
**Administration** beside Global Administration, and gated the same way (`hub.IsGlobalAdmin`). It is
**not** a browsable `Plugins` Space: a catalog is a platform-admin feature, and a Space partition
would (correctly) deny read to everyone else — the very "Access denied on 'Plugins'" a Space produced.

The tab reads `PluginCatalog:RegistryUrl` (e.g. `https://memex.meshweaver.cloud`), lists the
registry's packages via `RegistryPackageSource` (an `IPackageSource` over HTTP, on the mesh's Http
I/O pool), and joins them against this instance's install registry — the `Package` nodes under the
`Plugins` partition — to render **Install / Update / Installed** per module.

## Installing

An admin clicks **Install** (or **Update**). No GitHub credential is involved on the consumer:

1. `POST /api/plugins/files {id}` on the registry → the package's folder files.
2. `PackageInstaller.Install` **on the consumer** parses the files into MeshNodes and upserts them —
   a **Content** package imports its folder into the target partition; a **Code** package synthesizes
   its `NodeType` node from the manifest's `nodeTypeConfiguration`, imports its `Source/*.cs` as Code
   children, and requests a release so the mesh [compiles](/Doc/Architecture/NodeTypeCompilation) the
   type live. No app rebuild, no NuGet.
3. An install record (a `Package` node) is written under the `Plugins` partition so the tab flips the
   card to **Installed** and can offer **Update** when the registry's version moves on.

Re-installing is an upsert (create-or-update by path); installing one module never disturbs another
in a shared partition.

## Why this shape

- **Credential encapsulation.** GitHub access lives on exactly one instance. Onboarding a new
  installation is "point `PluginCatalog:RegistryUrl` at the registry," not "provision it a GitHub App."
- **Not a Space.** The catalog is an admin tab reading a remote registry; there is no partition for a
  non-admin to navigate into and be denied.
- **Curated + public.** Only `package.json` folders are exposed, so anonymous read is safe and the
  catalog lists real modules (Slides, Edu, …), not every partition that happens to define a type.
- **Capability, not data.** A package ships its `NodeType`/`Code`/content folder — never a partition's
  user data — so installing a plugin gives you the types and their code, not anyone's records.

## Relationship to GitSync

GitSync is how the registry itself reads plugins — the registry's `IGitHubRepoClient` fetches the
plugins repo with its one credential. The registry adds the *fan-out*: git → registry (credentialed,
once) → many installations (credential-free, over HTTP). See [Plugins](/Doc/Architecture/Plugins) for
the node-native plugin model and [Static Repo Import](/Doc/Architecture/StaticRepoImport) for the
import pipeline both paths share.
