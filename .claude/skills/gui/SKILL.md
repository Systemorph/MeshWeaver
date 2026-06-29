---
name: gui
description: Build or review ANY MeshWeaver UI — layout areas, editable views, forms, tables, content editing, side-panel composers — by wiring the framework's EXISTING data-binding pieces, NEVER hand-rolling. The one rule is ALWAYS DATA-BIND, especially editable views: the backend layout area declares WHAT to render (a UiControl tree + node PATHS), and every value read / write-back happens on the GUI side through the per-node IMeshNodeStreamCache. Hand-woven binding — replicating a node into a /data/{id} copy + a save subscription, emitting HTML strings, hand-built selects/checkboxes, .Take(1) on a live stream, async/await/QueryAsync in a layout area — is the bug class behind "awaiting first data" / "renders empty" / a stuck editable view. Use when writing or reviewing a layout area, control, editor, form, table, or any data-bound view. Grounded in src/MeshWeaver.Layout/DataBinding/, Doc/GUI/DataBinding.md + Editor.md, the Edit extension (EditorExtensions.cs), and the node-bound editor controls.
user-invocable: true
allowed-tools:
  - Read
  - Bash
  - Grep
  - Edit
---

# /gui — Always data-bind. Never hand-roll. Especially editable views.

A "UI feature" in MeshWeaver means **wiring up the framework's existing pieces** — data binding, the
controls language, the `Edit` editor, the node-bound editor controls, auto-persist. The framework
already does rendering, two-way binding, and persistence ONE standard way that every layout area
uses. Hand-rolling a parallel version — a `/data` copy of the node, a save subscription, an HTML
string, a hand-built `<select>` — is FORBIDDEN and is the root of the **"awaiting first data" /
"renders empty" / stuck editable view** bug class.

> Canonical references:
> - [DataBinding.md](../../../src/MeshWeaver.Documentation/Data/GUI/DataBinding.md) — the Golden Rule + the canonical Blazor view template.
> - [Editor.md](../../../src/MeshWeaver.Documentation/Data/GUI/Editor.md) + [Attributes.md](../../../src/MeshWeaver.Documentation/Data/GUI/Attributes.md) — record → form via `host.Edit(...)`.
> - `src/MeshWeaver.Layout/DataBinding/` — `Binding` / `JsonPointerReference`, `ReactiveExtensions` (`Debounce`, `ThrottleImmediate`).
> - `src/MeshWeaver.Layout/EditorExtensions.cs` — the `Edit` macro overloads.
> - `src/MeshWeaver.Graph/MeshNodeContentEditorControl.cs` (`ForType`), `src/MeshWeaver.Layout/MarkdownEditorControl.cs` (`WithAutoSave`).
> - AGENTS.md → "🚨🚨🚨 ABSOLUTE: Never hand-roll UI / data-binding / persistence / submit"; memory `feedback_no_handrolling`.

## The Golden Rule

> **The backend layout area declares WHAT to render and never fetches instances or puts concrete
> values into controls. Every value read of a `MeshNode`, and every write-back of user input,
> happens on the GUI side through the per-node `IMeshNodeStreamCache`.**

```csharp
// ❌ backend loads the node and bakes values in — freezes on first render, can deadlock the hub
var node = await meshQuery.QueryAsync<MeshNode>($"path:{path}").FirstOrDefaultAsync();
var card = MeshNodeThumbnailControl.FromNode(node, path);

// ✅ backend declares the binding PATH; the GUI loads + displays + stays live
var card = new MeshNodeThumbnailControl { NodePath = path };
```

Backend layout-area methods return `UiControl` (or `IObservable<UiControl?>` composed with
`Observable.Return`/`Select`) — **never** `async Task<UiControl>`, **never** `await`, **never**
`QueryAsync`, **never** `await PermissionHelper.GetEffectivePermissions(...)` (compose its
`IObservable<Permission>` with `CombineLatest`). Three reasons: no hub deadlocks, live updates, and
CQRS-correct authoritative reads (`GetMeshNodeStream(path)` ≠ the lagged read index).

