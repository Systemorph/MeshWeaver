# AGENTS.md

This file provides guidance to AI agents when working with code in this repository.

## Git Workflow

**NEVER commit or push automatically.** Always wait for the user to explicitly ask for a commit or push. Present changes for review first.

## GitHub PR Operations

The `gh` CLI token has **read + push** permissions but **cannot** merge PRs, resolve review threads, or request reviewers. For these operations:

### Resolve review threads + merge via GraphQL
```bash
# 1. Find unresolved threads
gh api graphql -f query='
query($owner:String!, $repo:String!, $pr:Int!) {
  repository(owner:$owner, name:$repo) {
    pullRequest(number:$pr) {
      reviewThreads(first:100) {
        nodes { id isResolved }
      }
    }
  }
}' -f owner=Systemorph -f repo=MeshWeaver -F pr=PR_NUMBER \
  --jq '.data.repository.pullRequest.reviewThreads.nodes[] | select(.isResolved==false) | .id'

# 2. Resolve each thread
gh api graphql -f query='mutation($id:ID!){ resolveReviewThread(input:{threadId:$id}){ clientMutationId }}' -f id=THREAD_ID

# 3. Merge
gh pr merge PR_NUMBER --merge
```

**If these fail with `FORBIDDEN`**, the token lacks write scope — do it from the GitHub UI or re-authenticate with `! gh auth login`.

## Documentation

Documentation is embedded in `src/MeshWeaver.Documentation/` and served under the `Doc/` namespace at runtime.

### Architecture

The documentation on the architecture is accessible via src/MeshWeaver.Documentation/Data/Architecture/

Topics: Message-based communication, Actor model, UI streaming, AI agents, Data versioning, Serialization, Access control, Partitioned persistence, Business rules & calculations, **Debugging message flow**

**When a hub-handler test hangs or a message disappears: read `Doc/Architecture/DebuggingMessageFlow.md` first.** It tells you exactly which trace tags to grep, where to crank the log levels, and **why you should never rerun a hung test 2-3 times "to see"** — the framework already prints a structured `MESSAGE_FLOW:` trace at `Trace` level. Run once, grep, fix.

### `No handler found for message type X` = type-registry mismatch

The handler is almost certainly registered. The message just arrived deserialized as a different CLR type (or `JsonElement`) because one side's `ITypeRegistry` is missing `WithType(typeof(X), nameof(X))`. Check sender + receiver registry parity first; for Orleans, that includes both silo hub config and any client/portal hub posting the request.

### Request/response: `hub.Observe(...)` — NOT `RegisterCallback` / `AwaitResponse`

The Task-returning `IMessageHub.RegisterCallback(...)` and `IMessageHub.AwaitResponse(...)` overloads are `[Obsolete]`. Production code uses `hub.Observe(request, options?)` (returns `IObservable<IMessageDelivery<TResponse>>`) — DeliveryFailure flows via `OnError`; no Task-await deadlock; no silently-skipped callback. Tests use `MonolithMeshTestBase.AwaitResponseAsync(request, ...)` which is a thin Task wrapper for ergonomic test code only.

**NEVER** `Observable.FromAsync(() => hub.RegisterCallback(...))` — bridges the Task back into Rx and the continuation captures sync-context → deadlock. `hub.Observe(...)` uses `task.ToObservable()` which is a different operator (no func re-invocation, no scheduler capture).

### DataMesh

The documentation on the data mesh is accessible via src/MeshWeaver.Documentation/Data/DataMesh/

Topics: Node type configuration, Query syntax, Unified Path references, Interactive markdown, Collaborative editing, CRUD operations, Data modeling

### GUI

The documentation on the GUI is accessible via src/MeshWeaver.Documentation/Data/GUI/

Topics: Container controls (Stack, Tabs, Toolbar, Splitter), Layout grid, DataGrid, Editor, Observables, Data binding, Attributes, Reactive dialogs

### AI Integration

The documentation on AI integration is accessible via src/MeshWeaver.Documentation/Data/AI/

Topics: Agentic AI, MCP authentication, MeshPlugin tools (Get, Search, Create, Update, Delete, NavigateTo)

### Deployment

The documentation on deployment is accessible via src/MeshWeaver.Documentation/Data/Architecture/Deployment.md

Topics: Aspire CLI deployment, deployment modes (local/test/prod/monolith), secrets management, Azure Container Apps, PostgreSQL, Orleans clustering, infrastructure provisioning

