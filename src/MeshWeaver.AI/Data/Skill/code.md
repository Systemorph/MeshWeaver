---
nodeType: Skill
name: /code
description: Create and modify NodeTypes, source, data models, layout areas, and scripts — type /code followed by the task
icon: Sparkle
category: Skills
order: 4
autoMount: true
---

You are now operating with **coding capability** for this MeshWeaver mesh. Create and modify
custom NodeTypes including their source code (`Source/`), data models, layout areas, reference
data, CSV loaders, JSON definitions, and executable Scripts.

# 🚨 Read these architecture docs FIRST (non-negotiable)

Before you write any handler, layout area, click action, service method, or Blazor view, internalise
these. Almost every recent deadlock and stale-content incident traces back to violating one of them.

1. **[Asynchronous Calls](@/Doc/Architecture/AsynchronousCalls)** — no `Task<T>` / `async` / `await`
   in mesh-reachable code. Public methods on services, handlers, layout areas and click actions return
   `IObservable<T>` (or `void`); compose with `SelectMany` / `Select` / `Where`. Request/response is
   `hub.Observe(request).Subscribe(onNext, onError)` — never the `[Obsolete]` `RegisterCallback` /
   `AwaitResponse`, and never `Observable.FromAsync`. Click actions stay sync
   (`ctx => { …; return Task.CompletedTask; }`).
2. **[CQRS — Queries vs. Content Access](@/Doc/Architecture/CqrsAndContentAccess)** — never read a known
   node with `QueryAsync`/`Query` (lagged, stale right after a write). Live read =
   `workspace.GetMeshNodeStream(path)`; one-shot = `hub.GetMeshNode(path, timeout?)`. `Query` is for
   sets and existence, not single-node content.
3. **[Data Binding](@/Doc/GUI/DataBinding)** — the GUI is fully data-bound with ONE source of truth: the
   node stream. Bind directly to `Hub.GetMeshNodeStream(path)` and write edits back via
   `GetMeshNodeStream(path).Update(current => …)`. Never replicate a node into a layout-area `/data/{id}`
   copy + a server-side save subscription, and never hand-roll HTML for structured data — use the
   framework controls (`Controls.DataGrid`, `MeshNodeContentEditorControl`, `MarkdownEditorControl`).
4. **[Activity Control Plane](@/Doc/Architecture/ActivityControlPlane)** — every operation on a stateful
   node is a property patch on the node's content, not a new message type. Pair `Status` (written only
   by the owning hub) with `RequestedStatus` (patched by callers via `GetMeshNodeStream(path).Update(…)`).
   Do not invent `CancelXRequest` / `RetryXRequest` message types.

# Mutations go through `GetMeshNodeStream(path).Update(...)`

Every mesh-node mutation goes through `workspace.GetMeshNodeStream(path).Update(current => modified)`
and **must be subscribed** (the observable is cold — the write runs on `Subscribe`). Create / delete /
move route through `meshService.CreateNode` / `DeleteNode` / `MoveNodeRequest`. Collections are
immutable (`ImmutableList`/`ImmutableDictionary`); never a `static` mutable collection.

# Tests are reactive role models — no `await` in the test body

Assert on the stream directly (`x.Should().Match(predicate)` / `.Emit()`) and drive
creates/updates from the assertion's subscribe. Use the test base, never mock `IMessageHub` /
`IMeshService`. Read the full guide in **[Coder.md](@/Agent/Coder)** before building NodeTypes,
data models, layout areas, or CSV loaders.
