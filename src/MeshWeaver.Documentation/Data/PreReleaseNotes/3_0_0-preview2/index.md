---
Name: MeshWeaver 3.0.0-preview2
Category: Release Notes
Description: Preview 2 of MeshWeaver 3.0 — fully reactive runtime, activity control plane, transparent NodeType recompile, MCP OAuth, cross-instance mirror, and the new Social/Memex stack
Icon: Rocket
---

# MeshWeaver 3.0.0-preview2

Five hundred and sixty commits. Where **preview1** introduced the new mesh, **preview2** hardens it into something you can build on with confidence. The runtime is reactive top-to-bottom, long-running operations have a real control plane, NodeTypes recompile themselves without manual intervention, and the MCP server is now a proper OAuth-secured surface that external portals can mirror against.

This release is the result of a thorough refactor pass. Every `Task`-returning surface in `src/` was either eliminated or pushed to a narrow sanctioned boundary, with a test suite backing each change. The highlights follow.

---

## Reactive Everywhere — No `Task<T>` in Hub Code

The most pervasive theme of this release. Every hub-reachable code path now returns `IObservable<T>` instead of `Task<T>`. Handlers, services, layout areas, navigation, security, persistence, and the kernel all compose with `SelectMany` / `Select` / `Where` / `Timeout` — never `await`.

| Surface | Change |
|---|---|
| `IMeshService` write APIs | `CreateNode`, `UpdateNode`, `DeleteNode`, `MoveNode` return `IObservable<T>` — call `Subscribe` to fire the side effect |
| `IMeshStorage` | Writes are reactive end-to-end; `GetNodeAsync` replaced by `IObservable<MeshNode?> GetNode` |
| `PermissionEvaluator` | Hub-scoped, fully local, observable; synchronous fast path for built-in roles; per-user scope-roles cache |
| Navigation | Exposes `IObservable<NavigationContext?>` — the old event + retry timer is gone |
| Hub initialization hooks | Now synchronous at the framework level — async hooks were a deadlock magnet |
| `hub.Observe(...)` | Replaces the obsolete `RegisterCallback` / `AwaitResponse` overloads |

Sanctioned exceptions are exactly two: the persistence boundary (`Observable.FromAsync`) and one-line MCP/SDK adapters that bridge to `Task<string>` for external callers. Everything else stays observable.

> The cold-observable Subscribe contract and the `RequireSubscribeObservable<T>` warning channel that catches missed subscribes at GC are documented in [AsynchronousCalls.md](@/Architecture/AsynchronousCalls).

---

## CQRS Discipline — Reading a Node Correctly

`QueryAsync` / `ObserveQuery` are eventually-consistent indexes — they lag after writes. preview2 establishes and enforces the canonical single-node read primitive:

```csharp
// Read or write a specific node (own, local, or remote — auto-dispatched)
workspace.GetMeshNodeStream(path)

// Explicit cross-hub variant
workspace.GetRemoteStream<MeshNode, MeshNodeReference>(addr, new MeshNodeReference())
```

Fifteen call sites that read content via the lagged query path were migrated. `GetMeshNodeStream` auto-dispatches own → local collection → remote and is also the right primitive for *waiting for work to finish*.

> Full rationale and migration guide: [CqrsAndContentAccess.md](@/Architecture/CqrsAndContentAccess).

---

## Activity Control Plane

Operations with inputs, multi-step progress, and an output (export, import, compile, mirror, deploy) now follow a single architectural pattern instead of bespoke `XxxRequest/XxxResponse` pairs.

- **State-machine semantics** live in node properties: `Status` + `RequestedStatus`. Cancelling is `RequestedStatus = Cancelled` — no `CancelXRequest` message.
- **`ActivityLog` MeshNode** is created at dispatch and streams per-step progress. Sync responses inline the log; async ones return a path to it.
- The kernel emits `Progress` to the `ActivityLog` feed via `ILogger` — no global state.
- `WatchControlPlane` centralises the "watch a node, react to `RequestedStatus`" pattern. Imperative watchers were replaced with pure-Rx `WatchSubmission`.

