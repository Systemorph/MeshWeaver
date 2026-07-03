---
NodeType: Markdown
Name: "Instance Sync — bi-directional space replication between MeshWeaver instances"
Abstract: "Replicate a Space to another MeshWeaver instance and keep syncing changes as they happen, SharePoint-style. The sync registry lives at {space}/_Sync/{sourceId}; each registration carries a durable change manifest that accumulates while the remote is unreachable and drains on reconnect. The registering instance is the sync CLIENT for both directions — push (change feed → manifest → remote MCP surface) and pull (periodic reconciliation sweep) — so the remote stays passive."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#1565c0'/><path d='M7 9a5 5 0 0 1 9.6-1.5M17 15a5 5 0 0 1-9.6 1.5' stroke='white' stroke-width='2' fill='none' stroke-linecap='round'/><path d='M16.5 4v3.5H13M7.5 20v-3.5H11' stroke='white' stroke-width='2' fill='none' stroke-linecap='round' stroke-linejoin='round'/></svg>"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Sync"
  - "Replication"
  - "Federation"
---

> **Read first:** [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls) (everything here is reactive, IoPool-bounded) and [Access Context Propagation](/Doc/Architecture/AccessContextPropagation) (worker writes run as system). The GitHub counterpart of this feature is the GitSync engine; instance sync reuses its config-satellite and settings-tab shape.

## What it does

From any Space's **Settings → Instance Sync** tab, a Space admin adds a *syncing party*: the URL
of another MeshWeaver instance plus an ApiToken issued there. The Space is replicated to the
remote instance, and from then on changes keep syncing as they happen — like syncing a
SharePoint library. If the other instance is down, changes **accumulate** in a durable manifest
and are applied when it can be reached again. The tab lists every syncing party with its live
status, and each party can be paused (`Active` off), poked (`Sync now`) or removed (cancel).
The same sources also appear on the platform-admin **Partitions** page through the
`IPartitionSyncSourceProvider` seam (kind: *Remote Instance*).

## The registry: `{space}/_Sync/{sourceId}`

Each syncing party is one MeshNode at `{space}/_Sync/{sourceId}` with content
`InstanceSyncConfig` (project `MeshWeaver.InstanceSync`):

| Field | Meaning |
|---|---|
| `RemoteUrl`, `RemoteToken` | The remote portal and the ApiToken issued **by that remote** |
| `RemoteSpace` | Space id on the remote (blank = same id) |
| `Direction` | `Bidirectional` (default) / `PushOnly` / `PullOnly` |
| `Active` | Uncheck to pause — changes still accumulate, nothing transfers |
| `SyncRequestedAt` | Control-plane trigger stamped by "Sync now" (the Requested-field pattern) |
| `Status`, `LastSyncedAt`, `LastError` | Worker-written, read-only in the GUI |
| `PendingChanges` | **The durable change manifest** (see below) |

`_Sync` is a `_`-prefixed satellite segment, so every content filter (the GitHub export filter
and the instance-sync push filter itself) excludes it: the registry — including the token —
never replicates anywhere, and the worker's own status/manifest writes never re-enter the sync
loop. The GUI edits the config through the standard node-content editor bound directly to the
node stream; there is no hand-rolled form or replica.

## Architecture: the registering instance is the sync client

The remote instance needs nothing but its existing MCP surface (`get` / `create` / `update` /
`delete` / `search`, reached through `McpRemoteMeshClient` with `Authorization: Bearer` — the
same client the mirror feature uses). All sync logic runs on the instance that holds the
registration, exactly like a SharePoint/OneDrive sync client:

```
IMeshChangeFeed ──► InstanceSyncCoordinator (IHostedService, one per process)
                       │  config-node events ⇒ start/stop/poke workers
                       │  content events     ⇒ offer to each worker
                       ▼
                InstanceSyncWorker (one per registration)
                  PUSH: coalesce into PendingChanges manifest (stream.Update, durable)
                        └─ drain pipeline (Subject → Throttle → Concat, serialized)
                            └─ per node: Get remote → equal? skip : Create/Update/Delete
                  PULL: periodic sweep — search hits carry version + lastModified,
                        Get only changed nodes, newest-writer-wins, apply as system
```

- **Initial replication** — first drain pushes the whole filtered subtree (root first, so the
  remote provisions the Space partition), then stamps `InitialSyncAt`.
- **Offline accumulation** — a transport failure flips the source to `Offline`, keeps the
  manifest (it lives on the config node, so it survives restarts) and starts a reconnect probe
  with backoff (5s → 60s cap). The probe **is** the feature — "changes accumulate until the
  other instance can be reached again" — not a recovery watchdog for a defect. Entries coalesce
  per path: the drain pushes each node's *current* content, so only the latest pending state
  matters.
- **Restart resume** — the coordinator's boot discovery (one system-identity query for
  `nodeType:InstanceSyncConfig`) restarts every worker; pending manifests drain immediately.
- **Errors are never swallowed** — a remote-rejected node stays in the manifest with
  `Status=Error` + `LastError` on the node (visible in the GUI); a transport failure surfaces
  as `Offline`. Wedges-to-zero: every failure lands on the registration node.

## Loop prevention (the echo problem)

Applying a pulled change locally fires the local change feed, which would re-enter the manifest
and push the change straight back — an infinite ping-pong. Two independent layers stop it:

1. **Consume-once suppression** — the pull-apply registers the local path in an instance-scoped
   registry *before* writing; the very next feed event for that path is swallowed exactly once.
2. **Value-equality convergence guard** — every push and pull first compares content
   (name/type/state + canonical JSON) and drops value-equal writes. Any echo that slips past
   layer 1 terminates after one hop instead of oscillating.

Conflicts resolve **newest-writer-wins** by `LastModified` (ties keep the local side). This is
last-write-wins at node granularity — concurrent edits to the *same node* on both instances
within one sweep interval resolve to the newer stamp, not a field merge.

## Known limitations (v1)

- **Remote deletes don't propagate on pull** — only local deletes push. In a symmetric setup
  (a registration on each side) deletes propagate both ways via each side's push.
- **Clock skew** between instances shifts newest-writer-wins fairness; stamps come from each
  instance's own clock.
- **One coordinator per process** — multi-replica portals would run duplicate workers. The
  equality guard makes duplicate pushes converge, but the intended deployment is the standard
  single-replica portal; a single-activation grain is the follow-up for scale-out.
- The pull sweep enumerates via the remote `search` envelope (now carrying `version` +
  `lastModified` per hit); listings beyond the per-level search cap are walked per-namespace
  and logged loudly when still truncated — never silently dropped.

## Testing

`test/MeshWeaver.InstanceSync.Test` runs the full loop against an in-memory
`IRemoteMeshClient` fake (the interface's sanctioned test seam) with tight intervals:
initial replication, incremental push, offline accumulation → reconnect drain, restart resume,
pull, echo suppression, and direction gating.
