# AGENTS.md

This file provides guidance to AI agents working with this repository.

## Git Workflow

**NEVER commit or push automatically.** Always wait for the user to explicitly ask.

## Test Triage

When CI fails, **DO NOT run entire test projects** — iterate one test at a time:

1. Read failed test names from CI logs (`gh run view <id> --log`)
2. `dotnet test <project> --filter "FullyQualifiedName~<TestName>" --no-build --no-restore`
3. **No skipping** — CI-only failures catch real timing/state bugs

Full guidance: [WritingTests.md](src/MeshWeaver.Documentation/Data/Architecture/WritingTests.md) · [CqrsAndContentAccess.md](src/MeshWeaver.Documentation/Data/Architecture/CqrsAndContentAccess.md) · [TestStateIsolation.md](src/MeshWeaver.Documentation/Data/Architecture/TestStateIsolation.md)

## GitHub PR Operations

`gh` CLI has **read + push** only — cannot merge, resolve threads, or request reviewers.

```bash
# Find unresolved review threads
gh api graphql -f query='query($owner:String!, $repo:String!, $pr:Int!) { repository(owner:$owner, name:$repo) { pullRequest(number:$pr) { reviewThreads(first:100) { nodes { id isResolved } } } } }' \
  -f owner=Systemorph -f repo=MeshWeaver -F pr=PR_NUMBER \
  --jq '.data.repository.pullRequest.reviewThreads.nodes[] | select(.isResolved==false) | .id'
# Resolve a thread
gh api graphql -f query='mutation($id:ID!){ resolveReviewThread(input:{threadId:$id}){ clientMutationId }}' -f id=THREAD_ID
gh pr merge PR_NUMBER --merge
```

**If `FORBIDDEN`**: re-authenticate with `! gh auth login`.

## 🚨 Postgres: One Schema Per Partition

**`public.mesh_nodes` is empty by design.** Data lives in per-partition schemas (`acme.mesh_nodes`, `rbuergi.mesh_nodes`, etc.).

Satellite table routing by path segment:

| Path segment | Table |
|---|---|
| `…/_Access/…` | `access` |
| `…/_Thread/…` | `threads` |
| `…/_Activity/…` | `activities` |
| `…/_Comment/…`, `_Approval`, `_Tracking` | `annotations` |
| `…/Source/…` or `…/Test/…` | `code` |
| (none) | `mesh_nodes` |

**`namespace` keeps the partition prefix — never strip it.** `namespace = rbuergi/ApiToken`, not `ApiToken`.

**Never run raw `psql UPDATE` on a live portal** — bypasses the workspace cache. Use `MoveNodeRequest` or add a Repair vN migration. If you must SQL-edit, restart `Memex.Portal.Distributed`.

Full reference: [PostgresSchemaArchitecture.md](src/MeshWeaver.Documentation/Data/Architecture/PostgresSchemaArchitecture.md)

## Documentation

All docs embedded in `src/MeshWeaver.Documentation/` and served under `Doc/` at runtime.

| Topic area | Path |
|---|---|
| Architecture | `src/MeshWeaver.Documentation/Data/Architecture/` |
| DataMesh | `src/MeshWeaver.Documentation/Data/DataMesh/` |
| GUI | `src/MeshWeaver.Documentation/Data/GUI/` |
| AI Integration | `src/MeshWeaver.Documentation/Data/AI/` |
| Agent definitions | `src/MeshWeaver.AI/Data/Agent/` |

**Hub-handler test hangs or message disappears:** read [DebuggingMessageFlow.md](src/MeshWeaver.Documentation/Data/Architecture/DebuggingMessageFlow.md) first — it tells you which trace tags to grep and why you should never rerun a hung test "to see".

**`type 'X' is not registered in this hub's TypeRegistry`:** Fix is `WithType(typeof(X), nameof(X))` on the receiving hub. See DebuggingMessageFlow.md → "Type-registry mismatch".

**Use `hub.Observe(...)` not `RegisterCallback`/`AwaitResponse`** — those overloads are `[Obsolete]` and deadlock. Tests use `MonolithMeshTestBase.AwaitResponseAsync(...)`.

## Deployment

**Never run bare `aspire deploy`** — Aspire 13.2 reports success even when the db-migration container crashes. Always use:

```bash
tools/deploy.sh prod   # production
tools/deploy.sh test   # test environment
```

Full reference: [Deployment.md](src/MeshWeaver.Documentation/Data/Architecture/Deployment.md)

## Bash Command Guidelines

