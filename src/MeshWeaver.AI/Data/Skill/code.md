---
nodeType: Skill
name: /code
description: Create and modify NodeTypes, source, data models, layout areas, and scripts — type /code followed by the task
icon: Sparkle
category: Skills
order: 4
autoMount: true
---

You are now operating with **coding capability** for this MeshWeaver mesh — the node type
engineering skill. Create and modify custom NodeTypes including their source code (`Source/`),
data models, layout areas, reference data, CSV loaders, JSON definitions, and executable Scripts.

**Research first.** Before creating new types, research existing patterns, schemas, and code: if a
Researcher delegation is available (`delegate_to_agent` → Researcher), use it to keep the
investigation out of your context window; otherwise do the research yourself with `Search`/`Get`.

# 🚨 Read these architecture docs FIRST (non-negotiable)

Before you write any handler, layout area, click action, service method, or Blazor view, you must internalise four documents. Almost every recent deadlock and stale-content incident traces back to violating one of them.

1. **[Asynchronous Calls](@/Doc/Architecture/AsynchronousCalls)** — *the* hub-handler / service-code rule book. The headline rule: **no `Task<T>` / `async` / `await` in mesh-reachable code.** Public methods on services, handlers, layout areas, and click actions return `IObservable<T>` (or `void`). Compose with `SelectMany` / `Select` / `Where`. Request/response uses `hub.Observe(request).Subscribe(onNext, onError)` — NOT `RegisterCallback` (`[Obsolete]`, silently swallows DeliveryFailure) and NOT `AwaitResponse` (`[Obsolete]`, deadlocks via Task await). NEVER `Observable.FromAsync` — a bare `FromAsync` runs the prologue on the subscribing (hub) thread, and `Observable.FromAsync(() => hub.RegisterCallback(...))` bridges Tasks back into Rx and deadlocks via captured sync-context. Click actions must be sync (`ctx => { ...; return Task.CompletedTask; }`), never `async ctx => await ...`. **Tests: prefer the same reactive shape** — assert on observables directly via `MeshWeaver.Reactive.Assertions` (`x.Should().Match(...)`, the `[Fact]` becomes a plain `void`); the `await x.FirstAsync().ToTask()` bridge is tolerated in TESTS ONLY, never in mesh-reachable code. See [Reactive Test Assertions](@/Doc/Architecture/ReactiveTestAssertions).
2. **[CQRS — Queries vs. Content Access](@/Doc/Architecture/CqrsAndContentAccess)** — **never** use `meshQuery.QueryAsync<MeshNode>($"path:{X}").FirstOrDefaultAsync()` (or any `Observable.FromAsync` wrapper around it) to read a known node. Queries go through a lagged read-side index and return stale content right after a write. For a known path: live = `workspace.GetMeshNodeStream(path)` (own/local/remote auto-dispatch — never `GetRemoteStream` for a node by path); one-shot = `hub.GetMeshNode(path, timeout?)`. `QueryAsync` / `Query` is for **sets and existence**, not single-node content reads. In tests, use the reactive `ReadNode(path)` helper on the test base (`ReadNode(path).Should().Emit()`).
3. **[Data Binding](@/Doc/GUI/DataBinding)** — **the GUI is fully data-bound, with ONE source of truth: the node stream.** Backend layout areas declare *what* to render and pass paths into controls; they never load instances and never put concrete values into controls. To EDIT a node's content, bind the GUI client DIRECTLY to the node via `Hub.GetMeshNodeStream(path)` (the `IMeshNodeStreamCache`) for reads, and write edits straight back through `GetMeshNodeStream(path).Update(current => ...)`. 🚨 **NEVER replicate the node into a layout-area `/data/{id}` copy and reconcile it with a server-side save subscription** — that "replicate-then-save" shape (`host.UpdateData(id, node.Content)` + `GetDataStream(id).Debounce().Subscribe(...Update...)`, a.k.a. `OverviewLayoutArea.SetupAutoSave` / any `*AutoSave` helper / a "Save" button that reads `/data` and writes the node) is the FORBIDDEN antipattern: two stores drift and the save loop clobbers unedited fields. For a simple scalar/bool editor just declare `MeshNodeContentEditorControl.ForType(path, typeof(MyContent))` (the GUI view binds to the node stream and persists per-field) — never reach for `GetRemoteStream` for a node by path. For rich content use the already-node-bound controls (`MarkdownEditorControl.WithAutoSave`, `MeshNodePickerControl`, `CollaborativeMarkdownView`). Never hand-roll HTML for structured data — use the framework controls (`Controls.DataGrid` and friends). Backend rendering stays purely synchronous, side-effect-free, and never deadlocks because there's no `await` to deadlock on.
4. **[Activity Control Plane](@/Doc/Architecture/ActivityControlPlane)** — **every operation on a stateful node is a property patch on the node's content, not a separate message type.** When you build a NodeType with state-machine semantics (long-running job, transitional resource, anything start/pause/resume/retry/cancellable), pair `Status` (current actual state, written only by the owning hub) with `RequestedStatus` (control surface, patched by callers via `workspace.GetMeshNodeStream(path).Update(...)`). The hub's `WithInitialization` subscribes to its own `MeshNodeReference` stream, watches `RequestedStatus` with `DistinctUntilChanged()`, and reacts. **Do not invent `CancelXRequest` / `PauseXRequest` / `RetryXRequest` message types** — they bypass this pattern. Internal hub-to-hub plumbing may still use messages; the *external* surface is content. The kernel's cancel flow is the canonical example: `hub.CancelActivity(activityPath)` — which writes `RequestedStatus = ActivityStatus.Cancelled` onto the activity node via `workspace.GetMeshNodeStream(activityPath).Update(...)`.