**Quick deploy commands** (run from repo root):
- **Prod**: `aspire deploy --project memex/aspire/Memex.AppHost/Memex.AppHost.csproj -- --mode prod`
- **Test**: `aspire deploy --project memex/aspire/Memex.AppHost/Memex.AppHost.csproj -- --mode test`

Prerequisites: Azure CLI authenticated, Aspire CLI installed, Docker running. See the full deployment doc for details.

### Agents

Built-in agent definitions are embedded in src/MeshWeaver.AI/Data/Agent/

Agents: Executor, Navigator, Planner, Research

## Bash Command Guidelines

**Stay in the root directory** (`C:\dev\MeshWeaver`) and use simple, single commands. Chained commands (`&&`, `||`), `for` loops, and `cd` all require user confirmation — avoid them.
```bash
# CORRECT — simple single commands from root directory
dotnet build src/MeshWeaver.Graph/MeshWeaver.Graph.csproj
dotnet test test/MeshWeaver.Graph.Test --no-build

# WRONG — these all require extra approval:
cd /c/dev/MeshWeaver && dotnet build    # chained cd
for d in test/*; do dotnet test $d; done  # for loop
dotnet build && dotnet test               # chained commands
```

## Development Commands

### Build and Test
```bash
# Build entire solution
dotnet build

# Run tests (uses xUnit v3)
dotnet test

# Run specific test project (example)
dotnet test test/MeshWeaver.Data.Test/MeshWeaver.Data.Test.csproj

# Clean solution
dotnet clean

# Restore packages
dotnet restore
```

### Running Applications

#### Memex Portal (Recommended for Development)
```bash
dotnet run --project memex/Memex.Portal.Monolith
# Access at https://localhost:7122
```

The Memex Portal uses `AddGraph()` to dynamically load Graph nodes from `samples/Graph/Data/`, and `AddDocumentation()` to serve embedded documentation under the `Doc/` namespace. This is the recommended portal for development.

#### Microservices Portal (.NET Aspire)
```bash
dotnet run --project memex/aspire/Memex.AppHost
# Access Aspire dashboard for service management
# Requires Docker for dependencies
```

## 🚨 Reactive Pattern — NOTHING ASYNC EVER (READ THIS FIRST)

> **RULE: no `await`, no `async`, no `Task<T>` return types anywhere in hub-reachable
> code. Period. No exceptions for "just this small bit".** Mesh code is `IObservable<T>`
> end-to-end. `async` + `await` looks innocent and deadlocks the mesh. Every recent
> "ExecuteScript times out", "Patch hangs", "click does nothing" incident traced to
> someone (usually me) sliding an `await` into a path that eventually flows through a
> hub handler. **Stop doing it.**

**What this actually means:**

- **Return types**: public methods on `MeshOperations`, handlers, services, layout
  areas → `IObservable<T>` (or `void` for fire-and-forget). Never `Task<T>`.
- **Internals**: compose with `.SelectMany`, `.Select`, `.Where`, `.Timeout`. Convert
  Task-returning primitives at the boundary with `Observable.FromAsync(() => task)`
  — but never `await` the task yourself inside hub flow.
- **MCP / external-SDK boundaries** that MUST return `Task<T>` (because the SDK
  requires it): acceptable as a *single adapter layer* at the surface — e.g.
  `public Task<string> Patch(...) => ops.Patch(...).FirstAsync().ToTask();`. Keep
  the body of that adapter one line. The hub work itself still lives on
  `IObservable<T>`.
- **Click actions**: synchronous — `WithClickAction(ctx => { ...; return Task.CompletedTask; })`.
  Never `async ctx =>`.
- **Tests**: the single exception. Test code MAY `await` to block until a stream
  emits (`.FirstAsync().ToTask()`). Everywhere else, no.

**The canonical mistake ledger (what has blown up recently):**

- `Patch` TCS hang — `SerialisePretty` threw inside `Subscribe`, TCS never resolved,
  xUnit fact timed out at 30 s. Fix: `Observable.FromAsync` + composed chain, not
  Subscribe-with-TCS-callback.
- `ExecuteScript` silent timeout — `AwaitResponse` inside a tool method, response
  never routed back to the scope that awaited.
- Kernel grain activation loop — `await contentService.GetContentAsync(...)` inside
  a script deadlocked the kernel's action block.
