---
Description: "The mirror MCP tool for pushing or pulling MeshNode subtrees between running MeshWeaver instances over HTTPS, with server-side remote profiles for credentials."
title: Cross-Instance Mirror — Push & Pull between MeshWeaver portals
order: 30
---

# Cross-Instance Mirror

Move a subtree of MeshNodes from one running MeshWeaver instance to another — no ZIP exports, no file uploads, no per-node back-and-forth. The most common use is **pushing from local dev to prod** so content you authored in `memex-local` appears at `https://memex.meshweaver.cloud` in a single command.

> ⚠️ **Status: the mesh hub currently has no handler for `MirrorRequest`** — the contract (`MirrorRequest`/`MirrorResult` in `src/MeshWeaver.Mesh.Contract/Services/IMirrorOperations.cs`), the MCP tools, the REST endpoint, and the HTTP transport are all in place, but a posted `MirrorRequest` receives no response until the handler (fanning out `CreateNodeRequest` per node) lands. This page documents the contract and intended behavior.

## What it does
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 760 300" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="arr-r" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto">
      <polygon points="0 0, 8 3, 0 6" fill="#1e88e5"/>
    </marker>
    <marker id="arr-g" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto">
      <polygon points="0 0, 8 3, 0 6" fill="#43a047"/>
    </marker>
    <marker id="arr-l" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto">
      <polygon points="8 0, 0 3, 8 6" fill="#43a047"/>
    </marker>
  </defs>
  <rect x="30" y="60" width="180" height="180" rx="12" fill="#1565c0" opacity="0.9"/>
  <text x="120" y="92" font-family="sans-serif" font-size="14" font-weight="bold" fill="#fff" text-anchor="middle">Local Dev</text>
  <text x="120" y="112" font-family="sans-serif" font-size="11" fill="#90caf9" text-anchor="middle">localhost:7122</text>
  <rect x="55" y="130" width="130" height="32" rx="8" fill="#1e88e5"/>
  <text x="120" y="151" font-family="sans-serif" font-size="11" fill="#fff" text-anchor="middle">mirror (push)</text>
  <rect x="55" y="174" width="130" height="32" rx="8" fill="#1e88e5"/>
  <text x="120" y="195" font-family="sans-serif" font-size="11" fill="#fff" text-anchor="middle">mirror (pull)</text>
  <rect x="550" y="60" width="180" height="180" rx="12" fill="#1b5e20" opacity="0.9"/>
  <text x="640" y="92" font-family="sans-serif" font-size="14" font-weight="bold" fill="#fff" text-anchor="middle">Prod Portal</text>
  <text x="640" y="112" font-family="sans-serif" font-size="11" fill="#a5d6a7" text-anchor="middle">memex.meshweaver.cloud</text>
  <rect x="575" y="130" width="130" height="32" rx="8" fill="#43a047"/>
  <text x="640" y="151" font-family="sans-serif" font-size="11" fill="#fff" text-anchor="middle">Node import (upsert)</text>
  <rect x="575" y="174" width="130" height="32" rx="8" fill="#43a047"/>
  <text x="640" y="195" font-family="sans-serif" font-size="11" fill="#fff" text-anchor="middle">ApiToken auth</text>
  <line x1="215" y1="146" x2="540" y2="146" stroke="#1e88e5" stroke-width="2" stroke-dasharray="6 3" marker-end="url(#arr-r)"/>
  <text x="378" y="138" font-family="sans-serif" font-size="11" fill="#90caf9" text-anchor="middle">Push — outbound HTTPS</text>
  <line x1="540" y1="190" x2="215" y2="190" stroke="#43a047" stroke-width="2" stroke-dasharray="6 3" marker-end="url(#arr-l)"/>
  <text x="378" y="208" font-family="sans-serif" font-size="11" fill="#a5d6a7" text-anchor="middle">Pull — outbound HTTPS (local fetches prod)</text>
  <text x="380" y="270" font-family="sans-serif" font-size="11" fill="currentColor" fill-opacity="0.55" text-anchor="middle">Both operations run server-side; Claude Code makes one MCP tool call regardless of subtree size.</text>
</svg>

*Push (`mirror direction=push`) and Pull (`mirror direction=pull`) both initiate outbound HTTPS from the local instance — no tunnel needed when targeting a public prod URL.*

`mirror` is one MCP tool available on every MeshWeaver instance, with a `direction` of `push` (local → remote) or `pull` (remote → local). It executes entirely server-side: a 10 000-node migration is one MCP tool call from Claude Code's perspective, not 10 000.

### Remote profiles — keep the token out of the model context

The preferred way to authenticate is a **named remote profile** in the host configuration:

```json
"Mirror": {
  "Remotes": {
    "prod": { "BaseUrl": "https://memex.meshweaver.cloud", "Token": "mw_…" }
  }
}
```

Then the tool call is just `mirror direction=push remote="prod" sourcePath="rbuergi/Story"` — the ApiToken never travels through the model context, transcripts, or logs. Passing a base URL as `remote` also resolves the token from a profile with a matching `BaseUrl`. Supplying `remoteToken` inline remains available as an ad-hoc fallback, but is discouraged: tool arguments are visible to the model and may be persisted in conversation transcripts.

Under the hood:

1. Reads every node under `sourcePath` from the source side.
2. Calls `mcp.create` (or `mcp.update` for upserts) on the destination for each node, authenticating with the **destination's** ApiToken.
3. Delegates the recursive copy to the mirror handler on the mesh hub, which fans out `CreateNodeRequest` per node (see the status note above).
4. Returns a JSON summary with the fields `status`, `direction`, `sourcePath`, `targetPath`, `nodesImported`, `nodesSkipped`, `nodesRemoved`, `partitionsImported`, and `elapsedMs`.

## Network direction matrix

Both tools initiate outbound HTTPS from the side they run on. The rule of thumb: **run the tool on whichever side has network reach to the other**.

| You want to … | Run the tool on | Initiates outbound to | Works without a tunnel? |
|---|---|---|---|
| Push local → prod | `mcp__memex-local__mirror` (`direction=push`) | `https://memex.meshweaver.cloud` | ✅ public HTTPS |
| Pull prod → local | `mcp__memex-local__mirror` (`direction=pull`) | `https://memex.meshweaver.cloud` | ✅ public HTTPS (local pulls in) |
| Push prod → local | (run on prod) `mirror direction=push` | localhost | ❌ prod can't reach localhost |
| Pull local → prod | (run on prod) `mirror direction=pull` | localhost | ❌ same |

For the third and fourth cases, expose your local instance with a Cloudflare tunnel or ngrok and use the public URL as the remote.

## Step-by-step recipe — push local content to prod

### 1. Issue an ApiToken on the **destination** portal

Open the destination portal (e.g. `https://memex.meshweaver.cloud`), log in as the user the import should run as, and:

- Navigate to **Settings → API Tokens** (or `/me/Settings/ApiTokens`).
- Click **Create token**, name it (e.g. `mirror-from-local-2026-05`), scope it to the user, and **copy** the `mw_…` value.
- Tokens are revocable from the same page; rotate after one-shot mirrors.

### 2. Dry-run from the source

Always preview before writing. Pass `dryRun=true` to enumerate the subtree without touching the destination:

```text
mcp__memex-local__mirror_to_remote
    remoteBaseUrl="https://memex.meshweaver.cloud"
    remoteToken="mw_…"
    sourcePath="rbuergi/Story"
    targetPath="rbuergi/Story"
    dryRun=true
```

Example response:

```json
{
  "status": "DryRun",
  "direction": "Push",
  "sourcePath": "rbuergi/Story",
  "targetPath": "rbuergi/Story",
  "nodesScanned": 4,
  "paths": [
    "rbuergi/Story/KernelTour",
    "rbuergi/Story/KernelTour/01-Code",
    "rbuergi/Story/KernelTour/02-Activity",
    "rbuergi/Story/KernelTour/03-NodeTypes"
  ]
}
```

Read the list. Confirm the count and paths match your expectations before proceeding.

### 3. Execute for real

Same call, `dryRun=false` (the default):

```text
mcp__memex-local__mirror_to_remote
    remoteBaseUrl="https://memex.meshweaver.cloud"
    remoteToken="mw_…"
    sourcePath="rbuergi/Story"
```

Example response:

```json
{
  "status": "Ok",
  "direction": "Push",
  "sourcePath": "rbuergi/Story",
  "targetPath": "rbuergi/Story",
  "nodesImported": 4,
  "nodesSkipped": 0,
  "nodesRemoved": 0,
  "partitionsImported": 0,
  "elapsedMs": 412
}
```

### 4. Verify on the destination

```text
mcp__memex-prod__search query="namespace:rbuergi/Story scope:subtree"
```

This should return the four nodes. You can also open `https://memex.meshweaver.cloud/rbuergi/Story/KernelTour` directly in a browser.

## Pulling from a remote into local

`PullFromRemote` uses the same shape as `MirrorToRemote` — just a different tool name. Here local makes outbound calls to prod, fetches the subtree, and writes it under the target path:

```text
mcp__memex-local__pull_from_remote
    remoteBaseUrl="https://memex.meshweaver.cloud"
    remoteToken="mw_…"
    sourcePath="Doc/Architecture/GrantingAccess"
    targetPath="rbuergi/MyDocs/GrantingAccess"
    dryRun=true
```