> Reference: [ActivityControlPlane.md](@/Architecture/ActivityControlPlane).

---

## Transparent NodeType Recompile

NodeType compilation is now a first-class, self-driving control-plane operation.

**How it works.** Every NodeType keeps a `{sourcePath → version}` snapshot of its dependencies. When a `SyncedQuery` emission detects a change, the NodeType automatically flips to `Status = Pending` and the compile pipeline fires — no manual trigger needed.

Additional improvements:

- **`RequestedReleasePath`** lets callers pin a specific historical release.
- **Release MeshNode** is written on every successful compile; the Configuration view exposes a *Create Release* button and a *Releases* pane.
- **Reactive grain activation** — NodeType grains start non-blocking via `NodeTypeStreamCache`, eliminating a long-standing Orleans activation deadlock.
- **Cross-silo cache invalidation** broadcasts via the `MeshChangeFeed`.
- **Live "Compiling \<path\> (Ns)..."** progress shows in the LayoutAreaView while a stream is pending.
- **In-process `#r "nuget:..."`** — NuGet packages resolve in-process; a new mesh-local source (`dist/packages`) is wired in as a feed.
- **Parallel, handle-free `MetadataReference` loading** — no `Lazy<T>`, no `GC.Collect` on first compile.
- **`Source` / `Test`** replace the legacy `_Source` / `_Test` segment names; migration v9 renames existing nodes in-place.

> Reference: [ScriptExecution.md](@/Architecture/ScriptExecution), [ScriptExecutionDemo.md](@/Architecture/ScriptExecutionDemo).

---

## Script Execution and Executable Code Nodes

- **Code MeshNodes can now run.** An `IsExecutable` flag surfaces a *Run* button on the overview, gated by the `Execute` permission.
- **Kernel migrated from .NET Interactive to `CSharpScript`**, hosted inside the Activity MeshNode hub. The standalone `kernel/*` hub is retired.
- **`ExecuteScript`** flows through the node hub; the kernel stays private.
- **Inputs payload + observable kernel executor** — scripts read form-bound inputs via `JsonPointerReference` and stream output through a fully observable executor.
- **`PatchDataRequest`** commits via the source stream; `Log` calls are wired to the activity feed.
- **Markdown export** runs as a script via a `ScriptDispatch` relay — *operations as scripts* is now the default pattern for export/import-style work.

---

## MCP Server — OAuth, Diff, Mirror, and New Tools

The Model Context Protocol surface graduates into a proper authenticated server.

**Authentication.** OAuth discovery and RFC 7591 Dynamic Client Registration are now built in — a minimal authorization server lives in-portal and issues tokens for one year. MCP authentication is cleanly separated from the Blazor cookie pipeline: unauthenticated requests receive `401 + WWW-Authenticate`, never a `302` redirect.

**Per-session satellite hub.** Every MCP caller gets its own hosted hub for write-amplified workflows (`DataChangeRequest`, `PatchDataRequest`, scripts).