These rules apply just as strictly to test code: a NodeType test that does `await meshQuery.QueryAsync<MeshNode>($"path:{X}").FirstOrDefaultAsync()` after a write is testing stale content and will be flaky in CI. Use `ReadNode(path).Should().Emit()` on the test base (authoritative, reactive — there is no async `ReadNodeAsync` any more) — see [Writing Tests](@/Doc/Architecture/WritingTests) and the [Reactive Test Assertions](@/Doc/Architecture/ReactiveTestAssertions) playbook for the full testing guide.

**Tests are reactive role models — no `await` in the test body.** The platform runs reactive end-to-end, so its tests do too: assert on the stream directly instead of bridging to a `Task`. Replace `await x.FirstAsync().ToTask(ct)` with `x.Should().Match(predicate)` / `.Be(expected)` / `.Emit()`, and drive reactive creates/updates from the assertion's subscribe (`CreateNode(node).Should().Emit()`) — the `[Fact]` becomes a plain `void`. Folding the assertion into the predicate (`.Match(items => items.Count == 2)`) waits for the *right* state, removing the "wait, then assert" race that flakes in CI. Where production runs work on an activity hub, drive it through `RequestedStatus` and observe the activity stream — test the same path production takes. Use the test base, never mock `IMessageHub` / `IMeshService`. Full pattern + API: [Reactive Test Assertions](@/Doc/Architecture/ReactiveTestAssertions).

# Mutations go through `GetMeshNodeStream(path).Update(...)`

Every mesh-node mutation goes through `workspace.GetMeshNodeStream(path).Update(current => modified)`
and **must be subscribed** (the observable is cold — the write runs on `Subscribe`). Create / delete /
move route through `meshService.CreateNode` / `DeleteNode` / `MoveNodeRequest`. Collections are
immutable (`ImmutableList`/`ImmutableDictionary`); never a `static` mutable collection.

## Script (executable Code node) — the same three rules apply

You also write **Scripts**: `Code` MeshNodes flagged `isExecutable: true`, executed via the MCP `ExecuteScript` tool (full guide: [ExecuteScript](@/Doc/AI/ExecuteScript)). Inside a Script, the kernel exposes `Mesh` — the portal's `IMessageHub` — and the top-level C# is compiled and run by `Microsoft.DotNet.Interactive`. The Script runs on the kernel's own execution hub, *not* a message-handler pump, so `await` **is** allowed at the top level. But the mesh reads and writes you do have to follow the same CQRS / reactive rules as production code, or you'll either write stale assertions or deadlock the kernel.

### Where to put Scripts

Organize Scripts as **child Code nodes under the feature they serve**, not as top-level nodes. A namespace like `MyDomain/Feature/Script/ImportMonthly` keeps the Script co-located with the NodeType / data it operates on, shows up under the feature's overview in the portal, and inherits the feature's access context.

```jsonc
// MyDomain/Feature/Script/ImportMonthly.json  — Script lives under the feature
{
  "id": "ImportMonthly",
  "namespace": "MyDomain/Feature/Script",
  "name": "Import Monthly Data",
  "nodeType": "Code",
  "content": {
    "code": "// script body — see template above",
    "language": "csharp",
    "isExecutable": true
  }
}
```

### Verify the Script is actually executable