- Any `TaskCompletionSource` in hub-reachable code is a code smell — a 99%-of-the-time
  sign that someone is trying to bridge `IObservable` → `Task` inside the hub flow.
  Delete it and return `IObservable<T>` instead.

**If you catch yourself reaching for `async`/`await`/`Task<T>` in hub code: stop,
refactor to `IObservable<T>`, and submit a PR without the async. "But this is a small
helper" / "just a one-liner wrapper" / "the MCP SDK needs Task" are all traps — the
wrapper either becomes part of the hot path or someone copies it into one.**

## 🚨 CQRS — never query for a single node's content

> **Queries (`QueryAsync` / `ObserveQuery`) bring sets of elements. Nothing more.**
> To read the *content* of a specific node, **never use a query** — use
> `workspace.GetRemoteStream<MeshNode, MeshNodeReference>(address, new MeshNodeReference())`.

Queries route through a read-side index that is **eventually consistent** — it lags
behind writes. Using `mesh.QueryAsync($"path:X").FirstOrDefaultAsync()` (or any
`Observable.FromAsync(() => ...)` wrapper around it) to read `X` will sometimes
return stale content right after a write. That's the bug class this rule prevents.

```csharp
// ❌ WRONG — indexed read path, lagged, stale just after writes.
var node = await mesh.QueryAsync<MeshNode>($"path:{path}").FirstOrDefaultAsync();

// ❌ WRONG — same bug wrapped in Observable.FromAsync to look reactive.
return Observable.FromAsync(ct =>
    mesh.QueryAsync<MeshNode>($"path:{path}").FirstOrDefaultAsync(ct).AsTask());

// ✅ CORRECT — direct subscription to the owning hub's workspace. Authoritative,
//    live (you get future updates too), no staleness.
var workspace = hub.GetWorkspace();
return workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
        new Address(path), new MeshNodeReference())
    .Take(1)
    .Timeout(TimeSpan.FromSeconds(10))
    .Select(change => change.Value);
```

**Valid query uses:**
- Listing children of a namespace (`path/*`)
- Searching by predicate (`nodeType:X`, `name:*sales*`)
- Checking existence (did any node match?)
- Autocomplete / browsing

**Always-wrong query uses:**
- Getting a node by exact path so you can read its content
- Reading the current state before a Patch/Update
- "Wait for this script/job to finish" (use `GetRemoteStream` and `Where(...).Take(1)` on the completion condition)

`GetRemoteStream` is also the right primitive for **waiting for work to finish** —
subscribe until a completion field flips in the node's content, then `Take(1)`.
Queries polled in a loop would lag on every tick.

Full treatment: `Doc/Architecture/CqrsAndContentAccess.md`.

### The three building blocks

1. **`IMeshService.CreateNode / UpdateNode / DeleteNode` return `IObservable<T>`** (NOT `Task<T>`). They internally `hub.Post` + `hub.Observe`. Subscribe to drive them — never call `.ToTask()` / `.FirstAsync()` / `await` on them from a click action or hub handler.
2. **Click actions must be synchronous**: `WithClickAction(ctx => { ...; return Task.CompletedTask; })`. Never `async ctx => await ...`.
3. **Read form data via `Subscribe(...)` with `Take(1)`**, not `await FirstAsync()`. The data stream emits its current value synchronously on subscribe.

### The canonical reactive click handler

```csharp
.WithClickAction(ctx =>
{
    // Immediate optimistic UI feedback — the click registered.
    ctx.Host.UpdateData(resultId, "<p>Working…</p>");

    // Read form data via Subscribe (sync emission for BehaviorSubject-style streams).
    ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formId)
        .Take(1)
        .Subscribe(data =>
        {
            var label = data?.GetValueOrDefault("label")?.ToString() ?? "";
            if (string.IsNullOrEmpty(label))
            {
                ctx.Host.UpdateData(resultId, "<p>Please enter a label.</p>");
                return;
            }

            // Reactive service call — returns IObservable<T>, no await.
            // Service internally composes meshService.CreateNode/UpdateNode/DeleteNode chains.
            myService.DoWork(label).Subscribe(
                result => ctx.Host.UpdateData(resultId, $"<p>Done: {result}</p>"),
                ex     => ctx.Host.UpdateData(resultId, $"<p>Error: {ex.Message}</p>"));
        });

    return Task.CompletedTask;  // ← click action itself is sync
})
```

### Writing reactive services

Compose `IObservable` chains with `SelectMany`, `Select`, `FirstOrDefaultAsync`. Return `IObservable<T>` (not `Task<T>`) from any method that will be called from a hub handler or click action.