## Editable views — ALWAYS data binding (this is where hand-rolling sneaks in)

There are exactly three sanctioned editable surfaces. Pick by what you're editing:

| You are editing | Use this — it is already node-bound + auto-persisting |
|---|---|
| A plain C# record into a form | `host.Edit(instance)` / `host.Edit(typeof(T), id)` — the **Editor**. Reflects properties → typed field controls; `[Required]`/`[DisplayName]`/`[Range]`/`[Browsable(false)]`/`[Editable(false)]` drive rendering. See Editor.md / Attributes.md. |
| A mesh node's **content** (simple scalar/bool fields), data-bound + auto-persisting | `MeshNodeContentEditorControl.ForType(nodePath, typeof(MyContent))` — binds the GUI directly to `Hub.GetMeshNodeStream(nodePath)`; each field writes via `GetMeshNodeStream(nodePath).Update(...)`. ONE source of truth: the node stream. |
| A node's **markdown / rich body** | `MarkdownEditorControl.WithAutoSave(hubAddress, nodePath)` or `CollaborativeMarkdownControl` — already node-bound, debounced auto-save. |
| **Picking** a mesh node | `[MeshNode("query")]` → `MeshNodePickerControl` (stores the node PATH). |

The write primitive underneath all of them is the ONE mutation API:
`workspace.GetMeshNodeStream(path).Update(cur => cur with { Content = … }).Subscribe(_ => {}, onError)`.
It is COLD — the Subscribe runs the write. The owning per-node hub serialises every writer; cross-hub
writes send only an RFC-7396 merge patch, so concurrent editors never clobber each other's fields.

## 🚨 The hand-woven tells — FORBIDDEN (these cause "awaiting first data")

If you are reaching for any of these to build a UI feature, STOP and find the control/macro above:

1. **Replicate-then-save** (the #1 offender): `host.UpdateData(id, node.Content)` +
   `GetDataStream(id).Debounce/Throttle.Subscribe(… GetMeshNodeStream(path).Update …)` — a.k.a. any
   `*AutoSave` helper, a "Save" button that reads `/data` and writes the node, or a layout area that
   replicates a node into a `/data/{id}` copy. **Two stores drift; the `/data/content_{node}` copy
   never seeds its initial value → the editable form binds to a stream that never emits → the area is
   stuck "awaiting first data" forever, fully idle (no storm, no error).** This is exactly how an
   editable Overview hangs. The fix is to bind the editor DIRECTLY to the node stream (the table
   above), never a `/data` replica.
2. **Emitting HTML strings for structured data**: `StringBuilder`/`$"<table>…"`/`$"<td>…"`, any
   `RenderHtml`-shaped helper, or `Controls.Html(handBuiltMarkup)`. → use a CONTROL:
   `Controls.DataGrid(rows).WithColumn(new PropertyColumnControl<T> { Property = nameof(Row.X).ToCamelCase() }.WithTitle("…").WithFormat("N0"))`,
   composed with `Controls.Stack` / `Controls.LayoutGrid` / `Controls.Title` / `Controls.Markdown`.
   (`Controls.Html` is ONLY for genuinely pre-rendered markdown/rich text.)
3. **Hand-built `<select>`/checkbox/textarea + a `/data` section** → the `Edit` macro + the attributes
   above, or `Controls.Select` / `Controls.Combobox` / `Controls.Listbox` / `Controls.Text`.
4. **`.Take(1)` on a stream feeding a live data-bound view** — freezes the binding. Stay subscribed
   for the component's lifetime (`AddBinding(...)`).
5. **`workspace.GetRemoteStream<MeshNode, MeshNodeReference>(addr, …)` inside a Blazor view** —
   bypasses the cache; writes through `_cache.Update` won't be observed. Use `_cache.GetStream(path)`.
6. **`async`/`await`/`Task<T>`/`.ToTask()`/`QueryAsync`/`SelectMany(async …)` in a layout area** —
   deadlocks the hub; pairs with the [/async](../async/SKILL.md) rule.

## The controls language

Compose `UiControl`s from the `Controls` factory (`src/MeshWeaver.Layout/Controls.cs`) — never build
markup. Containers: `Controls.Stack`, `Controls.LayoutGrid`, `Controls.Splitter`, `Controls.Tabs`,
`Controls.Toolbar`, `Controls.NavMenu`. Content: `Controls.Markdown`, `Controls.Title`,
`Controls.Text`, `Controls.DataGrid`, charts (`ChartControl`). Inputs: `Controls.Select`,
`Controls.Combobox`, `Controls.Listbox`, `Controls.Text`. Column API + a worked table:
`samples/Graph/Data/Cornerstone/Pricing/Source/PricingLayoutAreas.cs`.

## GUI side: subscribe via the cache (the canonical Blazor view)

```csharp
public partial class MyView : BlazorView<MyControl, MyView>
{
    private IMeshNodeStreamCache? _cache;
    protected override void BindData()
    {
        base.BindData();
        DataBind(ViewModel.NodePath, x => x.NodePath);
        if (string.IsNullOrEmpty(NodePath)) return;
        _cache = Hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        AddBinding(_cache.GetStream(NodePath)            // shared upstream handle; access-checked
            .Where(n => n is not null).DistinctUntilChanged()
            .Subscribe(n => { Title = n.Name; InvokeAsync(StateHasChanged); }));   // NO .Take(1)
    }
    private void OnTitleChanged(string t) =>             // writes route through the SAME cache
        _cache!.Update(NodePath, c => c with { Name = t })
               .Subscribe(_ => {}, ex => Logger.LogWarning(ex, "update failed {Path}", NodePath));
}
```

`_cache` is a **field** (writers reuse it); `AddBinding` auto-disposes the subscription on teardown;
`_cache.GetStream` terminates with `UnauthorizedAccessException` if the user lacks Read — handle it
(toast / empty state), don't swallow. Debounce field edits with
`MeshWeaver.Layout.DataBinding.ReactiveExtensions.Debounce` / `ThrottleImmediate`, not a hand-rolled timer.

## Diagnose a stuck / empty editable view

`"Rendering {area}... awaiting first data"` (from `LayoutAreaHost`), a form that renders blank, or an
editor that never accepts input → almost always ONE of:

1. **A hand-woven `/data` replica** (tell #1) whose stream never seeds → bind the editor to the node
   stream instead (the editable-views table).
2. **The content type isn't registered** in the TypeRegistry on the owning/reading hub → the content
   "stayed an untyped JsonElement after deserialization" and `Content is T` consumers get nothing.
   Register with `config.TypeRegistry.WithType(typeof(T), nameof(T))` on the hub that reads it (and,
   for AI partitions, `AddAITypes`). Grep the logs for `untyped JsonElement` / `not a registered type`.

Both are *silent* (no storm, no exception, just a never-resolving binding) — confirm with a
Debug-level `ILogger` file capture or `MESHWEAVER_MSG_TRACE=1`, not by raising a timeout.

## Before writing ANY UI — checklist

- [ ] Did you FIND the existing layout area / control / `Edit` macro / node-bound editor control and reuse it?
- [ ] Backend area returns `UiControl` / `IObservable<UiControl?>` — no `async`/`await`/`QueryAsync`/`Task<T>`.
- [ ] Editing a node's content? Bound DIRECTLY to the node stream (`MeshNodeContentEditorControl.ForType` / `MarkdownEditorControl.WithAutoSave` / `GetMeshNodeStream(path).Update`) — **not** a `/data/{id}` replica + save subscription.
- [ ] Structured/tabular data rendered with `Controls.DataGrid`/`Stack`/`LayoutGrid` — **no** HTML strings.
- [ ] Form fields from a record via `host.Edit(...)` + attributes — **no** hand-built selects/checkboxes.
- [ ] GUI view reads via `_cache.GetStream(path)` (field, not local), writes via `_cache.Update(path, fn)`, **no** `.Take(1)`, **no** `GetRemoteStream` in the view.
- [ ] The content type is registered in the TypeRegistry of the hub that reads it.
- [ ] Cold write observables are `.Subscribe(...)`d with an onError that surfaces at a real boundary.
