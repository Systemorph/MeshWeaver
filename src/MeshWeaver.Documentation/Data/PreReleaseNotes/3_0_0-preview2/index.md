---
Name: MeshWeaver 3.0.0-preview2
Category: Release Notes
Description: Preview2 of MeshWeaver 3.0 — fully reactive runtime, activity control plane, transparent recompile, MCP OAuth, cross-instance mirror, and the new Social/Memex stack
Icon: Rocket
---

# MeshWeaver 3.0.0-preview2

Five hundred and sixty commits, one preview later. Where **3.0.0-preview1** introduced the new mesh, **preview2** hardens it: the runtime is now reactive top-to-bottom, the platform earns a real *control plane* for long-running operations, NodeTypes recompile themselves transparently, and the MCP server graduates into a proper OAuth-secured surface that other portals can mirror against.

This release is the result of an extensive refactor pass — every Task-returning surface in `src/` was either eliminated or pushed to a sanctioned boundary, with a comprehensive test suite to back each change. Read on for the highlights.

---

## Reactive Everywhere — No `Task<T>` in Hub Code

The single largest theme of this release. Every hub-reachable code path now returns `IObservable<T>` instead of `Task<T>`. Handlers, services, layout areas, navigation, security, persistence, and the kernel all compose with `SelectMany` / `Select` / `Where` / `Timeout` rather than `await`.

- **`IMeshService` write APIs** (`CreateNode`, `UpdateNode`, `DeleteNode`, `MoveNode`) return `IObservable<T>` — call `Subscribe` to fire the side effect.
- **`IMeshStorage` writes** are reactive end-to-end; `GetNodeAsync` is replaced by `IObservable<MeshNode?> GetNode`.
- **`ISecurityService`** is hub-scoped, fully local, observable; `GetPolicy` / `GetPermissionRequest` flow through observable streams with a synchronous fast path for built-in roles and a per-user scope-roles cache.
- **Navigation** exposes `IObservable<NavigationContext?>` — the old event + retry timer is gone.
- **Hub initialization hooks are synchronous** — async hooks were a deadlock magnet; they're now disallowed at the framework level.
- **`hub.Observe(...)`** replaces the obsolete `RegisterCallback` / `AwaitResponse` overloads in production code.

Sanctioned exceptions are exactly two: the persistence boundary (`Observable.FromAsync`) and one-line MCP/SDK adapters that bridge to `Task<string>` for external callers. Everything else stays observable.

The full pattern — including the cold-observable Subscribe contract and the `RequireSubscribeObservable<T>` warning channel that catches missed subscribes at GC — is documented in `Architecture/AsynchronousCalls.md`.

## CQRS Discipline — Read a Node the Right Way

`QueryAsync` / `ObserveQuery` lag — they're eventually-consistent indexes. preview2 introduces and enforces the canonical single-node read primitive:

```csharp
workspace.GetMeshNodeStream(path)        // read or write a specific node
workspace.GetRemoteStream<MeshNode, MeshNodeReference>(addr, new MeshNodeReference())
```

Fifteen call sites that read content via the lagged query path were migrated. `GetMeshNodeStream` auto-dispatches own → local collection → remote and is also the right primitive for *waiting for work to finish*. Full rationale: `Architecture/CqrsAndContentAccess.md`.

## Activity Control Plane

A new architectural pattern — and the new default — for any operation with inputs, multi-step progress, and an output (export, import, compile, mirror, deploy):

- **State-machine semantics** live in node properties: `Status` + `RequestedStatus`. Cancel is `RequestedStatus = Cancelled`, not a `CancelXRequest` message.
- **Just-start dispatch** + **import script templates** for one-shot operations.
- **`ActivityLog` MeshNode** is created at dispatch and streams per-step progress; sync responses inline the log, async ones return a path to it.
- The kernel emits `Progress` to the `ActivityLog` feed via `ILogger` — no global state.
- `WatchControlPlane` helper centralizes the "watch a node, react to RequestedStatus" pattern. Imperative watchers were replaced with pure-Rx `WatchSubmission`.