```csharp
public IObservable<TokenCreationResult> CreateToken(...)
{
    var userNode = new MeshNode(...);
    return nodeFactory.CreateNode(userNode)                  // IObservable<MeshNode>
        .SelectMany(created =>
        {
            var indexNode = new MeshNode(...) { ... };
            return nodeFactory.CreateNode(indexNode)         // chain the second write
                .Select(_ => new TokenCreationResult(raw, created));
        });
    // No await anywhere. The consumer calls .Subscribe(onNext, onError).
}

// Wrap IAsyncEnumerable queries into observables:
public IObservable<bool> DeleteToken(string path) =>
    Observable.FromAsync(() =>
            meshQuery.QueryAsync<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}"))
                     .FirstOrDefaultAsync().AsTask())
        .SelectMany(node =>
        {
            /* ... */
            return nodeFactory.DeleteNode(path);             // IObservable<bool>
        });
```

### What NOT to do

```csharp
// ❌ DEADLOCKS the hub under load.
.WithClickAction(async ctx =>
{
    var data = await ctx.Host.Stream.GetDataStream<T>(id).FirstAsync();
    var result = await myService.DoWorkAsync(data);  // never awaiting hub-backed services
    ctx.Host.UpdateData(resultId, result);
})

// ❌ Task.Run is a crutch, not a fix — identity doesn't flow, failures are invisible.
.WithClickAction(ctx =>
{
    _ = Task.Run(async () => { await myService.DoWorkAsync(); });
    return Task.CompletedTask;
})

// ❌ Hub handlers must NOT await mesh writes either.
public async Task<IMessageDelivery> HandleFoo(IMessageDelivery<FooRequest> req)
{
    await meshService.CreateNodeAsync(...);   // deadlock risk
    return req.Processed();
}
```

### When `await` IS acceptable

- Top-level app startup code (`Main`, `ConfigureServices`, `InitializeAsync` of test base classes).
- Pure CPU / file-I/O work that does NOT flow through the hub (e.g., `File.ReadAllTextAsync`).
- Test code that explicitly wants to block until a stream emits (use `.FirstAsync().ToTask()` then await, but only in tests).

**Everywhere else, the shape is `Subscribe(onNext, onError)`.** If a service you need only exposes `…Async` / `Task<T>`, add a reactive overload that returns `IObservable<T>` and refactor.

## Mesh URL shape

Browser URLs are `{baseUrl}/{meshpath}` — the mesh path is appended directly to the base URL. **No `/node/` segment, no URL-escaping of path separators.**

| Environment | Base URL |
|---|---|
| Prod | `https://memex.meshweaver.cloud` |
| Dev | `http://localhost:5000` (Memex.Portal.Monolith) |
| Test | Same host as the deployed test ACA |

Examples:

- Prod ACME Pricing: `https://memex.meshweaver.cloud/Systemorph/FutuRe/EuropeRe/AcmeSubmission2025`
- Prod ACME Pricing Triangle view: `https://memex.meshweaver.cloud/Systemorph/FutuRe/EuropeRe/AcmeSubmission2025/Triangle`
- A content-collection file: `https://memex.meshweaver.cloud/Systemorph/FutuRe/EuropeRe/content/LargeClaims.xlsx`

The MCP server's `NavigateTo` tool and the `GetBaseUrl` tool both honour this shape. If you ever see a URL like `{host}/node/Foo%2FBar` in agent output, it's a bug — `NavigateTo` should return `{host}/Foo/Bar` with real slashes.

## `@/` is Local-Only — Never in HTTP URLs or href Attributes

The `@/path` prefix is a **Unified Content Reference (UCR)** used exclusively for:
- Native markdown link syntax: `[text](@/Path)` — Markdig's `LinkUrlCleanupExtension` strips the `@` and resolves the path to an absolute URL.
- Autocomplete / path pickers inside the mesh (chat, mention fields).
- Tool arguments for agent plugins (`Get('@/Path')`, `Search(...)`, `NavigateTo(...)`).

`@/` **MUST NOT** appear in:
- `href="..."` attributes when writing raw HTML inside markdown (the markdown renderer does NOT reach inside `<a href>` — the `@/` will leak to the browser and produce `https://host/@/Path`, which 404s or misroutes).
- External / HTTP URLs anywhere in code, content, or documentation.
- Razor component navigation targets (`NavigateTo("/path")`, not `NavigateTo("/@/path")`).