**Stay in root** (`C:\dev\MeshWeaver`). Avoid chained commands (`&&`, `||`), `for` loops, and `cd` — they all require user confirmation.

## Development Commands

```bash
dotnet build                                              # Build solution
dotnet test test/MeshWeaver.Data.Test --no-restore        # Run one test project
dotnet run --project memex/Memex.Portal.Monolith          # Dev portal (https://localhost:7122)
dotnet run --project memex/aspire/Memex.AppHost           # Aspire (requires Docker)
aspire run --project memex/aspire/Memex.AppHost           # Aspire via CLI (registers with `aspire mcp`)
```

### Restarting just the Portal (no full Aspire restart)

When you change code in `Memex.Portal.Distributed` or any project it references, you do NOT need to kill the whole AppHost. Three options, ordered by cost:

1. **Hot reload (cheapest)** — start with `dotnet watch` instead of `dotnet run` / `aspire run`:
   ```bash
   dotnet watch --project memex/aspire/Memex.AppHost
   ```
   File save → Aspire restarts the affected resource only. Preserves the dashboard, the Postgres container, and the SignalR endpoints. Most code changes apply within seconds.
2. **Aspire dashboard UI** — open `https://localhost:17200/` → Resources tab → click the ⋯ next to `memex-portal-distributed` → **Restart**. Runs `dotnet build` + restart in-place.
3. **Process kill (last resort, when watch missed a change)**:
   ```powershell
   Get-Process Memex.Portal.Distributed -ErrorAction SilentlyContinue | Stop-Process -Force
   ```
   Aspire's resource watcher detects the exit and restarts the resource within ~5 s. Avoids a full `aspire run` restart (which would also rebuild every other resource and re-launch Postgres / blob-storage containers).

**Don't** kill the whole `aspire` / `Memex.AppHost` process unless you changed AppHost wiring itself — full restart costs 30-60 s and loses the dashboard auth token.

Full reference: [LocalDevWorkflow.md](src/MeshWeaver.Documentation/Data/Architecture/LocalDevWorkflow.md)

## 🚨 NodeMutations: `stream.Update()` only — never request/response

**Threads, thread messages, NodeType compile state, Code editing — every mesh-node mutation goes through `workspace.GetMeshNodeStream(path).Update(current => modified)`. NO bespoke `IRequest`/`IResponse` pairs.**

This is the unification of three rules we used to write separately:

1. **Writes**: `stream.Update(current => current with { Content = ... })`. The owning hub's action block serialises; no race. State-machine semantics? Set a `RequestedX` field — the owning hub's watcher reacts (see [ActivityControlPlane.md](src/MeshWeaver.Documentation/Data/Architecture/ActivityControlPlane.md)).
2. **Reads**: `workspace.GetMeshNodeStream(path)` (server-side, backed by [IMeshNodeStreamCache](src/MeshWeaver.Hosting/MeshNodeStreamCache.cs)) or `workspace.GetRemoteStream<MeshNode, MeshNodeReference>(addr, new MeshNodeReference())` (client/Blazor — see [GUI Data Binding](src/MeshWeaver.Documentation/Data/GUI/DataBinding.md)). Never `meshService.QueryAsync(path:X)` for a single node's content (stale by design).
3. **Delete the request type.** If you find yourself writing `class XxxRequest` to mutate a thread / message / NodeType, stop. Add a `RequestedXxx` field to the node's content and watch it from the owning hub.

Sanctioned exceptions (NOT for state mutations):
- `CreateNodeRequest` / `DeleteNodeRequest` / `MoveNodeRequest` — node-lifecycle on the mesh hub. These route, they don't mutate node content.
- Transient queries that don't belong on any node (e.g. autocomplete completions).

Why this rule unblocks tests: every "hub becomes unresponsive after the second compile" failure (CodeEditRecompile, NodeTypeRelease, LinkedInPullActions, ThreadAgentIntegration in CI 26036857424) traces back to bespoke request/response patterns that race the watcher → two concurrent activities → leaked callbacks → wedged hub.

Canonical references:
- [RequestViaStreamUpdate.md](src/MeshWeaver.Documentation/Data/Architecture/RequestViaStreamUpdate.md) — the canonical pattern + helpers (`hub.WatchControlPlane`, `hub.WatchSubmission`).
- [ActivityControlPlane.md](src/MeshWeaver.Documentation/Data/Architecture/ActivityControlPlane.md) — `Status`/`RequestedStatus` pair, operations-as-scripts.
- [CqrsAndContentAccess.md](src/MeshWeaver.Documentation/Data/Architecture/CqrsAndContentAccess.md) — read semantics + why `QueryAsync` lags.
- [DataBinding.md](src/MeshWeaver.Documentation/Data/GUI/DataBinding.md) — the Blazor-side mirror of the same pattern.

