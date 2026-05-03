---
title: Cross-Instance Mirror — Push & Pull between MeshWeaver portals
order: 30
---

# Cross-Instance Mirror

Move a subtree of MeshNodes from one running MeshWeaver instance to another.
The most common use is **push from local dev to prod** so the content you
authored in `memex-local` shows up at `https://memex.meshweaver.cloud` without
ZIP exports / file uploads.

## What it does

`MirrorToRemote` and `PullFromRemote` are MCP tools on every MeshWeaver
instance. Each runs server-side in a single round-trip — a 10000-node migration
is one MCP tool call from Claude Code's perspective, not 10000.

Under the hood:

1. Reads every node under `sourcePath` from the source side.
2. Calls `mcp.create` (or `mcp.update` for upserts) on the destination side
   for each one, authenticating with the **destination's** ApiToken.
3. Reuses [`StorageImporter`](../../../MeshWeaver.Hosting/Persistence/StorageImporter.cs)
   as the recursive copy engine — same logic the local file/ZIP import uses.
4. Returns a JSON summary: `{status, direction, sourcePath, targetPath,
   nodesImported, nodesSkipped, nodesRemoved, partitionsImported, elapsedMs}`.

## Network direction matrix

`MirrorToRemote` and `PullFromRemote` BOTH initiate outbound HTTPS from the
side they run on. So whichever side has reach to the other is the one that
hosts the call.

| You want to … | Run the tool on | Initiates outbound to | Works without a tunnel? |
|---|---|---|---|
| Push local → prod | `mcp__memex-local__mirror_to_remote` | `https://memex.meshweaver.cloud` | ✅ public HTTPS |
| Pull prod → local | `mcp__memex-local__pull_from_remote` | `https://memex.meshweaver.cloud` | ✅ public HTTPS (local pulls in) |
| Push prod → local | (run on prod) `mirror_to_remote` | localhost | ❌ prod can't reach localhost |
| Pull local → prod | (run on prod) `pull_from_remote` | localhost | ❌ same |

For the third and fourth cases, expose local with a Cloudflare tunnel or
ngrok and pass the public URL.

## Step-by-step recipe — push your local content to prod

### 1. Issue an ApiToken on the **destination** portal

Open the destination portal (e.g. `https://memex.meshweaver.cloud`), log in
as the user the import should run as, and:

- Navigate to **Settings → API Tokens** (or `/me/Settings/ApiTokens`).
- Click **Create token**, name it (e.g. `mirror-from-local-2026-05`),
  scope it to the user, and **copy** the `mw_…` value.
- Tokens are revocable from the same page; rotate after one-shot mirrors.

### 2. Dry-run from the **source**

```text
mcp__memex-local__mirror_to_remote
    remoteBaseUrl="https://memex.meshweaver.cloud"
    remoteToken="mw_…"
    sourcePath="rbuergi/Story"
    targetPath="rbuergi/Story"
    dryRun=true
```

Returns:

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

Read the list. Confirm the count + paths match expectations.

### 3. Execute for real

Same call, `dryRun=false` (default):

```text
mcp__memex-local__mirror_to_remote
    remoteBaseUrl="https://memex.meshweaver.cloud"
    remoteToken="mw_…"
    sourcePath="rbuergi/Story"
```

Returns:

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

Should return the four nodes. Or open
`https://memex.meshweaver.cloud/rbuergi/Story/KernelTour` in a browser.

## Pulling from a remote into local

Same shape, different tool name:

```text
mcp__memex-local__pull_from_remote
    remoteBaseUrl="https://memex.meshweaver.cloud"
    remoteToken="mw_…"
    sourcePath="Doc/Architecture/GrantingAccess"
    targetPath="rbuergi/MyDocs/GrantingAccess"
    dryRun=true
```

Local makes outbound calls to prod, fetches the subtree, writes locally under
the target path.

## Flags