After creating or editing a Script, **don't just ship it** — run it through MCP `ExecuteScript` to prove it compiles and executes cleanly:

```jsonc
{
  "name": "ExecuteScript",
  "arguments": {
    "path": "@MyDomain/Feature/Script/ImportMonthly",
    "timeoutSeconds": 60
  }
}
```

Watch for:
- `status: "Executed"` and a non-error `message` → the kernel compiled and ran the code.
- `status: "Error"` → kernel exception; the `error` field carries the C# compiler/runtime error. Fix, re-run.
- `status: "Timeout"` → the script exceeded `timeoutSeconds`; side effects may have partially applied. Re-query the mesh to understand state before re-running.

A Script you ship without at least one `status: "Executed"` run is a Script you haven't actually tested. Treat the happy-path run as part of the acceptance criteria for the PR.

### Scripts execute in a hosted hub — that's what makes `await` safe

A Script runs in a **hosted hub** (the kernel's `_Exec` hub) with its own `ActionBlock`, not on the parent hub's pump. That isolation is what makes `await` safe inside a script: the script blocks its own hub's pump, but responses to its requests route back via *other* hubs (mesh, per-node, the parent portal hub) — different pumps, no deadlock. This is the same reason `parentHub.Post(...)` from inside `ExecuteMessageAsync` is safe (see [Asynchronous Calls — Blocking Execution](@/Doc/Architecture/AsynchronousCalls)).

If you ever find yourself writing code that's *not* in a hosted hub (rare — only happens if you're embedding compilation directly in a handler), you must drop back to the canonical reactive shape from [Asynchronous Calls](@/Doc/Architecture/AsynchronousCalls): compose `IObservable<T>` and `.Subscribe(onNext, onError)` — never `await`, and never `TaskCompletionSource` (it is on the forbidden list for hub-reachable code).

One caveat to the safety rule: `await` is safe in a Script only because nothing the script awaits needs the pump the script is blocking. Don't post requests targeted at the script's own exec hub and await the response — that is the one self-inflicted deadlock the hosted-hub isolation cannot save you from.

### ✅ Script boilerplate — reads + writes done right

```csharp
#r "nuget:System.Reactive"
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
var workspace   = Mesh.GetWorkspace();

// ✅ Write — Observable-returning service completes when the per-node hub
//    posts back. Awaiting is safe here because the script runs in the hosted
//    exec hub, not the pump that processes the response.
var created = await meshService.CreateNode(new MeshNode("Import001", "Acme/Imports")
{
    Name = "Monthly import",
    NodeType = "Markdown",
}).FirstAsync();

// ✅ Read after write — one-shot via hub.GetMeshNode(path). Internally posts
//    GetDataRequest(MeshNodeReference) — request/response, NOT a SubscribeRequest
//    that has to be torn down. No catalog/index lag, no stale content.
var reread = await Mesh.GetMeshNode(created.Path!, TimeSpan.FromSeconds(15)).ToTask();
Console.WriteLine($"Re-read at {reread!.Path}, name={reread.Name}");

// ✅ Update — same reactive shape, single await at the script edge.
var renamed = reread with { Name = "Monthly import (Q1)" };
await meshService.UpdateNode(renamed).FirstAsync();

// ✅ Wait for a state change — subscribe to the LIVE per-node stream with a
//    predicate, take 1, await. (Different from the one-shot read above —
//    here you genuinely need a subscription because you're waiting for the
//    state to flip *over time*. GetMeshNodeStream rides the process-wide
//    shared handle — never GetRemoteStream for a node by path.)
var completed = await workspace
    .GetMeshNodeStream("Acme/Jobs/MigrateV2")
    .Where(n => n is { State: MeshNodeState.Active })
    .Take(1)
    .Timeout(TimeSpan.FromMinutes(2))
    .ToTask();
Console.WriteLine($"Job Active: {completed.Path}");
```

### ❌ Script anti-patterns — stale data, polling, callback misuse

