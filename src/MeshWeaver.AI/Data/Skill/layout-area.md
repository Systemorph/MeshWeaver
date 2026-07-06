---
nodeType: Skill
name: /layout-area
description: Build MeshWeaver UI the implementation-independent way — a layout area that returns a declarative tree of typed controls (the controls language), data-bound to mesh nodes. You describe controls, never HTML/Razor/XAML.
icon: Layout
category: Skills
order: 15
---

In MeshWeaver you do **not** write platform UI. You write a **layout area** — a reactive function that returns a tree of **controls**. Controls are a *declarative, implementation-independent language*: you compose them and bind them to data, and the framework renders them on whatever surface is hosting (Blazor Server today, Blazor Hybrid / native tomorrow). The same area definition renders everywhere. You never touch HTML, Razor, or XAML.

The cardinal sin is reaching *under* the controls language — emitting raw HTML, copying node data into a side store, or polling instead of binding.

Before writing anything, read [Layout Areas](/Doc/GUI/LayoutAreas) and [GUI Data Binding](/Doc/GUI/DataBinding).

# 1. The unit is a layout area

A layout area is a **named, reactive view** addressed `@{address}/{areaName}/{areaId}`. It returns controls and re-renders when its bound data changes. It is the only place UI is defined — there is no per-platform view to maintain. See [Layout Areas](/Doc/GUI/LayoutAreas).

# 2. Controls are the language — describe them, never render them

A view is a **tree of typed controls**, not a string of markup. The renderer maps each control to the host platform; your code is platform-agnostic.

- **Compose**: `Controls.Stack`, `Controls.LayoutGrid`, `Controls.Title`, `Controls.Markdown` → [Container Control](/Doc/GUI/ContainerControl) · [Layout Grid](/Doc/GUI/LayoutGrid).
- **Tabular / structured data → a control, NEVER hand-built markup**: `Controls.DataGrid(rows).WithColumn(new PropertyColumnControl<T> { Property = nameof(Row.X).ToCamelCase() }.WithTitle("…").WithFormat("N0"))` bound to a plain row record — sorting / formatting / theming / virtualization for free → [DataGrid](/Doc/GUI/DataGrid).
- **🚨 FORBIDDEN**: `StringBuilder` / `$"<table>…"` / any `RenderHtml`-shaped helper / `Controls.Html(handBuiltMarkup)` for structured data. `Controls.Html` is ONLY for genuinely pre-rendered markdown/rich text. Building HTML strings breaks the implementation-independence (and was a banned hack).
- The tree is **reactive** — never `.Take(1)` on a stream feeding a live area; it freezes the binding.

# 3. Data: BIND to the node stream, never copy

The mesh node is the single source of truth. Controls bind to it; they don't hold a copy.

```csharp
// READ — live, authoritative
workspace.GetMeshNodeStream(path)              // IObservable<MeshNode>
    .Select(node => node.ContentAs<MyContent>(hub.JsonSerializerOptions))

// WRITE — the ONLY mutation API; the owning hub serialises every writer
workspace.GetMeshNodeStream(path)
    .Update(node => node with { Content = ... })
    .Subscribe(_ => { }, ex => logger.LogWarning(ex, "update failed"));
```

- **`GetMeshNodeStream(path).Update(...)` is the only mutation surface.** No `XxxRequest` message type to mutate a node — add a `RequestedXxx` field and watch it from the owning hub → [Request via stream.Update](/Doc/Architecture/RequestViaStreamUpdate).
- **🚨 NEVER replicate-then-save**: don't copy the node into a `/data/{id}` and write it back on a debounce/Save button — two stores drift, the save loop clobbers unedited fields. Bind directly to the node stream.
- Writes are **cold** — the side effect runs on `.Subscribe(...)`; forgetting to subscribe means it silently doesn't happen.

# 4. Editing & forms: declarative, not hand-built

- **Node content, data-bound + auto-persisting** → `MeshNodeContentEditorControl.ForType(path, typeof(MyContent))`; rich content → `MarkdownEditorControl.WithAutoSave`, `MeshNodePickerControl`.
- **Forms** → the `Edit` macro + `[UiControl<T>]` / `[Description]` / `[Editable(false)]` attributes — never hand-build selects/checkboxes/textareas → [Editor](/Doc/GUI/Editor) · [Attributes](/Doc/GUI/Attributes).
- **Pick a mesh node** → `[MeshNode("query")]` → `MeshNodePickerControl` (stores the node PATH).

# 5. Everything is `IObservable<T>` — compose, never `await`

In a layout area (hub-reachable): **no `async`/`await`/`Task<T>`/`Observable.FromAsync`.** Compose with `.Select`/`.SelectMany`/`.Where`/`.CombineLatest` and `.Subscribe`. Click actions return `Task.CompletedTask` (never `async ctx =>`). Async/IO leaves go through `IIoPool`. See [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls) · [Observables](/Doc/GUI/Observables).

The same controls language renders in a native client too — same `GetMeshNodeStream` / `Update`, marshalling UI updates with `MainThread.BeginInvokeOnMainThread`: [Data Binding in a MAUI Client](/Doc/GUI/DataBindingMaui).

# 6. Mesh links in markdown content

Wherever you author markdown — a `Controls.Markdown`, a node body, a doc page — reference other mesh nodes with the `@` notation, never a bare path:

- **Link** (navigate) → single `@`/`@/`: a markdown link `[text](@/Full/Path)`. The renderer strips the `@` and resolves it to the node's URL.
- **Embed** (render inline) → double `@@`: `@@("path/area/Name")` renders a live layout area in place.
- A **bare path is just text**, not a link — always wrap it in `[text](@/Path)`.
- 🚨 `@/` and `@@` are **local mesh-authoring syntax — markdown body only.** NEVER put `@/` inside a raw HTML `href` (an `href` needs a plain `/Path`) or an HTTP URL. Full reference: [Unified Path](/Doc/DataMesh/UnifiedPath).

# The litmus test

If you're reaching for a string of HTML, a `/data` replica + a save subscription, a `*Request` type to change a node, or `.Take(1)` on a live stream — **stop**. You've dropped under the controls language. Find the control, the node stream, or the `RequestedXxx` field. Full reference: GUI/DataBinding, GUI/LayoutAreas, the "Never hand-roll UI" rule in AGENTS.md.