**Rule of thumb:** `@/` is for things the mesh resolves locally. Once it's in a URL bar or an `href`, the `@` is wrong. Write `href="/Systemorph/X"` — not `href="@/Systemorph/X"`.

A safety-net redirect `GET /@/X → GET /X` (301) is in place in `MemexConfiguration.StartMemexApplication`, but authoring content with `@/` in raw HTML hrefs is still a bug — fix it at the source.

## Collections Policy

**NEVER use mutable collections.** Always use `System.Collections.Immutable`:
- `List<T>` → `ImmutableList<T>.Empty` + `= list.Add(item)`
- `Dictionary<K,V>` → `ImmutableDictionary<K,V>.Empty` + `= dict.SetItem(key, val)`
- `HashSet<T>` → `ImmutableHashSet<T>.Empty` + `= set.Add(item)`
- `Queue<T>` → `ImmutableQueue<T>.Empty` + `= queue.Enqueue(item)` / `= queue.Dequeue(out var item)`
- `.ToList()` → `.ToImmutableList()`, `.ToHashSet()` → `.ToImmutableHashSet()`

The codebase is distributed (Orleans, reactive streams). Mutable collections cause race conditions and unpredictable behavior. The only exception is `ConcurrentDictionary` for thread-safe concurrent mutation patterns.

## Architecture Overview

### Core Concepts

**Message Hub Architecture**: MeshWeaver is built on an actor-model message hub system (`MeshWeaver.Messaging.Hub`). All application interactions flow through hierarchical message routing with address-based partitioning (e.g., `@app/Address/AreaName`).

**Layout Areas**: The UI system uses reactive Layout Areas - framework-agnostic UI abstractions that render in Blazor Server. Layout areas are addressed by route and automatically update via reactive streams.

**AI-First Design**: First-class AI integration using Microsoft.Extensions.AI with plugins (MeshPlugin, LayoutAreaPlugin) that provide agents access to application state and functionality.

### Key Directory Structure

- **`src/`** - Core framework libraries (50+ projects)
  - `MeshWeaver.Messaging.Hub` - Actor-based message routing
  - `MeshWeaver.Layout` - Framework-agnostic UI abstractions
  - `MeshWeaver.AI` - Agent framework with plugin architecture
  - `MeshWeaver.Blazor` - Blazor Server implementation
  - `MeshWeaver.Data` - CRUD operations with activity tracking
  - `MeshWeaver.Documentation` - Embedded documentation (served under Doc/)
  - `MeshWeaver.Graph` - Graph node configuration and node type system

- **`samples/`** - Sample business domain applications
  - `Graph/Data/` - Sample data nodes (ACME, Northwind, Cornerstone, etc.)
  - `Graph/content/` - Static content files (icons, images, attachments)

- **`memex/`** - Memex Portal (recommended for development)
  - `Memex.Portal.Monolith/` - Development portal with full Graph support
  - `aspire/` - Microservices with .NET Aspire orchestration

### Architectural Patterns

**Request-Response**: Use `hub.Observe<TResponse>(request, o => o.WithTarget(address)).Subscribe(resp => …, ex => …)` for operations requiring results.
The response is submitted as `hub.Post(responseMessage, o => o.ResponseFor(request))`. In tests, `await MonolithMeshTestBase.AwaitResponseAsync(request, ...)` is the sanctioned Task wrapper.

**Fire-and-Forget**: Use `hub.Post(message, o => o.WithTarget(address))` for notifications and events.

**Address-Based Routing**: Services register at specific addresses (e.g., `bookings/q1_2025`, `app/northwind`, `pricing/id`).
Layout areas follow the pattern `@{address}/{areaName}/{areaId}`. The areaId is optional and depends on the view.
E.g. `{address}/Details/{itemId}` would render a details view for the item with `itemId`.

Layout areas are typically kept on the same address as the underlying data.

**Reactive UI**: All UI state changes flow through the message hub. Controls are immutable records that specify their current state.

## Data Access Patterns

**IMPORTANT:** Application code must never use `IMeshStorage` or `IMeshCatalog` directly — these are internal infrastructure interfaces.

### Reads — Use IMeshService
```csharp
var query = hub.ServiceProvider.GetRequiredService<IMeshService>();
var node = await query.QueryAsync("path:org/Acme", maxResults: 1).FirstOrDefaultAsync(ct);
```