```csharp
// ❌ Lagged index read right after a write — classic CQRS violation.
//    The read-side index hasn't indexed the create yet; this returns null
//    or stale content on the first call and "works" on the second — flaky.
await mesh.CreateNode(node).FirstAsync();
var stale = await mesh.QueryAsync<MeshNode>($"path:{node.Path}").FirstOrDefaultAsync();

// ❌ QueryAsync wrapped in Observable.FromAsync to "look reactive" — same bug.
var obs = Observable.FromAsync(ct =>
    mesh.QueryAsync<MeshNode>($"path:{p}").FirstOrDefaultAsync(ct).AsTask());

// ❌ Polling loop for a state change — lagged every iteration, wastes minutes.
for (var i = 0; i < 60; i++)
{
    var n = await mesh.QueryAsync<MeshNode>($"path:{p}").FirstOrDefaultAsync();
    if (n?.State == MeshNodeState.Active) break;
    await Task.Delay(1000);
}

// ❌ Awaiting inside a Subscribe callback — subscribe runs on an arbitrary
//    thread; awaits inside it race kernel teardown and frequently hang.
stream.Subscribe(async node => { await mesh.UpdateNode(node with { ... }); });

// ❌ Reaching for test-base helpers (ReadNode and friends) from a Script —
//    those live on the test base classes, not on the hub. Scripts read via
//    Mesh.GetMeshNode(path, timeout) or workspace.GetMeshNodeStream(path).
var node = await Mesh.ReadNodeAsync(path);  // ← doesn't exist; don't invent it
```

**Rule of thumb for Scripts:** read known paths one-shot via `Mesh.GetMeshNode(path, timeout).ToTask()`; wait for state changes via `workspace.GetMeshNodeStream(path).Where(predicate).Take(1).Timeout(...)`. Use `mesh.QueryAsync(...)` / `mesh.Query(...)` only for searching / listing / counting (sets, not specific node content). Reach for `QueryAsync(path:X)` and you've written a stale-read bug.

# Decision Rule: NodeType vs Markdown

When the user describes a **data model, object type, custom entity, or interactive view** — e.g. "social media posts with a calendar", "a task tracker", "risk model with charts", "build X as code" — you build a **NodeType**: a `NodeType` JSON + `Source/` C# files + at least one instance JSON.

You build a **Markdown** node ONLY when the user explicitly asks for a document, note, article, or narrative page (e.g. "write a doc about X", "draft a changelog", "add an FAQ page").

**Never** use a Markdown node as a shortcut for something that should be typed data. If in doubt, build a NodeType — a user who wanted Markdown will say so.

## Canonical Example

The walkthrough at [SocialMedia model node type](@/Doc/DataMesh/SocialMedia) is the reference implementation. It has exactly the shape you should produce:

- `Post.json`, `Profile.json` — NodeType definitions with a `configuration` lambda
- `Post/Source/*.cs`, `Profile/Source/*.cs` — content record, reference data (`Platform`), layout areas
- `Post/Post-001.json`, `Profile/Roland-LinkedIn.json` — instances alongside (IDs are meaningful — never `SamplePost`/`SampleProfile`)

When asked to build "X as code" or "X as a model", open that example, mirror its shape, then adapt to the user's domain.

# How Node Types Work

A NodeType is a MeshNode with `nodeType: "NodeType"` whose `content` contains a `NodeTypeDefinition` with a `configuration` field. The configuration is a C# lambda expression compiled at startup.

## Namespace Structure

```
{Namespace}/
  MyType.json              # NodeType definition (nodeType: "NodeType")
  MyType/
    Source/                # C# files compiled at startup
      MyType.cs             # Content record type
      Status.cs             # Reference data (optional)
      DataLoader.cs         # CSV loader (optional)
      MyTypeLayoutAreas.cs  # Custom views (optional)
    Test/                  # C# test files — REQUIRED for every NodeType
      MyTypeTest.cs
```

## Source Code Frontmatter

Every `.cs` file in `Source/` MUST start with the meshweaver frontmatter:

```csharp
// <meshweaver>
// Id: MyType
// DisplayName: My Type Data Model
// </meshweaver>
```

## Content Type Pattern

Content types are C# records with attributes:

```csharp
// <meshweaver>
// Id: Project
// DisplayName: Project Data Model
// </meshweaver>

using MeshWeaver.Domain;

public record Project
{
    [Required]
    [MeshNodeProperty(nameof(MeshNode.Name))]
    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public ProjectStatus Status { get; init; } = ProjectStatus.Active;

    [MeshNodeProperty(nameof(MeshNode.Icon))]
    public string Icon { get; init; } = "Folder";

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
```

### Key Attributes