## 🚨 Never write as hub — AccessContext propagation

**Every framework write primitive (`meshService.CreateNode/UpdateNode/DeleteNode/CopyNode`, `MeshNodeStreamHandle.Update`, `IMeshNodeStreamCache.Update`) automatically carries the caller's `AccessContext` through `.Subscribe()` boundaries.** Callers keep writing the natural `.Subscribe(...)` shape; the framework guarantees the operation runs under the calling user's identity even when the emission lands on another thread.

If a write must run as system/hub (legitimate infrastructure — cache hydration, SyncStream heartbeats), wrap explicitly:
- `using (accessService.ImpersonateAsSystem()) { … }` — well-known `"system-security"` identity; `Permission.All` granted unconditionally.
- `using (accessService.ImpersonateAsHub(hub)) { … }` or `o.ImpersonateAsHub(hub.Address)` on the post — stamps the hub's address as principal.

PostPipeline fails closed when no context is set. The "silently stamp hub-self as principal" fallback was deleted 2026-05-21 — it masked the prod EventCalendar bug. Application code that needs to write MUST have a real user identity on `AccessService.Context` (set by MessageHub on every handler invocation from `delivery.AccessContext`).

Canonical reference: [AccessContextPropagation.md](src/MeshWeaver.Documentation/Data/Architecture/AccessContextPropagation.md).

## 🚨 Reactive Pattern — Nothing Async Ever

**No `await`, no `async`, no `Task<T>` in hub-reachable code.** All hub code is `IObservable<T>` end-to-end.

- Handlers, services, layout areas → return `IObservable<T>` (or `void` for fire-and-forget). Never `Task<T>`.
- Compose with `.SelectMany`, `.Select`, `.Where`, `.Timeout`.
- Task-returning primitives: convert at the boundary only via `Observable.FromAsync(() => task)`.
- MCP/SDK surface adapters: one-line `public Task<string> Patch(...) => ops.Patch(...).FirstAsync().ToTask();` is the only sanctioned exception.
- Click actions: `WithClickAction(ctx => { ...; return Task.CompletedTask; })` — never `async ctx =>`.
- `TaskCompletionSource` in hub code = red flag — delete it, return `IObservable<T>`.
- **Tests only**: `await .FirstAsync().ToTask()` is acceptable.

**🚨 Cold observables: Subscribe is mandatory.** Every method that performs a write returns a cold `IObservable<T>` — the side effect runs on `Subscribe`, not on call. Forgetting to subscribe means the work silently doesn't happen.

```csharp
// ❌ WRONG — fire-and-forget. UpdateMeshNode is cold; the dsStream.Update side
//   effect only runs on Subscribe. This was the chat-doesn't-work root cause.
workspace.GetMeshNodeStream().Update(node => node with { … });

// ✅ RIGHT — subscribe with explicit error propagation.
workspace.GetMeshNodeStream().Update(node => node with { … })
    .Subscribe(_ => { }, ex => logger.LogWarning(ex, "Update failed for {Path}", path));
```

`workspace.GetMeshNodeStream()` returns a `MeshNodeStreamHandle` that is both `IObservable<MeshNode>` (read) AND has `.Update(update)` (write). Writes return `RequireSubscribeObservable<MeshNode>` which **logs a warning at GC if Subscribe was never called** — search the `MeshWeaver.Mesh.RequireSubscribe` log channel after every CI run. Old API `workspace.UpdateMeshNode(...)` is `[Obsolete]`.

**Auto-save pattern:** Form fields update the MeshNode via `stream.UpdateMeshNode` (debounced). The click action reads nothing — just flips a trigger field. No `Take(1)` on a hot stream.

Full patterns + mistake ledger: [AsynchronousCalls.md](src/MeshWeaver.Documentation/Data/Architecture/AsynchronousCalls.md)

## 🚨 CQRS — Never Query for a Single Node's Content

`QueryAsync`/`ObserveQuery` are eventually consistent — **stale after writes**. To read a specific node:

```csharp
// ❌ WRONG — lagged index, stale after writes
var node = await mesh.QueryAsync<MeshNode>($"path:{path}").FirstOrDefaultAsync();

// ✅ CORRECT — authoritative, live
workspace.GetRemoteStream<MeshNode, MeshNodeReference>(new Address(path), new MeshNodeReference())
    .Take(1).Timeout(TimeSpan.FromSeconds(10)).Select(change => change.Value);
```