### Creates/Deletes — Use IMeshNodeFactory
```csharp
var factory = hub.ServiceProvider.GetRequiredService<IMeshNodeFactory>();
await factory.CreateNodeAsync(node, createdBy: userId, ct);
await factory.DeleteNodeAsync(path, recursive: true, ct);
```

### Updates/Moves — Use message requests
```csharp
hub.Post(new UpdateNodeRequest(updatedNode));
hub.Observe(new MoveNodeRequest(sourcePath, targetPath))
    .Subscribe(resp => /* handle resp.Message */, ex => /* DeliveryFailureException etc */);
hub.Post(new DataChangeRequest { Updates = [entity] });
```

### Service Resolution
Always use `GetRequiredService<T>()` for core services (`IMeshNodeFactory`, `IMeshService`). Never use `GetService<T>()` + null check for services that must be registered.

For full documentation see `src/MeshWeaver.Documentation/Data/Architecture/DataAccessPatterns.md`.

## MCP Mutations — Always Show a Diff

Claude Code renders diffs only for local file Edit/Write, not for MCP tool results.
Every time you mutate a mesh node through MCP (`patch`, `update`, `create`, `delete`,
`move`, `copy`), **surface what changed** so the user has the same visibility as for
file edits:

1. `get @path` **before** the mutation — cache the JSON.
2. Mutate.
3. `get @path` **after** — cache the new JSON.
4. Render a ```diff code-fence in your response with the relevant change. Claude Code
   applies syntax highlighting to ```diff blocks, so the user sees exactly what you
   changed on the mesh.

```diff
--- Systemorph/FutuRe/Pricing/Source/Foo.cs (before)
+++ Systemorph/FutuRe/Pricing/Source/Foo.cs (after)
@@ @@
-public string Old { get; init; }
+public string New { get; init; }
```

- Trim to the changed region — a full-file dump drowns out the delta.
- For `create`, show the whole new content as additions. For `delete`, show the content
  as removals. For `move`/`copy`, show the old path → new path.
- Read-only / side-effect MCP tools don't need this: `get`, `search`, `recycle`,
  `get_diagnostics`, `navigate_to`, `get_base_url`, `execute_script`.
- If the mutation was a no-op (server rejected the change or content was already
  equal), say so explicitly rather than rendering an empty diff.

The `MeshOperations` MCP tools are being extended to return a unified diff in the
tool response directly, so this convention holds even when other agents consume
the MCP. Until that's universally deployed, compute the diff locally from
before/after `get` calls.

## Development Patterns

### Adding New Layout Areas
```csharp
public static class MyLayoutArea
{
    public static void AddMyLayoutArea(this LayoutConfiguration config) =>
        config.AddLayoutArea(nameof(MyLayout), MyLayout);

    public static UiControl MyLayout(LayoutAreaHost host, RenderingContext ctx) =>
    Controls.Stack
            .WithView(Controls.Html("Some text")
            .WithView(Controls.Markdown("Some markdown view"))
    );

}
```
We support rich markdown with mermaid diagrams, code blocks, MathJax,
and live execution via dynamic markdown. Layout areas can be inserted by
using `@{address}/{areaName}/{areaId}`

### Message Handling
Messages are registered in the configuration of the hub. Also DI is set up on the level of hub configuration:
```csharp
public static class NorthwindHubConfiguration
{
    public static MessageHubConfiguration AddNorthwindHub(this MessageHubConfiguration config)
    {
        return config.WithHandler<MyRequestAsync>(HandleMyRequestAsync)
                     .WithHandler<MyRequest>(HandleMyRequest);

    }

    public static async Task<IMessageDelivery> HandleMyRequestAsync(MessageHub hub, IMessageDelivery<MyRequestAsync> request, CancellationToken ct)
    {
        // Process the request
        var result = await SomeService.ProcessAsync(request.Message);

        // Send response
        await hub.Post(new MyResponse(result), o => o.ResponseFor(request));
        return request.Processed();
    }

    public static IMessageDelivery HandleMyRequest(MessageHub hub, IMessageDelivery<MyRequest> request)
    {
        // Process the request
        var result = SomeService.Process(request.Input);

        // Send response
        hub.Post(new MyResponse(result), o => o.ResponseFor(request));
        return request.Processed();
    }
}
```