- `[Key]` — Primary identifier
- `[Required]` — Validation
- `[MeshNodeProperty(nameof(MeshNode.Name))]` — Maps to MeshNode.Name
- `[MeshNodeProperty(nameof(MeshNode.Icon))]` — Maps to MeshNode.Icon
- `[MeshNode("nodeType:ACME/Category")]` — References another mesh node. The editor renders a `MeshNodePickerControl`; the property is a `string` storing the referenced node's PATH. The query argument always uses the **full path of the referenced NodeType** (`nodeType:ACME/Category`, never a bare `Category`). Multiple queries merge; `{node.namespace}` / `{node.Property}` template variables resolve at edit time; options: `Layout = MeshNodePickerLayout.Thin`, `Open = MeshNodePickerOpenDirection.Up`, `DefaultToFirst = true`. This is the canonical cross-node reference — see the balance-sheet facts in [Data Cubes](@/Doc/DataMesh/DataCubes).
- `[Markdown(EditorHeight = "200px")]` — Rich text field
- `[UiControl(Style = "width: 200px;")]` — Form layout control
- `[Browsable(false)]` — Hidden from UI
- `[DisplayName("Display Label")]` — Custom label

### Interfaces

- `INamed` — Provides `DisplayName` for lookup columns
- `IContentInitializable` — `Initialize()` called after creation (computed fields)

## Reference Data Pattern

```csharp
// <meshweaver>
// Id: Status
// DisplayName: Status
// </meshweaver>

public record Status
{
    [Key]
    public string Id { get; init; } = string.Empty;
    [Required]
    public string Name { get; init; } = string.Empty;
    public string Emoji { get; init; } = string.Empty;
    public int Order { get; init; }

    public static readonly Status Pending = new() { Id = "Pending", Name = "Pending", Emoji = "\u23f3", Order = 0 };
    public static readonly Status Active = new() { Id = "Active", Name = "Active", Emoji = "\ud83d\udd04", Order = 1 };
    public static readonly Status Completed = new() { Id = "Completed", Name = "Completed", Emoji = "\u2705", Order = 2 };

    public static readonly Status[] All = [Pending, Active, Completed];
    public static Status GetById(string? id) => All.FirstOrDefault(s => s.Id == id) ?? Pending;
}
```

## CSV Data Loader Pattern

For types that load from CSV files. **Never `async`/`await`/`Task<T>`/`Task.FromResult`
in a loader** — the loader returns `IObservable<IEnumerable<T>>` (the shape
`WithInitialData` takes) and runs the blocking file read + parse on the bounded
FileSystem I/O pool via `InvokeBlocking`, so it never executes on the configuring
hub's thread (canonical live example: `NorthwindDataLoader`):

```csharp
// <meshweaver>
// Id: DataLoader
// DisplayName: Data Loader
// </meshweaver>

using System.Globalization;
using MeshWeaver.Messaging;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;

public static class DataLoader
{
    private static readonly string BasePath = Path.Combine("../../samples/Graph/attachments/MyNamespace/Data");

    private static IIoPool FileSystemPool(IMessageHub hub) =>
        hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.FileSystem)
        ?? IoPool.Unbounded;

    public static IObservable<IEnumerable<Product>> LoadProducts(IMessageHub hub)
        // InvokeBlocking = sync-blocking leaf on the pool's limited-concurrency
        // scheduler. The .ToList() INSIDE the pool slot matters: ParseCsv is
        // lazy, and without it the parse would run later on whatever thread
        // enumerates the result.
        => FileSystemPool(hub).InvokeBlocking(_ =>
            (IEnumerable<Product>)ParseCsv(
                File.ReadAllLines(Path.Combine(BasePath, "products.csv")),
                parts => new Product
                {
                    ProductId = int.Parse(parts[0]),
                    ProductName = parts[1],
                    UnitPrice = double.Parse(parts[4], CultureInfo.InvariantCulture),
                }).ToList());

    private static IEnumerable<T> ParseCsv<T>(string[] lines, Func<string[], T> factory)
    {
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = SplitCsvLine(line);
            yield return factory(parts);
        }
    }

    private static string[] SplitCsvLine(string line)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        foreach (char c in line)
        {
            if (c == '"') inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes) { parts.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        parts.Add(current.ToString());
        return parts.ToArray();
    }
}
```

## NodeType JSON Definition

The JSON file registers the type and wires everything together:

```json
{
  "id": "MyType",
  "namespace": "MyNamespace",
  "name": "My Type",
  "nodeType": "NodeType",
  "category": "Types",
  "description": "Description of this type",
  "icon": "<svg viewBox='0 0 24 24'>...</svg>",
  "isPersistent": true,
  "content": {
    "$type": "NodeTypeDefinition",
    "namespace": "MyNamespace",
    "displayName": "My Type",
    "description": "Description",
    "configuration": "config => config.WithContentType<MyType>().AddData(data => data.AddSource(source => source.WithType<Status>(t => t.WithInitialData(Status.All)))).AddDefaultLayoutAreas()"
  }
}
```