**Valid query uses:** listing children (`path/*`), searching by predicate, existence checks, autocomplete.  
**Wrong:** reading content by exact path, reading state before a write, polling for job completion.

`GetRemoteStream` + `Where(...).Take(1)` is also the right primitive for **waiting for work to finish**.

Full treatment: [CqrsAndContentAccess.md](src/MeshWeaver.Documentation/Data/Architecture/CqrsAndContentAccess.md)

## Mesh URL Shape

`{baseUrl}/{meshpath}` — no `/node/` segment, no URL-encoding of separators.

| Environment | Base URL |
|---|---|
| Prod | `https://memex.meshweaver.cloud` |
| Dev | `http://localhost:5000` (Memex.Portal.Monolith) |

## `@/` is Local-Only

`@/path` is a Unified Content Reference for markdown links (`[text](@/Path)`), autocomplete, and agent tool args — **never in `href=""` attributes or HTTP URLs**. Markdig strips `@` in native markdown syntax but NOT inside `<a href>`.

## Collections Policy

**NEVER use mutable collections.** Always `System.Collections.Immutable`:  
`List<T>` → `ImmutableList<T>`, `Dictionary<K,V>` → `ImmutableDictionary<K,V>`, `HashSet<T>` → `ImmutableHashSet<T>`, `Queue<T>` → `ImmutableQueue<T>`.  
Exception: `ConcurrentDictionary` for concurrent mutation.

## Architecture Overview

Actor-model message hub (`MeshWeaver.Messaging.Hub`) with address-based partitioning. UI is reactive Layout Areas rendered in Blazor Server. AI agents use plugins (MeshPlugin, LayoutAreaPlugin).

| Directory | Contents |
|---|---|
| `src/` | Core framework (50+ projects) |
| `samples/Graph/Data/` | Sample data nodes (ACME, Northwind, Cornerstone, etc.) |
| `memex/Memex.Portal.Monolith/` | Dev portal with full Graph + Documentation support |
| `memex/aspire/` | Microservices with .NET Aspire orchestration |

**Request-Response:** `hub.Observe<TResponse>(request, o => o.WithTarget(address)).Subscribe(resp => …, ex => …)`  
Response sent as: `hub.Post(responseMessage, o => o.ResponseFor(request))`  
**Fire-and-Forget:** `hub.Post(message, o => o.WithTarget(address))`  
**Layout area route:** `@{address}/{areaName}/{areaId}`

## Data Access Patterns

Never use `IMeshStorage` or `IMeshCatalog` directly — internal infrastructure only.

| Operation | API |
|---|---|
| Read (query) | `IMeshService.QueryAsync(...)` |
| Read (single node) | `workspace.GetRemoteStream<MeshNode, MeshNodeReference>(...)` |
| Create/Delete | `IMeshNodeFactory.CreateNodeAsync / DeleteNodeAsync` |
| Update | `hub.Post(new UpdateNodeRequest(node))` |
| Move | `hub.Observe(new MoveNodeRequest(src, dst)).Subscribe(...)` |

Always `GetRequiredService<T>()` — never `GetService<T>()` + null check for required services.

Full reference: [DataAccessPatterns.md](src/MeshWeaver.Documentation/Data/Architecture/DataAccessPatterns.md)

## MCP Mutations — Always Show a Diff