### AI Plugin Development
```csharp
public class MyPlugin(IMessageHub hub, IAgentChat chat)
{
    [Description("Description on how to use")]
    public Task<string> DoSomething([Description("Description for input")]string input)
    {
        var request = new MyRequest(input); // Create a request object
        var address = GetAddress(request); // Get the address for the plugin, e.g., "app/northwind"
        // MCP tool surface requires Task<string>; bridge once at the boundary via .ToTask().
        // The hub round-trip stays observable end-to-end (no `await` inside hub-reachable code).
        return hub.Observe<MyResponse>(request, o => o.WithTarget(address))
            .Select(resp => JsonSerializer.Serialize(resp.Message, hub.JsonSerializationOptions))
            .FirstAsync()
            .ToTask();
    }

    public Address GetAddress(MyRequest request)
    {
        // Logic to determine the address based on the request
        // the chat contains a context, which is usually good to use.
        // can also contain agent specific mapping logic.
        return chat.Context.Address;
    }
}
```

## Key Dependencies

- **.NET 10.0** - Target framework
- **Orleans** - Distributed deployment (distributed deployment, microservices)
- **Blazor Server** - Web UI framework
- **Microsoft.Extensions.AI** - AI integration
- **xUnit v3** - Testing framework
- **FluentAssertions** - Test assertions
- **Chart.js** - Data visualization
- **Azure SDKs** - Cloud integration
- **Markdig** - Markdown processing


## Testing Guidelines

**When building NodeTypes, data models, layout areas, or CSV loaders — read `src/MeshWeaver.AI/Data/Agent/Coder.md` first.** It is the canonical guide for that kind of work and includes the non-negotiable testing standard: **comprehensive unit tests per invariant + branch + boundary + degenerate-input**, not "at least one test per feature". Applies to both Coder-agent sessions and hand-written code. A NodeType with a single happy-path test is not tested; it's demoed.

Tests use xUnit v3 with structured logging and test parallelization configured via `xunit.runner.json`:
- `parallelizeAssembly: false`
- `parallelizeTestCollections: false`
- `maxParallelThreads: 1`
- `methodTimeout: 60000ms` (1 minute per test method)

**No mocking.** Tests that need infrastructure (persistence, messaging, DI) must use `MonolithMeshTestBase` or `OrleansTestBase` — never mock `IMessageHub`, `IMeshService`, or other core interfaces.

### Satellite Entity Patterns

For implementing and testing satellite entities (comments, threads, tracked changes), see `src/MeshWeaver.Documentation/Data/Architecture/SatelliteEntityPatterns.md`.

**Key rules:**
- Handler must be synchronous (`IMessageDelivery`, not `async Task<IMessageDelivery>`)
- Use `meshService.CreateNode()` (Observable) + `.Subscribe(onNext, onError)` — never `await`
- Use `workspace.UpdateMeshNode()` for parent node content updates (in-memory, persisted via debounce)
- Post response inside the `Subscribe(onNext)` callback, not before
- Orleans tests: client configurator must call `AddGraph()` for type registry alignment
- Verify via `GetDataRequest` or `GetRemoteStream` — never `QueryAsync` in distributed tests

### Running Tests

Run tests from the root directory using sub-paths. Do NOT write output to `/tmp` or temp directories — test results (.trx) are automatically collected in the project's `bin/` directory.

**CRITICAL: Always use `run_in_background: true`** for test runs. Tests can take minutes — never block the conversation waiting for them. Use `timeout: 180000` (3 min) max for Bash test commands. The xunit.runner.json `methodTimeout` is 60000ms (1 min) per test method.

**Do NOT use `--verbosity minimal`** (or `-v m`) when tests are expected to fail. Minimal verbosity hides error details (stack traces, assertion messages), forcing you to re-run with normal verbosity — wasting time and frustrating the user. Use default verbosity or `--verbosity normal` so failures are visible on the first run. Only use `--verbosity minimal` when you are confident all tests will pass and just need a quick green/red check.

```bash
# Run from root directory with sub-path
dotnet test test/MeshWeaver.Hosting.Monolith.Test --no-restore

# Run a specific test project
dotnet test test/MeshWeaver.Graph.Test --no-restore

# Filter to specific tests
dotnet test test/MeshWeaver.Graph.Test --filter "ClassName~AccessAssignment" --no-restore
```

**Workflow:**
1. Run tests **once** in background (`run_in_background: true`)
2. If failures: read the output to understand errors — do NOT re-run
3. Fix the code
4. Run tests **once** again to verify fixes
5. Repeat 2–4 until green