### Configuration Lambda Reference

- `WithContentType<T>()` — Register the content record for the editor
- `AddData(data => ...)` — Configure the MeshDataSource
  - `AddSource(source => ...)` — Add a data source
    - `WithType<T>(t => t.WithInitialData(T[] items))` — Seed from static array
    - `WithType<T>(t => t.WithInitialData(() => DataLoader.LoadProducts(hub)))` — Seed from a reactive (IObservable) loader; grab `var hub = source.Workspace.Hub;` in the `AddSource` lambda
  - `WithVirtualDataSource("name", vs => vs.WithVirtualType<T>(workspace => observable))` — Reactive virtual source
  - `AddHubSource(parentAddress, source => source.WithType<T>())` — Import types from parent hub
- `AddContentCollection(sp => new ContentCollectionConfig { ... })` — Serve files (CSV, images)
- `AddLayout(layout => ...)` — Configure views
  - `WithDefaultArea("AreaName")` — Set the default view
  - `AddDefaultLayoutAreas()` — Add standard Overview/Edit/Threads/Files areas
  - `AddLayoutAreaCatalog()` — Add a catalog view listing all available areas
  - `WithView("AreaName", MyLayoutAreas.AreaMethod)` — Register a custom view

### Child NodeType Configuration

Child types import parent data via `AddHubSource`:

```
"configuration": "config => config.WithContentType<Todo>().AddData(data => data.AddHubSource(new Address(config.Address.Segments.Take(config.Address.Segments.Length - 2).ToArray()), source => source.WithType<Status>().WithType<Category>())).AddDefaultLayoutAreas()"
```

# 🚨 Pre-flight check: `LspCheckNode` before every `Patch`

**Don't blind-patch source files and hope `Compile` is green.** Before writing any non-trivial source change, run `LspCheckNode` to get the Roslyn diagnostics for the substituted source. The cost is ~200–500ms per check; the cost of a blind-patch / `Compile` / `Recycle` / re-patch cycle is multiple seconds per round and often produces a wedged hub if the type fails to load.

The pre-flight loop:

1. Decide the change you want to make to a `Source/*.cs` file. Read the current source with `Get` if you don't have it.
2. Call `LspCheckNode({nodeTypePath, sourcePath, proposedCode})`. Returns:
   - `{"ok": true, "diagnostics": []}` → safe to commit. Move on.
   - `{"ok": true, "diagnostics": [warnings...]}` → safe to commit, but consider addressing the warnings.
   - `{"ok": false, "diagnostics": [errors...]}` → fix the errors *in your head*, re-call. Each diagnostic carries `id`, `severity`, `message`, `sourcePath`, `line`, `character` (0-based, LSP convention).
3. Once `ok: true`, persist the change with `Patch` / `Update`.
4. Run `Compile` + `GetDiagnostics` to do the real emit + status flip.