## Flags

| Flag | Default | Effect |
|------|---------|--------|
| `dryRun` | `false` | Enumerate without writing. Safe to run any time. |
| `removeMissing` | `false` | **Destructive.** Delete destination nodes that don't exist on the source. Use only when you want the destination to mirror the source exactly. |
| `targetPath` | `sourcePath` | Write under a different path on the destination — useful for sandbox copies (e.g. `rbuergi/Story` → `rbuergi/Story-staging`). |

## Authentication and access scope

The destination's `ApiTokenAuthenticationHandler` validates the token and stamps the user's ObjectId onto every per-node write. **The mirror runs as the user who issued the token.** That user must have:

- `Permission.Read` on the source paths (on the source portal — usually trivially satisfied when operating on your own partition).
- `Permission.Create` / `Update` on the destination paths.

> **Note:** If the destination user lacks Create on a path, the import silently skips the offending nodes (logged via `_logger?.LogWarning`). Run with the destination's Admin role during development to avoid surprises.

## What does NOT cross instances (v1)

Not everything survives a mirror. Content that lives outside `node.Content` is out of scope for v1:

- **Partition objects** — Activity messages, Comment trees, and satellite-table data are not included. The MCP tool surface doesn't yet expose a generic enumerate-partition-objects call. Inline `node.Content` payloads (Markdown, Code, NodeTypeDefinition, etc.) survive cleanly; satellite data does not.
- **Compiled assemblies** — destination NodeTypes recompile from source on first access. Trigger early with `mcp__memex-prod__compile @rbuergi/MyType` to warm the cache before any UI hits the type.
- **API tokens themselves** — tokens are partition-scoped and cannot be mirrored across portals. Issue a fresh token on the destination.

## Token economy

Each `mirror_to_remote` / `pull_from_remote` invocation from Claude Code is **one** MCP tool call: approximately 1 k input tokens (args) plus a short text summary back. The actual recursive copy runs server-side via `HttpClient` — Claude isn't reasoning node by node.

If you want strictly zero LLM tokens, the same `MirrorOperations` logic is also exposed via the import dialog UI (Blazor). Future work includes a CLI that drives it without an LLM in the loop.

## Source links

| Component | Path |
|-----------|------|
| Request/response contract (`MirrorRequest`/`MirrorResult`) | `src/MeshWeaver.Mesh.Contract/Services/IMirrorOperations.cs` |
| HTTP storage adapter | `src/MeshWeaver.Hosting/Persistence/Http/HttpMeshStorageAdapter.cs` |
| MCP transport | `src/MeshWeaver.Hosting/Persistence/Http/McpRemoteMeshClient.cs` |
| Path remapping (source → target prefix) | `src/MeshWeaver.Hosting/Persistence/Http/PathRemappingStorageAdapter.cs` |
| MCP tools (`MirrorToRemote`, `PullFromRemote`) | `src/MeshWeaver.Blazor.AI/McpMeshPlugin.cs` |
| REST endpoint (`POST /api/mesh/mirror`) | `memex/Memex.Portal.Shared/Api/MeshApiEndpoints.cs` |
| Mesh-hub handler | *not yet registered — see status note* |
| Tests | `test/MeshWeaver.Hosting.Test/HttpMeshStorageAdapterTests.cs` · `MirrorOperationsTests.cs` |
| Auth handler | `memex/Memex.Portal.Shared/Authentication/ApiTokenAuthenticationHandler.cs` |

## Troubleshooting

| Symptom | Likely cause |
|---------|--------------|
| `401 Unauthorized` on every call | ApiToken expired or revoked, or the owning user has been disabled. Re-issue. |
| `nodesSkipped` is non-zero | Destination user lacks Create/Update on the failing paths. Check AccessAssignments on the destination ([GrantingAccess.md](/Doc/Architecture/GrantingAccess)). |
| Empty `nodesImported` for a non-empty path | `sourcePath` doesn't match anything. Verify with `mcp.search namespace:{sourcePath} scope:subtree` on the source side first. |
| Partition data missing on destination | Expected in v1 — only inline `node.Content` is mirrored. Use the local ZIP export/import for satellite-table data. |
| Hung or 30 s+ for a small subtree | Remote is in a bad state (recently restarted; cold-grain activation). Retry once. |

## Related

- [GrantingAccess.md](/Doc/Architecture/GrantingAccess) — how to issue and audit AccessAssignments.
- [PostgresSchemaArchitecture.md](/Doc/Architecture/PostgresSchemaArchitecture) — partition-scope model.