### DevLogin and Access Control in Tests

`MonolithMeshTestBase` automatically logs in `rbuergi@systemorph.com` as Admin via `TestUsers.DevLogin(Mesh)` in `InitializeAsync()`. This means all tests start with a logged-in admin user — no manual setup needed for basic CRUD.

**TestUsers** (`MeshWeaver.Hosting.Monolith.TestBase.TestUsers`):
- `TestUsers.Admin` — default admin AccessContext
- `TestUsers.SampleUsers()` — MeshNode array of sample users from `samples/Graph/Data/User/`
- `TestUsers.DevLogin(mesh)` — logs in the admin user (called automatically by base class)
- `builder.AddSampleUsers()` — extension to pre-seed user MeshNodes in `ConfigureMesh`

When tests with `AddRowLevelSecurity()` need **per-user** access control (e.g., testing that User1 can't see User2's data), use explicit admin setup for data creation:

```csharp
// Before creating test data: set up admin context
var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
await securityService.AddUserRoleAsync("setup-admin", "Admin", null, "system");
accessService.SetCircuitContext(new AccessContext { ObjectId = "setup-admin", Name = "Setup Admin" });

// ... create test nodes ...

// After setup: clear admin context so tests start clean
accessService.SetCircuitContext(null);
```

### Node Types

Only use **registered** node types in tests. Standard types registered by `AddGraph()`:
`Markdown`, `Code`, `Agent`, `Group`, `User`, `VUser`, `Role`, `Notification`, `Approval`, `AccessAssignment`, `GroupMembership`, `PartitionAccessPolicy`, `ActivityLog`, `UserActivity`, `Comment`, `Thread`, `ThreadMessage`

Custom types can be registered via `builder.AddMeshNodes(new MeshNode("MyType") { Name = "My Type" })` in `ConfigureMesh`.

### MonolithMeshTestBase (recommended for most tests)

Reference `MeshWeaver.Hosting.Monolith.TestBase` and inherit from `MonolithMeshTestBase`:

```csharp
public class MyTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Override ConfigureMesh to add services and sample users
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph()
            .AddSampleUsers()
            .ConfigureHub(hub => hub.AddMyHub());

    [Fact]
    public async Task MyTestMethod()
    {
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var nodeFactory = Mesh.ServiceProvider.GetRequiredService<IMeshNodeFactory>();

        // Create test data
        await nodeFactory.CreateNodeAsync(new MeshNode("test", "Namespace") { Name = "Test" }, "testuser");

        // Query
        var result = await meshQuery.QueryAsync<MeshNode>("path:Namespace/test").FirstOrDefaultAsync();
        result.Should().NotBeNull();
    }
}
```

### HubTestBase (for message routing / layout tests)

```csharp
public class MyTest : HubTestBase, IAsyncLifetime
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration config)
        => base.ConfigureHost(config).AddNorthwindHub();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration config)
        => base.ConfigureClient(config).AddLayoutClient();

    [Fact]
    public async Task MyTestMethod()
    {
        var hub = GetClient();
        // Test code: bridge to Task once via .FirstAsync().ToTask(ct).
        // Production code MUST stay reactive — `hub.Observe(...).Subscribe(onNext, onError)`.
        var response = await hub.Observe<MyResponse>(request, o => o.WithTarget(new HostAddress()))
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);
        response.Should().NotBeNull();
    }
}
```

For tests deriving from `MonolithMeshTestBase`, the helper `await AwaitResponseAsync(request, options?, hub?, ct?)` already wraps `hub.Observe(...)` with the test context's cancellation token.

## Project Structure Guidelines

- Framework code belongs in `src/`
- Test code belongs in `test/`
- Sample applications go in `samples/`
- Each module should have its own set of hubs and address spaces (e.g., `@app/northwind`)
- UI components should be framework-agnostic in the layout layer. The language are the controls inheriting from `UiControl`.
- AI agents should use plugins to access application functionality

## Solution Management

The solution uses centralized package management via `Directory.Packages.props`. When adding new dependencies, update the central package file rather than individual project files.

### Key Configuration Files
- `Directory.Build.props` - Global MSBuild properties and versioning
- `Directory.Packages.props` - Centralized NuGet package version management
- `nuget.config` - NuGet package sources configuration
- `xunit.runner.json` - Test execution configuration

### Branch and Development
- Main branch: `main` (use for PRs)
- Solution file: `MeshWeaver.slnx` contains 50+ projects