**Mutations show diffs.** `Patch` returns a unified diff; `Update` / `Create` / `Delete` / `Move` / `Copy` render a ` ```diff ` block in chat. Inline *Diff* / *Revert* links appear on every node-modifying tool call chip.

**New tools.** `ExecuteScript`, `Recycle`, `GetDiagnostics`, and `Compile` (fire-and-forget recompile via `PatchDataRequest`).

**Deployment.** Aspire injects the portal endpoint as `Mcp__BaseUrl` — no hard-coded URLs.

> Reference: [McpAuthentication.md](@/AI/McpAuthentication), [ExecuteScript.md](@/AI/ExecuteScript).

---

## Cross-Instance Mirror

A new first-class operation: **push or pull MeshNodes between MeshWeaver portals over MCP-HTTP**. Built as a standard hub message (no bespoke verb), driven through the usual layout controls. Namespace rewrite on the destination is supported.

> Reference: [CrossInstanceMirror.md](@/Architecture/CrossInstanceMirror).

---

## Synced Query Data Source

`IMeshQueryProvider.ObserveQuery` is now wired into `VirtualDataSource`, giving the workspace a live, query-backed collection abstraction:

- **Auto-registered** Sources/Tests collections on every per-node hub.
- **Synchronous delete-notification** path via the change-feed (`NotifyDeleted`).
- **Name-keyed `workspace.GetQuery`** plus multi-query union.
- **Distinct providers by Name**; `ObserveQuery` dedupes Initial and live changes by `MeshNode.Path`.

This powers the new compile pipeline's source discovery and the side-menu Sources/Tests view.

> Reference: [SyncedQueryDataSource.md](@/DataMesh/SyncedQueryDataSource).

---

## MeshWeaver.Social — LinkedIn Out of the Box

A new `MeshWeaver.Social` project provides a skeleton for platform publishing. LinkedIn is the first integration:

- **OAuth-only sign-in** (`r_member_social` and `w_member_social` are dropped — OIDC only).
- **Connect button** on the login page; per-user `/me` convenience endpoint.
- Pull past posts, comments, and likes; per-post analytics; auto-create `LinkedInProfile` on first connect.
- Social Media menu shortcut on the user node; lazy-create `SocialMediaHub`.
- `PostTelemetry` samples + flat satellite layout under each post.

---

## Authentication, Login Tracking, and Per-User Routing

- **Unified login tracking** — every login emits a `UserActivity` record, browsable from the user node.
- **Bearer DB-role enrichment** — JWT bearer tokens are enriched with the user's DB role server-side.
- **Post-v10 paths** — legacy `User/{id}` prefix dropped from access rules and the index page; per-user routing tests fully migrated.
- **API tokens** — `DeleteToken` / `GetTokensForUser` migrated to `IMeshService` + remote stream patterns; edge-case coverage added.
- **Per-user self-assignments** — migration v17 ensures every user gets the correct baseline access on upgrade.

---

## Pinned Areas and UI Polish

- **Pinned areas** — pin any layout area to keep it visible while you navigate.
- **Recycle menu item** + markdown compilation-error overlay on broken NodeTypes.
- **Modified-nodes panel** — git-like collapsible header on threads, tabular layout, inline Diff/Restore, theme-safe colors.
- **Sub-thread streams** embed cleanly (no recursive layout overflow); duplicate sub-thread embeds removed.
- **Per-message metadata** + thread headers; `UsageContent` forwarded from streaming Azure Claude responses; durations formatted as h/m/s.
- **Code Overview** shows the last-executed timestamp and links to activity history.
- **NodeType Configuration view** — Create Release button, Releases nav, MeshNode auditing.
- **Catalog** groups Children by Category (falls back to NodeType).
- **Live "Compiling..."** progress through every page-lookup phase.
- **Chat** — duplicate-submission dedup (500 ms window), pin-clear on submit, watcher guard until the response cell exists.
- **Blazor portal** auto-reloads stale circuits after deploy; fast-fail on SignalR 404; Monaco AMD-load gate eliminates a circuit-crash race.

---

## Autocomplete

- **Instant `@/`** via `UserAccessiblePartitionsCache`.
- **Streaming end-to-end** — `IObservable` Monaco contract, streaming partition fan-out, bounded per-call hub round-trips.
- **Postgres partition keys** appear as suggestions; partition scope and chat-context visibility are honoured.
- Autocomplete tests split into a dedicated `MeshWeaver.Autocomplete.Test` project.

---

## Deployment Hardening

> **🚨 Always use `tools/deploy.sh`.** Never run bare `aspire deploy` — Aspire 13.2 reports success even when the migration container crashes.

```bash
tools/deploy.sh prod   # production
tools/deploy.sh test   # test environment
```

Additional hardening in this release:

- **Hard gates** at startup *and* deploy time refuse to bring up a half-migrated database.
- **Postgres advisory lock** closes a cross-silo schema-init race.
- **`Memex.Database.Migration`** refactored into one `IMigration` class per version; `launchSettings.json` is included; `db_version` is polled (not container state); the FQDN is passed as an argument.
- **Aspire 13.2.x** requires `Properties/launchSettings.json` with `"commandName": "Project"` on every AppHost-referenced project — preview2 ships those everywhere.

> Reference: [Deployment.md](@/Architecture/Deployment), [PostgresSchemaArchitecture.md](@/Architecture/PostgresSchemaArchitecture).

---

## Performance

| Area | Improvement |
|---|---|
| Storage | Lazy byte-read in `CachingStorageAdapter.DirectorySnapshot` |
| Compile | Parallel, handle-free `MetadataReference` loading; static-hoisted Roslyn refs |
| Messaging | Gated per-message log calls; cut per-message and per-dispose hot-path overhead |
| Security | Synchronous fast path for built-in roles; per-user scope-roles cache; shared synced-query upstream |
| Test logger | Holds `StreamWriter` open instead of `File.AppendAllText` per line |

---

## AI Improvements

- **Shape-aware factory routing** — `IChatClientFactory.Supports()` resolves the right provider per message shape.
- **Agent `PreferredModel`** wins; aggregated factory models; parameterised endpoints.
- **Anthropic Opus** bumped to `claude-opus-4-7`.
- **Streaming** — `UsageContent` emitted from Azure Claude; durations formatted as h/m/s.
- **Delegation tool** returns `IAsyncEnumerable<string>`; thread message updates are delta-based.

> Reference: [ProviderConfiguration.md](@/AI/ProviderConfiguration).

---

## Notable Bug Fixes

| Area | Fix |
|---|---|
| Address serialization | `ToFullString()` no longer doubles the host on each round-trip — previously caused exponential host-chain growth through Orleans |
| Init gates | `InitializeHubRequest` and `HeartBeatEvent` bypass the gate at framework level; no more startup deadlocks |
| Sync-stream gate | `UpdateStreamRequest` allowed through; `SubscribeRequest` Observe-timeout treated as success and disposes cleanly |
| Routing | Fully reactive `RouteMessageAsync`; `WithRequestIdFrom` on `DeliveryFailure` so original `Observe` callback matches; race in `StreamFanOutAsync` closed |
| Delete pipeline | Four-phase orchestrator with warning confirmation; recursive-delete commit routed through the mesh hub; `MeshOperationOptions.Timeout` honoured |
| Hub init | Framework-level conversion of init hooks to sync — `await` in init = deadlock |
| Path resolution | Relative paths resolve against the main node on satellite pages; embedded quotes/whitespace stripped from agent-emitted paths; satellite partition coverage added |
| `UpdateMeshNode` consistency | Direct workspace update — no more stale reads after writes |
| CI stability | Kernel timeouts bumped for cold CI; xunit `methodTimeout` 30s → 60s; numerous race/timing flakes eliminated |

---

## Migrating from preview1

1. **Replace `Task`-returning surfaces with `IObservable<T>`** in any code that calls `IMeshService` / `IMeshStorage` / `PermissionEvaluator` / navigation. The old async extensions are `[Obsolete]`. Subscribe to fire side effects.

2. **Replace `QueryAsync(path:X)` / `ObserveQuery` single-node reads** with `workspace.GetMeshNodeStream(path)` (or `GetRemoteStream<MeshNode, MeshNodeReference>` for explicit cross-hub).

3. **Replace bespoke `XxxRequest/XxxResponse` operation messages** that have inputs + progress + output with the activity-control-plane pattern: Code MeshNode template + form inputs + `RequestedStatus = Running` trigger.

4. **`_Source` / `_Test` → `Source` / `Test`.** Migration v9 handles existing DBs; update embedded references in your own samples.

5. **`Doc` / `Article` NodeTypes are retired** in favour of the explicit `Markdown` NodeType with standard YAML frontmatter (`Title` / `Description` aliases are supported).

6. **MCP clients must implement OAuth.** Point them at `/.well-known/oauth-authorization-server` and use Dynamic Client Registration.

7. **Deploy with `tools/deploy.sh prod`** (or `tools/deploy.sh test`). Never bare `aspire deploy`.