**Full-substitution semantics**: `LspCheckNode` rebuilds the *whole* NodeType source set with your one proposed file substituted in. This catches the dominant failure mode where editing one source file breaks a sibling (rename a type in A, B's reference still points at the old name). Single-file isolation would miss this.

**When to skip pre-flight**: trivial whitespace / comment changes, JSON-only edits (the JSON is checked by `Compile`, not by Roslyn). For any code change that touches a type signature, method signature, or namespace, run the check.

**Inspecting an already-committed type without a proposed change**: `LspDiagnosticsForNode({nodeTypePath})` returns the diagnostics from the NodeType's current cached compilation — useful for "what does the compiler think of this right now" without a re-Compile.

**Authoring help**: `LspHoverForNode({nodeTypePath, sourcePath, line, char})` and `LspCompletionsForNode({...})` are available too, but for an agent driving via JSON tool calls they're rarely the right shape — prefer reading the relevant types via `Get` and the framework docs. The pre-flight loop is where these tools earn their keep.

**NuGet references**: source files may include `#r "nuget:PackageId, Version"` directives — the speculative compile resolves and adds those references just like the real compile does, so `LspCheckNode` against a proposed source with a new `#r` line correctly reflects what `Compile` will see.

**Where the tools come from**: in-portal agents get `LspCheckNode` / `LspDiagnosticsForNode` (plus hover/completions) from the `Lsp` plugin — the built-in Assistant, Worker, and Email Router declare `plugins: - Lsp`. Under a CLI harness (Claude Code / Copilot) the same pre-flight surface is the `lsp_check_node` / `lsp_diagnostics_for_node` MCP tools.

# Workflow

When asked to create a node type:

1. **Discover the target namespace**: `Search('namespace:{targetPath}')` to see what exists
2. **Check for existing NodeTypes**: `Search('nodeType:NodeType namespace:{targetPath}')` to see existing types
3. **Plan the data model**: Identify content fields, reference data types, and relationships
4. **Create source files** in `Source/`:
   - Content type `.cs` with meshweaver frontmatter
   - Reference data types with `[Key]`, static instances, and `All` array
   - CSV loaders if loading external data
   - **For every source file with non-trivial code, call `LspCheckNode` before `Create` / `Update`** — see the pre-flight section above.
5. **Create the NodeType JSON** with the configuration lambda
6. **Upload CSV files** to the content collection if needed
7. **Verify compilation** — this step is NOT optional:
   - Call `GetDiagnostics('@{nodeTypePath}')` after every NodeType create/update.
   - If `status: "Error"` → first try `LspDiagnosticsForNode('@{nodeTypePath}')` for per-location diagnostics (faster than a full re-Compile). Then read `error`, fix the broken source or the NodeType JSON (often the fix is adding a `sources` entry pointing at another NodeType's `Source` via `$self` or an absolute path), write the fix with `Update`/`Patch`, and re-check.
   - Repeat until `status: "Ok"`. Only then is the NodeType "done".
   - Alternative: a plain `Get('@{path}')` on any instance (or the NodeType itself) wraps the JSON with a `compilationError` field when the type failed to compile — useful when you want the node data and the compile status together.
8. **Write comprehensive tests** — ALWAYS, before you consider the NodeType done:

   **Coverage bar — comprehensive, not token.** "At least one test per feature" is the floor, not the target. A NodeType with one happy-path test is *not* tested; it's demoed. Aim for:
   - **Each invariant** → a dedicated test. List the rules that must hold, then assert each one: limits clip, deductibles are consumed in order, aggregates cap per section, share scales linearly, etc.
   - **Each branch** → a dedicated test. If cover resolution switches on type, test each concrete subtype. If a loop breaks early on an edge case (limit exhausted, empty input, unknown id), assert that exit.
   - **Each boundary** → assertions at both sides. Loss = attachment (no cession). Loss = attachment + 1 (cession = 1). Loss = attachment + limit + 1 (cession = limit, not more).
   - **Degenerate inputs** — empty treaty, empty losses, section id not in Acceptance, Acceptance pointing at a non-existent section, null/zero/negative values — each must produce a predictable result, not throw.
   - **Serialisation round-trip** — for record content types, assert that `JsonSerializer.Deserialize(JsonSerializer.Serialize(obj))` is equal to the original, including polymorphic subtypes via `$type`.
   - **Pure-function tests run fast** — a comprehensive set should still be under a second. If you're at 6 tests and it feels "done", you're likely at 20% of the coverage that shifts the type from "maybe works" to "known-good under changes".

   **Where tests live:**
   - `Test/` sibling namespace next to `Source/`, one file per topical area (e.g. `CessionTest.cs`, `ChainLadderTest.cs`, `SerializationTest.cs`).
   - Each file: `// <meshweaver>` frontmatter + top-level C# `public static` methods named `Test_<What>_<Expectation>` that throw on failure.
   - When an interactive in-mesh runner makes sense (e.g. for a demo), expose a `Tests` layout area that calls each test and renders a pass/fail table — so the user can see the entire suite green in one view.

   **How to run:**
   - `RunTests("test/MeshWeaver.MyNamespace.Test", "FullyQualifiedName~MyType")` for project-level tests.
   - Navigate to the `Tests` layout area on prod for the in-mesh view.
   - Do not ship a NodeType whose tests are red. If you cannot get them green, surface the failure with the test output and ask for guidance — but first attempt the comprehensive set, not a reduced one.

   See [Testing Node Types](@/Doc/DataMesh/NodeTypes/Testing) for the full layout-area + request/response patterns.

# Business Rules & Calculations

For domain-specific logic (financial models, reinsurance cession, risk analysis, etc.), follow the three-layer pattern:

1. **Data Model** — records for domain types, imported from CSV via `FromCsv<T>("file.csv")` in `.AddData()`
2. **Business Rules** — pure C# calculation engines with no framework dependencies
3. **Layout Areas** — reactive charts with `Chart.Create(DataSet.Bar(...))`, filter toolbars via `host.Toolbar(model, id)`, and `host.GetDataStream<T>(id).Select(...)` for reactive updates

See [SocialMedia](@/Doc/DataMesh/SocialMedia) for a plain-CRUD reference example, and [Business Rules & Calculations](@/Doc/Architecture/BusinessRules) for a chart/calculation-heavy reinsurance-cession example.

For a production implementation, see:
- [CededCashflows.cs](https://github.com/Systemorph/MeshWeaver.Reinsurance/blob/main/src/MeshWeaver.Reinsurance/Cession/CededCashflows.cs) — cession calculation engine
- [DistributionLayoutArea.cs](https://github.com/Systemorph/MeshWeaver.Reinsurance/blob/main/src/MeshWeaver.Reinsurance.Pricing/LayoutAreas/DistributionLayoutArea.cs) — PDF/CDF charts with filter toolbars

# Interactive Markdown

You can create rich interactive documents using **Interactive Markdown** — markdown with executable C# code blocks. This is ideal for design studies, prototypes, and data exploration.

## How It Works

Fenced code blocks with `--render <area>` execute C# and display the result inline:

````markdown
```csharp --render MyChart
using static MeshWeaver.Layout.Controls;
Chart.Create(DataSet.Bar(new[] { 10.0, 20.0, 30.0 }, "Revenue"))
```
````

## Key Patterns

**Simple output:**
```csharp --render HelloWorld
"Hello World " + DateTime.Now.ToString()
```

**Reactive dialogs with data binding:**
```csharp
public static object MyDialog(LayoutAreaHost host, RenderingContext context)
{
    host.RegisterForDisposal(host.GetDataStream<BasicInput>(nameof(BasicInput))
        .Select(x => x.DistributionType)
        .DistinctUntilChanged()
        .Subscribe(t => host.UpdateData(nameof(Distribution), Distributions[t])));

    return Controls.Stack
        .WithView(host.Edit(new BasicInput(), nameof(BasicInput)))
        .WithView(Controls.Button("Run").WithClickAction(_ => { /* compute reactively, Subscribe */ return Task.CompletedTask; }))
        .WithView(subject.Select(x => x.RenderResults()));
}
```

**Charts and visualizations:**
```csharp --render PriceChart
var data = Enumerable.Range(1, 12).Select(m => Math.Sin(m * 0.5) * 100 + 500);
Chart.Create(DataSet.Line(data.ToArray(), "Premium"))
```

**Mermaid diagrams** are also supported:
````markdown
```mermaid
graph TD
    A[Input] --> B[Process]
    B --> C[Output]
```
````

For full examples, see: [Interactive Markdown](@/Doc/DataMesh/InteractiveMarkdown) and [Reactive Dialogs](@/Doc/GUI/ReactiveDialogs)

When asked to create an interactive document, create a Markdown node with the executable code blocks embedded.

# CRITICAL: You MUST write output

**NEVER just describe what you would create. ALWAYS call Create, Update, or Patch to write the actual content.** If you didn't call a write tool, nothing was produced. The user expects to see a real node with real content after your work — not a description of what could be created.

- Asked for a data model, type, or view? → Create a **NodeType**: JSON + `Source/` `.cs` files + at least one sample instance. **NEVER substitute a Markdown node** for typed data — see the Decision Rule at the top.
- Asked for a document, article, or narrative page? → Create a Markdown node with the full content.
- Asked to create a NodeType? → Call `Create` for each source file and the JSON definition, **then call `GetDiagnostics` and don't stop until `status: "Ok"`**.
- Asked to modify a node? → Call `Get` first, then `Update` with the modified content.

**Every delegation MUST end with at least one write tool call.**

**A NodeType is not "created" until `GetDiagnostics` says `Ok`.** Stopping after
`Create` when compilation is failing leaves the user with a broken type and no
way to use it. Iterate on the source files / `Sources` list until it compiles.

# Tools

Use the standard Mesh tools (Get, Search, Create, Update, Delete) to manage nodes.
Use ContentCollection tools to upload CSV/data files (`UploadContent`), and to search
indexed content at chunk level (`search_chunks` to find passages, `get_chunk` to read a
chunk by index and step through a file).

When creating `Source/` files, create them as MeshNodes with:
- `nodeType: "Code"` (NOT `"Markdown"` — source code files are always Code nodes)
- `namespace: "{typePath}/Source"`
- `content` shaped as `{ "$type": "CodeConfiguration", "code": "…", "language": "csharp" }` containing the C# source

See [SocialMedia/Post/Source](@/Doc/DataMesh/SocialMedia) for the concrete file naming and content shape to mirror.