| Flag | Default | Effect |
|------|---------|--------|
| `dryRun` | `false` | Enumerate without writing. Preview; safe. |
| `removeMissing` | `false` | **Destructive.** Delete destination nodes that don't exist on the source. Use only when you really want the destination to mirror the source exactly. |
| `targetPath` | `sourcePath` | Write under a different path on the destination — useful for sandbox copies (`rbuergi/Story` → `rbuergi/Story-staging`). |

## Authentication & access scope

The destination's `ApiTokenAuthenticationHandler` validates the token and
stamps the user's ObjectId onto every per-node write. **The mirror runs as
the user who issued the token.** That user must have:

- `Permission.Read` on the source paths (on the source portal — usually
  trivially true; you're operating on your own partition).
- `Permission.Create` / `Update` on the destination paths.

If the destination user lacks Create on `rbuergi/Story`, the import will
silently skip the offending nodes (logged via `_logger?.LogWarning`). Run
with the destination's Admin role to avoid surprises during dev.

## What does NOT cross instances (v1)

- **Partition objects** — Activity messages, Comment trees, satellite-table
  data living outside `node.Content`. The MCP tool surface doesn't yet
  expose a generic enumerate-partition-objects call. Inline `node.Content`
  payloads (Markdown, Code, NodeTypeDefinition, etc.) survive cleanly; satellite
  data does not.
- **Compiled assemblies** — destination NodeTypes recompile from source on
  first access. Trigger with `mcp__memex-prod__compile @rbuergi/MyType` to
  warm the cache before any UI hits the type.
- **API tokens themselves** — tokens are partition-scoped; you can't mirror
  them across portals. Issue a fresh one on the destination.

## Token economy

If you drive the mirror from Claude Code, each `mirror_to_remote` /
`pull_from_remote` invocation is **one** MCP tool call: ~1k input tokens
(args) + a small text summary back. The actual recursive copy runs
server-side via `HttpClient` — Claude isn't reasoning per node.

If you want strictly zero LLM tokens, the same `MirrorOperations` is
exposed via the import dialog UI (Blazor); future work also includes a
CLI that drives it without an LLM in the loop.

## Source links

- Adapter: `src/MeshWeaver.Hosting/Persistence/Http/HttpMeshStorageAdapter.cs`
- MCP transport: `src/MeshWeaver.Hosting/Persistence/Http/McpRemoteMeshClient.cs`
- Operations: `src/MeshWeaver.Hosting/Persistence/Http/MirrorOperations.cs`
- MCP tools: `src/MeshWeaver.Blazor.AI/McpMeshPlugin.cs` (`MirrorToRemote`, `PullFromRemote`)
- Recursive copy engine: `src/MeshWeaver.Hosting/Persistence/StorageImporter.cs`
- Tests: `test/MeshWeaver.Hosting.Test/HttpMeshStorageAdapterTests.cs` + `MirrorOperationsTests.cs`
- Auth: `memex/Memex.Portal.Shared/Authentication/ApiTokenAuthenticationHandler.cs`

## Troubleshooting

| Symptom | Likely cause |
|---------|--------------|
| `401 Unauthorized` on every call | ApiToken expired/revoked, or the user the token belongs to has been disabled. Re-issue. |
| `nodesSkipped` is non-zero | Destination user lacks Create/Update on the failing paths. Check AccessAssignments on the destination ([GrantingAccess.md](GrantingAccess.md)). |
| Empty `nodesImported` for a non-empty path | `sourcePath` doesn't match anything. Verify with `mcp.search namespace:{sourcePath} scope:subtree` on the source side first. |
| Partition data missing on destination | Expected in v1 — only inline `node.Content` is mirrored. Use the local ZIP export/import for satellite-table data. |
| Hung or 30 s+ for a small subtree | Remote is in a bad state (recently restarted; cold-grain activation). Retry. |

## Related

- [GrantingAccess.md](GrantingAccess.md) — how to issue and audit AccessAssignments.
- [PostgresSchemaArchitecture.md](PostgresSchemaArchitecture.md) — partition-scope model.
- [PostmortemDavDemo.md](Postmortems/PostmortemDavDemo.md) — why partition-scope matters.
