---
Name: Plugin Manual — Author, Publish, Install
Category: Architecture
Description: The end-to-end plugin manual — author a new plugin as a node repo, publish it to the plugins repository, install it on any instance (from the registry or straight from git), set up your own instance as a registry with its own GitHub App, and push changes back from the mesh to git.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20"/><path d="M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2z"/></svg>
---

# Plugin Manual — Author, Publish, Install

The practical companion to [Plugins](/Doc/Architecture/Plugins) (the architecture) and
[Plugin Registry](/Doc/Architecture/PluginRegistry) (the distribution model). This page is the
**how-to**: create a new plugin, publish it, install it anywhere, run your own registry, and push
changes back to git.

## 1. Anatomy of a plugin

A plugin is a **folder of mesh nodes** in a git repo — nothing else. The on-disk shape:

```text
MyPlugin.json                       the plugin's Space node ("package node": name, description, manifest)
MyPlugin/
  Widget.json                       a NodeType node (Content = NodeTypeDefinition{ configuration })
  Widget/
    Source/
      WidgetContent.cs              the content record
      WidgetLayoutAreas.cs          the layout areas (views)
    Test/
      WidgetTests.cs                in-node tests — compiled together with Source
  Guide.md                          a Markdown node documenting the plugin
```

- `*.json` files ARE MeshNodes, verbatim. `*.cs` files become `Code` nodes keyed by path.
- The **NodeType node** carries the type's configuration, e.g.
  `"configuration": "config => config.WithContentType<WidgetContent>().AddDefaultLayoutAreas().AddLayout(l => l.AddWidgetLayoutAreas())"`.
- The mesh **compiles `Source/` live** on install (Roslyn) — no app rebuild, no NuGet. Version = the
  node's mesh-tracked version.

Real examples to copy from (in `Systemorph/MeshWeaver.Plugins`): `Slides/` (one type),
`Edu/` (four types incl. cross-type reads), `LinkedIn/` (thirteen types + CSV loaders).

## 2. Author a new plugin, step by step

1. **Lay out the folder** as above, in a working copy of the plugins repo.
2. **Write the content record** (`Source/WidgetContent.cs`) — a plain record with the fields your
   type carries. Attributes like `[Required]`, `[DisplayName]`, `[MeshNodeProperty]` shape the editor.
3. **Write the layout areas** (`Source/WidgetLayoutAreas.cs`) — the views. Compose framework
   controls (`Controls.Stack`, `Controls.Markdown`, `Controls.DataGrid`…), never hand-built HTML.
4. **Write in-node tests** (`Test/WidgetTests.cs`) — plain C# that asserts by throwing (the runtime
   compile references only the platform assemblies; no xUnit).
5. **Mind the runtime-compile rules** (each one is load-bearing — details in
   [Plugins](/Doc/Architecture/Plugins)):
   - explicit `using`s in every file (no implicit usings);
   - only **public** framework surface (internal helpers are invisible — inline an equivalent);
   - a type's identity is its **install path** (`MyPlugin/Widget`) — derive "my type" from the node,
     never hardcode a name;
   - read **foreign content untyped** (a sibling type from another assembly resolves to
     `JsonElement` — read fields off the JSON).
6. **Add `Guide.md`** — how the plugin works, for the people installing it.

### Test locally

Install the folder into a local mesh and assert it compiles + renders — the pattern is
`EduNodeTypesCompileTest` (`test/MeshWeaver.Hosting.Monolith.Test`): create the NodeType + Source
nodes via `IMeshService.CreateNode`, wait for `CompilationStatus == Ok` on the node stream, render a
view. Any MeshWeaver dev instance also works: import the folder with GitSync from your fork/branch
and check the type's page.

## 3. Publish to the plugins repository

1. Push your folder to a branch of **`Systemorph/MeshWeaver.Plugins`** and open a PR.
2. CI runs `scripts/validate-repos.py` — every node `.json` must parse, carry `id` + `nodeType`,
   and every NodeType must ship `Source/*.cs`.
3. On merge, the plugin is published: the registry (memex) syncs the repo — as the **GitHub App**,
   never a person — and the plugin appears in `POST /api/mesh/catalog` for every consumer.

## 4. Install a plugin

### From the registry (any instance — no GitHub access needed)

```text
POST {registry}/api/mesh/catalog                       → pick a plugin
POST {registry}/api/mesh/catalog/download {plugin}     → its definition {nodes:[…]}
POST {your-instance}/api/mesh/update                   → feed the nodes in
```

Both calls use a mesh `Bearer mw_…` token (the same auth as the rest of `/api/mesh/*`). The types
compile on first import; re-running is an upsert. See [Plugin
Registry](/Doc/Architecture/PluginRegistry) for the payload shapes.

### Straight from git (your own instance, own repos)

If your instance has git access (its own GitHub App, or a connected user), import directly —
GitSync fetches the folder and parses every file into nodes:

- **GUI**: the Space's GitHub Sync settings → repository URL + branch + subfolder → Import.
- **Code / script**: `GitHubSyncService.ImportFromGitHub(repoUrl, ref, spaceId, spaceName, subdir, userId)`.

Authentication resolves user-credential-first, then the **App installation token** — so a headless
server instance imports with no personal login at all.

**Into an EXISTING Space** (the partition already has content): `ImportFromGitHub` is create-only —
it fails with `Node already exists`. Configure the source and reimport instead:

```csharp
sync.SaveConfig(spacePath, repoUrl, branch, subdir, false, false)   // register the source
sync.ReimportAtCommit(spacePath, branch, userId)                    // mirror add/update/prune
```

Two operational notes: a **failed** `ImportFromGitHub` (e.g. auth error) leaves an **empty orphan
Space** behind — inspect (`version:1`, no children) and delete it before retrying; and running
`SaveConfig` + `ReimportAtCommit` back-to-back can race the config read — a retry reads the settled
config. Re-running a reimport is idempotent (fingerprint-matched).

## 5. Set up your own instance as a registry

Any MeshWeaver instance can be the distribution point for its own plugins. One-time setup:

1. **Create a GitHub App** on your org (Settings → Developer settings → GitHub Apps → New):
   - Permissions: **Contents: Read** (Read & Write if you want push-back, §6);
   - no webhooks/callback needed for sync alone.
2. **Install the App** on the org and grant it the plugin repo(s).
3. **Generate a private key** on the App page (downloads a `.pem`).
4. **Configure the instance** (the `GitHub:App` section; ship secrets via KeyVault/env):

| Key | Value |
|---|---|
| `GitHub__App__ClientId` | the App's client id (`Iv23li…`) |
| `GitHub__App__PrivateKey` | the PEM text |
| `GitHub__App__InstallationOwner` | your org login (picks the installation; or pin `GitHub__App__InstallationId`) |

5. **Sync your plugins repo** into a Space (GitSync import, §4) — the instance now serves the
   plugins at `POST /api/mesh/catalog` to any consumer with a mesh token.

The App credential lives on this ONE instance; every consumer pulls over HTTP — that's the whole
point ([Plugin Registry](/Doc/Architecture/PluginRegistry) → credential encapsulation).

### App-grant gotchas (each one bites)

- **The grant is TWO steps**: the App's *Permissions & events* (Contents: Read [& Write]) **and** the
  installation's *Repository access* (which repos it can see). Either missing → API calls **404**
  (not 403) on the repo. A permission change on an installed App may also sit **pending approval**
  on the installation page until an org admin approves it.
- **Installation tokens fix their permissions at mint.** Granting repos/permissions does NOT upgrade
  already-minted tokens — and the platform caches its token until near expiry. After changing the
  grant, **restart the portal** to drop the cached token.
- **Client-id prefixes**: `Iv23li…` = GitHub **App**, `Ov23li…` = **OAuth App**. A client secret
  generated on one never matches the other's client id ("client_id and/or client_secret passed are
  incorrect" at token exchange while the authorize step succeeds).
- **Verify from outside** (no portal involved): sign an RS256 JWT with the PEM (`iss` = client id),
  then `GET /app/installations` → `POST /app/installations/{id}/access_tokens` → check the response
  `permissions`, then `GET /installation/repositories` with the token — the granted repos must list
  the plugin repo.

## 6. Push back (mesh → git)

Editing plugin content on the instance and syncing it back to the repo is the same GitSync export
("sync back") — and the same identity rules:

- **As the App** (default for server/registry instances): exports authenticate with the
  installation token and commits are authored as `meshweaver-app[bot]`. Requires the App's
  **Contents** permission to be **Read & Write**.
- **As a user**: someone who connected their GitHub account (`/connect/github/me`) exports under
  their own credential; commits are authored as them.

Trigger it from the Space's GitHub Sync settings (Sync back) or
`GitHubSyncService.SyncToGitHub(spacePath, userId)` — token resolution is automatic
(user credential first, else the App).

## 7. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `Connect your GitHub account first, or configure the GitHub App` | Neither identity available — connect a user, or set `GitHub:App:ClientId` + `PrivateKey` (§5). |
| `The GitHub App has no installations` | The App exists but isn't installed on the org — App page → Install App, grant the repo. |
| Import succeeds but the type doesn't render | Compile failed — check the NodeType's diagnostics (`get_diagnostics`/the node's Configuration tab); usual causes are the runtime-compile rules in §2.5. |
| Push-back 403 | The App's Contents permission is Read-only — set Read & Write and re-approve the installation. |
| `Octokit.NotFoundException` (404) on a repo that exists | The installation can't SEE the repo — repo missing from the installation's *Repository access*, or the token was minted before the grant (restart the portal to drop the cached token). |
| `Node already exists: {space}` on import | `ImportFromGitHub` is create-only. For an existing Space use `SaveConfig` + `ReimportAtCommit` (§4). If the existing node is an empty orphan from a previously failed import, delete it and retry. |