For every MCP mutation (`patch`, `update`, `create`, `delete`, `move`, `copy`):
1. `get @path` **before** — cache the JSON
2. Mutate
3. `get @path` **after** — cache the new JSON
4. Render a ` ```diff ` block showing the changed region in your response

Read-only tools skip this: `get`, `search`, `recycle`, `get_diagnostics`, `navigate_to`, `execute_script`.

## Development Patterns

For detailed patterns with code examples, read:
- Layout areas + UI controls: [UserInterface.md](src/MeshWeaver.Documentation/Data/Architecture/UserInterface.md) and [GUI docs](src/MeshWeaver.Documentation/Data/GUI/)
- Message handling: [MessageBasedCommunication.md](src/MeshWeaver.Documentation/Data/Architecture/MessageBasedCommunication.md)
- AI plugins: [AI docs](src/MeshWeaver.Documentation/Data/AI/)
- Activity control plane / operations as scripts: [ActivityControlPlane.md](src/MeshWeaver.Documentation/Data/Architecture/ActivityControlPlane.md)
- Reactive click handlers + service patterns: [AsynchronousCalls.md](src/MeshWeaver.Documentation/Data/Architecture/AsynchronousCalls.md)

**Static handlers for one-shot pipelines** — don't extract `IFooService` for DI cleanliness when there's no state. Resolve deps via `hub.ServiceProvider.GetRequiredService<T>()` inside the static handler.

**Operations with inputs + progress + output** (export, import, compile, mirror) → Code MeshNode template + form-bound inputs + `RequestedStatus = Running` trigger. Not a bespoke `XxxRequest/XxxResponse` handler. See [ActivityControlPlane.md](src/MeshWeaver.Documentation/Data/Architecture/ActivityControlPlane.md).

## Key Dependencies

.NET 10.0 · Orleans · Blazor Server · Microsoft.Extensions.AI · xUnit v3 · FluentAssertions · Markdig · Chart.js · Azure SDKs

## Testing Guidelines

Before building NodeTypes, data models, layout areas, or CSV loaders — read [Coder.md](src/MeshWeaver.AI/Data/Agent/Coder.md) first (canonical guide + non-negotiable testing standards).

**No mocking.** Use `MonolithMeshTestBase` or `OrleansTestBase` — never mock `IMessageHub`, `IMeshService`, or core interfaces.  
**Always `run_in_background: true`** for test runs (they take minutes).  
**Never `--verbosity minimal`** when tests may fail — it hides stack traces.

**Never `Task.Delay` to wait for propagation.** A fixed sleep races CI load: too short → flakes, too long → wastes minutes across the suite. Wait on the actual condition via `stream.Where(...).FirstAsync().Timeout(...)`. When the source is request/response (not an observable), wrap the re-query in `Observable.Interval(50.Milliseconds()).StartWith(0L).SelectMany(...).Where(predicate).FirstAsync().Timeout(...)`. Hand-rolled `while + Task.Delay(50)` poll loops are forbidden. Sanctioned `Task.Delay` uses: forcing distinct timestamps for sort assertions, and "wait to confirm nothing happened" negative tests where there's no positive signal to filter for. See WritingTests.md → "Polling loops around QueryAsync" for the full pattern.

**Never assert "exactly N change events"** on a stream backed by pg_notify or any change feed that can race the initial-snapshot path. Filter on the emission shape (e.g. `.Where(c => c.ChangeType == QueryChangeType.Initial)`), not the count.

xUnit v3 config (`xunit.runner.json`): `parallelizeAssembly: false`, `maxParallelThreads: 1`, `methodTimeout: 60000ms`.

Full guidance: [WritingTests.md](src/MeshWeaver.Documentation/Data/Architecture/WritingTests.md)

### Running Tests

```bash
dotnet test test/MeshWeaver.Hosting.Monolith.Test --no-restore
dotnet test test/MeshWeaver.Graph.Test --filter "ClassName~AccessAssignment" --no-restore
```

Workflow: run once in background → read failures → fix → run once more. Never re-run to see if it was a flake.

### DevLogin and Access Control

`MonolithMeshTestBase` auto-logs in `rbuergi@systemorph.com` as Admin. Available helpers: `TestUsers.Admin`, `TestUsers.SampleUsers()`, `builder.AddSampleUsers()`.

For per-user access control tests, use `accessService.SetCircuitContext(new AccessContext { ObjectId = "...", Name = "..." })` before creating test data; set `null` after.

### Node Types

Standard types from `AddGraph()`: `Markdown`, `Code`, `Agent`, `Group`, `User`, `VUser`, `Role`, `Notification`, `Approval`, `AccessAssignment`, `GroupMembership`, `PartitionAccessPolicy`, `ActivityLog`, `UserActivity`, `Comment`, `Thread`, `ThreadMessage`

Custom types: `builder.AddMeshNodes(new MeshNode("MyType") { Name = "My Type" })` in `ConfigureMesh`.

### Test Base Classes

- **`MonolithMeshTestBase`** (recommended) — full integration with persistence, messaging, DI; use `AwaitResponseAsync(request, ...)` for request/response in tests
- **`HubTestBase`** — message routing / layout tests; bridge to Task via `.FirstAsync().ToTask(ct)`

For satellite entities (comments, threads, tracked changes): [SatelliteEntityPatterns.md](src/MeshWeaver.Documentation/Data/Architecture/SatelliteEntityPatterns.md)

## Project Structure

Framework code in `src/`, tests in `test/`, samples in `samples/`.  
Main branch: `main`. Solution file: `MeshWeaver.slnx` (50+ projects).  
Package management: `Directory.Packages.props` — update this, not individual `.csproj` files.