Reference: `Architecture/ActivityControlPlane.md`.

## Transparent Recompile + Compile Pipeline

NodeType compilation became a first-class control-plane operation:

- **Version-snapshot recompile detection** — every NodeType keeps a `{sourcePath → version}` map of its dependencies. A `SyncedQuery` emission compares against the snapshot and flips `Status = Pending` when anything changes.
- **Per-NodeType compile watcher** + **`Pending` status** drive the pipeline; **`RequestedReleasePath`** lets callers pin a specific release.
- **Release MeshNode** is written on every successful compile; the NodeType Configuration view exposes a *Create Release* button + a *Releases* pane.
- **Reactive grain activation** — NodeType grains start non-blocking via `NodeTypeStreamCache`, eliminating a long-standing Orleans activation deadlock.
- **Cross-silo cache invalidation** broadcasts via the `MeshChangeFeed`.
- **Live progress** — "Compiling \<path\> (Ns)..." shows in the LayoutAreaView while a stream is pending; source-discovery emits diagnostics.
- **In-process `#r "nuget:..."`** — the kernel and NodeTypes resolve NuGet packages in-process. No SDK on the container. The new mesh-local NuGet source (`dist/packages`) is wired in as a feed.
- **Side-menu Sources/Tests** resolve via the same `SyncedQuery` the compiler runs.
- **Roslyn metadata refs** are loaded in parallel, handle-free, with no `Lazy<T>`; first-compile no longer triggers `GC.Collect`.
- **`Source` / `Test`** replace the legacy `_Source` / `_Test` segment names; migration v9 renames existing nodes in-place.

Reference: `Architecture/ScriptExecution.md`, `Architecture/ScriptExecutionDemo.md`.

## Script Execution + Executable Code Nodes

- **Code MeshNodes can now run.** An `IsExecutable` flag surfaces a *Run* button on the overview, gated by the `Execute` permission.
- **Kernel migrated from .NET Interactive to `CSharpScript`** and is now hosted *inside* the Activity MeshNode hub — the standalone `kernel/*` hub is retired.
- **`ExecuteScript`** flows through the node hub; the kernel stays private.
- **Inputs payload + observable kernel executor** — scripts read form-bound inputs via `JsonPointerReference` and stream output through a fully observable executor.
- **`PatchDataRequest`** commits via the source stream; script `Log` calls are wired to the activity feed.
- **Markdown export** runs as a script via a `ScriptDispatch` relay — operations as scripts is now the default for export/import-style work.

## MCP Server — OAuth, Diff, Mirror, Tools

The Model Context Protocol surface is now a proper authenticated server:

- **OAuth discovery + RFC 7591 Dynamic Client Registration** — a minimal authorization server lives in-portal; tokens are issued for one year. MCP authentication is fully separated from the Blazor cookie pipeline (401 + `WWW-Authenticate`, never 302).
- **Per-session satellite hub** — every MCP caller gets its own hosted hub for write-amplified workflows (`DataChangeRequest`, `PatchDataRequest`, scripts).
- **Per-mutation diff** — `Patch` returns a unified diff; `Update` / `Create` / `Delete` / `Move` / `Copy` render a ` ```diff ` block in chat, in addition to the file-only diff Claude renders natively.
- **New tools**: `ExecuteScript`, `Recycle`, `GetDiagnostics`, `Compile` (fire-and-forget recompile via `PatchDataRequest`).
- **Inline Diff/Revert links** appear on every node-modifying tool call chip.
- **Aspire injects** the portal endpoint as `Mcp__BaseUrl`; no hard-coded URLs.

Reference: `AI/McpAuthentication.md`, `AI/ExecuteScript.md`.

## Cross-Instance Mirror

A new operation: **push or pull MeshNodes between MeshWeaver portals over MCP-HTTP**. Built as a hub message (no bespoke verb), driven through standard controls. Supports namespace rewrite on the destination. Reference: `Architecture/CrossInstanceMirror.md`.

## Synced Query Data Source

`IMeshQueryProvider.ObserveQuery` is now wired into `VirtualDataSource`, giving the workspace a live, query-backed collection abstraction:

- **Auto-registered** Sources/Tests collections on every per-node hub.
- **Synchronous delete-notification** path via the change-feed (`NotifyDeleted`).
- **Name-keyed `workspace.GetQuery`** + multi-query union.
- **Distinct providers by Name**; `ObserveQuery` dedupes Initial + live changes by `MeshNode.Path`.

This is what powers the new compile pipeline's source discovery and the side-menu Sources/Tests view. Reference: `DataMesh/SyncedQueryDataSource.md`.

## MeshWeaver.Social — LinkedIn Out of the Box

A new `MeshWeaver.Social` project — skeleton for platform publishing, with **LinkedIn** as the first integration:

- **OAuth-only sign-in** (`r_member_social` and `w_member_social` were dropped — OIDC only).
- **Connect button** on the login page; per-user **/me** convenience endpoint.
- **Pull past posts**, comments, and likes; per-post analytics; auto-create `LinkedInProfile` on first connect.
- **Social Media menu shortcut** on the user node; lazy-create `SocialMediaHub`.
- **PostTelemetry samples** + flat satellite layout under each post.

## Authentication, Login Tracking, and Per-User Routing

- **Unified login tracking** — every login emits a `UserActivity` record, browsable from the user node.
- **Bearer DB-role enrichment** — JWT bearer tokens are enriched with the user's DB role server-side.
- **Post-v10 paths** — legacy `User/{id}` prefix is dropped from access rules and the index page; per-user routing tests fully migrated.
- **API tokens**: `DeleteToken` / `GetTokensForUser` migrated to `IMeshService` + remote stream patterns; edge-case coverage added.
- **Per-user self-assignments** — migration v17 ensures every user gets the right baseline access on upgrade.

## Pinned Areas + UI Polish

- **Pinned areas** — pin any layout area to keep it visible while you navigate.
- **Recycle menu item** + **markdown compilation-error overlay** on broken NodeTypes.
- **Modified-nodes panel** — git-like collapsible header on threads with a tabular layout, inline Diff/Restore, and theme-safe colors.
- **Sub-thread streams** embed cleanly (no recursive layout overflow); duplicate sub-thread embeds removed.
- **Per-message metadata** + thread headers; `UsageContent` forwarded from streaming Azure Claude responses; durations formatted as h/m/s.
- **Code Overview** shows last-executed timestamp and links to activity history.
- **NodeType Configuration view** — Create Release button, Releases nav, MeshNode auditing.
- **Catalog** groups Children by Category (falls back to NodeType).
- **Live "Compiling..."** progress through every page-lookup phase.
- **Chat improvements** — duplicate-submission dedup (500ms window), pin-clear on submit, watcher guard until response cell exists.
- **Blazor portal** auto-reloads stale circuits after deploy; fast-fail on SignalR 404; Monaco AMD-load gate kills a circuit-crash race.

## Autocomplete

- **Instant `@/`** via `UserAccessiblePartitionsCache`.
- **Streaming** end-to-end — `IObservable` Monaco contract, streaming partition fan-out, bounded per-call hub round-trips.
- **Postgres partition keys** themselves now appear as suggestions; partition scope and chat-context visibility honored.
- **Autocomplete tests** split into a dedicated `MeshWeaver.Autocomplete.Test` project.

## Deployment Hardening

- **`tools/deploy.sh`** is the canonical deploy command. **Never** run bare `aspire deploy` — Aspire 13.2 reports success even when the migration container crashes.
- **Hard gates** at startup *and* deploy time refuse to bring up a half-migrated DB.
- **Postgres advisory lock** closes a cross-silo schema-init race.
- **Migration**: `Memex.Database.Migration` is split into one `IMigration` class per version; `launchSettings.json` is included; `db_version` is polled (not container state); the FQDN is passed as an argument.
- **Aspire 13.2.x** requires `Properties/launchSettings.json` with `"commandName": "Project"` on every AppHost-referenced project — preview2 ships those everywhere.

Reference: `Architecture/Deployment.md`, `Architecture/PostgresSchemaArchitecture.md`.

## Performance

- **Storage** — lazy byte-read in `CachingStorageAdapter.DirectorySnapshot`.
- **Compile** — parallel + handle-free `MetadataReference` loading; static-hoisted Roslyn refs.
- **Messaging** — gated per-message log calls; cut per-message + per-dispose hot-path overhead.
- **Security** — synchronous fast path for built-in roles; per-user scope-roles cache; shared synced-query upstream.
- **Test logger** — holds `StreamWriter` open instead of `File.AppendAllText` per line.

## AI Improvements

- **Shape-aware factory routing** — `IChatClientFactory.Supports()` resolves the right provider per message.
- **Agent `PreferredModel`** wins; aggregated factory models; parameterized endpoints.
- **Anthropic Opus** bumped to `claude-opus-4-7`.
- **Streaming**: `UsageContent` emitted from Azure Claude; durations formatted h/m/s.
- **Delegation** tool returns `IAsyncEnumerable<string>`; thread message updates are delta-based.

Reference: `AI/ProviderConfiguration.md`.

## Notable Bug Fixes

- **Address serialization** — `ToFullString()` no longer doubles the host on each round-trip (caused exponential host-chain growth through Orleans).
- **Init gates** — `InitializeHubRequest` and `HeartBeatEvent` bypass the gate at framework level; no more startup deadlocks.
- **Sync-stream gate** — `UpdateStreamRequest` is allowed through; `SubscribeRequest` Observe-timeout is treated as success and disposes cleanly.
- **Routing** — fully reactive `RouteMessageAsync`; `WithRequestIdFrom` on `DeliveryFailure` so the original `Observe` callback matches; race in `StreamFanOutAsync` (pending counter set before tasks ran) closed.
- **Delete pipeline** — four-phase orchestrator with warning confirmation; recursive-delete commit routed through the mesh hub; `MeshOperationOptions.Timeout` honored.
- **Hub init**: framework-level conversion of init hooks to sync — `await` in init = deadlock.
- **Path resolution** — relative paths resolve against the main node on satellite pages; embedded quotes / single quotes / whitespace stripped from agent-emitted paths; satellite partition coverage added.
- **`UpdateMeshNode` write+read consistency** — direct workspace update; no more stale reads after writes.
- **CI stability** — kernel timeouts bumped for cold CI; xunit `methodTimeout` 30s → 60s; many race / timing flakes eliminated.

## Migrating from preview1

- **Replace `Task`-returning surfaces with `IObservable<T>`** in any user code that talked to `IMeshService` / `IMeshStorage` / `ISecurityService` / navigation. The old async extensions are `[Obsolete]`. Subscribe to fire side effects.
- **Replace `QueryAsync(path:X)` / `ObserveQuery` single-node reads** with `workspace.GetMeshNodeStream(path)` (or `GetRemoteStream<MeshNode, MeshNodeReference>` for explicit cross-hub).
- **Replace bespoke `XxxRequest/XxxResponse` operation messages** that have inputs + progress + output with the activity-control-plane pattern (Code MeshNode template + form inputs + `RequestedStatus = Running` trigger).
- **`_Source` / `_Test`** are now `Source` / `Test`. Migration v9 handles existing DBs; update embedded references in your own samples.
- **`Doc` / `Article` NodeTypes** are retired in favour of explicit `Markdown` NodeType + standard YAML frontmatter (`Title` / `Description` aliases supported).
- **MCP clients** must implement OAuth — point them at `/.well-known/oauth-authorization-server` and use Dynamic Client Registration.
- **Deploy** with `tools/deploy.sh prod` (or `tools/deploy.sh test`), never bare `aspire deploy`.
