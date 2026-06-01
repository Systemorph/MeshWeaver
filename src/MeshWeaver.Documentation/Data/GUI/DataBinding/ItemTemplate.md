---
Name: Item Templates
Category: Documentation
Description: Render reactive collections with ItemTemplateControl and BindMany — MeshWeaver's declarative for-each renderer
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="18" height="5" rx="1"/><rect x="3" y="10" width="18" height="5" rx="1"/><rect x="3" y="17" width="18" height="5" rx="1" stroke-dasharray="3 2"/></svg>
---

`ItemTemplateControl` is MeshWeaver's declarative for-each renderer. Give it a collection and a view template, and it stamps out one copy of that template per item — reactively, keeping the DOM in sync as the collection changes.

# Usage Patterns

There are four ways to supply data to `BindMany`, depending on where your collection lives.

## 1. Static Collection

The simplest case: you already have an in-memory array or list.

```csharp
var items = new[] { new User("Alice"), new User("Bob") };
var control = items.BindMany(item => Controls.Label(item.Name));
```

MeshWeaver's `TemplateBuilderVisitor` analyses the expression tree at build time and rewrites property accessors — `item.Name` becomes a `JsonPointerReference("name")` binding. The resulting template is entirely data-driven and renders correctly on the client without any runtime reflection.

## 2. Observable Stream

For collections that change over time, pass an `IObservable<IEnumerable<T>>`. The client re-renders automatically whenever the stream emits.

```csharp
IObservable<IEnumerable<AccessAssignment>> assignmentStream = ...;

var control = assignmentStream.BindMany("assignments", a =>
    Controls.Stack
        .WithOrientation(Orientation.Horizontal)
        .WithView(Controls.Label(a.DisplayLabel))
        .WithView(Controls.Switch(a.IsActive))
);
```

The `id` parameter (`"assignments"`) names the data stream inside the layout host's store. New emissions from the observable propagate to the client as JSON Patch operations — only the changed elements update.

## 3. Nested Inside `Template.Bind`

When you need to bind both a parent object and a child collection within it, combine `Template.Bind` with `Template.BindMany`:

```csharp
return Template.Bind(
    filterEntity,
    x => Template.BindMany(x.Items, y => Controls.CheckBox(y.Value)),
    "myFilter"
);
```

This creates an `ItemTemplateControl` whose `DataContext` points to the parent object's stream, and whose `Data` property is a `JsonPointerReference` into the `items` sub-path of that context. The parent and children share a single coherent data tree.

## 4. Workspace Stream

When your data is already registered as a workspace type (via `WithTypeSource` on a `DataSource`), retrieve it through the workspace and bind with `BindMany`:

```csharp
var stream = host.Workspace.GetStream<MyType>()
    ?.Select(items => items?.AsEnumerable() ?? Enumerable.Empty<MyType>())
    ?? Observable.Return(Enumerable.Empty<MyType>());

var control = stream.BindMany("my_stream_id", item =>
    Controls.Stack.WithView(Controls.Label(item.Name))
);
```

> **Tip:** Unlike the plain observable pattern above, you do not call `SetData` manually here. The workspace's `TypeSource` handles loading from the backing store on initialization and syncing changes whenever a `DataChangeRequest` arrives. The whole persistence and change-tracking pipeline is automatic.

# How Data Binding Works

When `BindMany` compiles the expression tree, `TemplateBuilderVisitor` replaces every property accessor with a `JsonPointerReference`:

```
Expression:  item => Controls.Label(item.DisplayLabel)
Compiled to: Controls.Label(new JsonPointerReference("displayLabel"))
```

On the Blazor client, `ItemTemplate.razor` iterates over the bound collection and renders one copy of the `View` template per item. Each item receives a scoped `DataContext` path:

```
/data/"assignments"/0    -- first item
/data/"assignments"/1    -- second item
```

The template's `JsonPointerReference("displayLabel")` resolves relative to each item's `DataContext`, producing paths like `/data/"assignments"/0/displayLabel`. Pointer resolution is entirely client-side — no round-trip per item.

# Data Flow

```mermaid
graph LR
    A[Observable Stream] -->|Push data| B[Layout Host Data Store]
    B -->|JSON Patch| C[Client Workspace]
    C -->|DataBind| D[ItemTemplate.razor]
    D -->|For each item| E[Rendered View Copy]
```

| Step | What happens |
|------|-------------|
| 1 | The server-side observable pushes data into the layout host via `SetData` |
| 2 | Changes propagate to the client as JSON Patch operations |
| 3 | `ItemTemplate.razor` subscribes to the data at `DataContext` via `DataBind` |
| 4 | For each element in the array, a copy of `View` is rendered with an item-scoped `DataContext` |

# Live Demo

The cell below builds a small static collection and renders it with `BindMany`, so you can see the template expansion in action:

```csharp --render ItemTemplateDemo --show-code
record UserRow(string Name, string Role);

var rows = new[]
{
    new UserRow("Alice",   "Admin"),
    new UserRow("Bob",     "Editor"),
    new UserRow("Charlie", "Viewer"),
};

MeshWeaver.Layout.Controls.Stack
    .WithView(MeshWeaver.Layout.Controls.Html("<b>Users</b>"))
    .WithView(
        rows.BindMany(u =>
            MeshWeaver.Layout.Controls.Html($"<div>{u.Name} — <em>{u.Role}</em></div>")
        )
    )
```

# Reference

| Resource | Path |
|----------|------|
| `Template.BindMany` methods | `src/MeshWeaver.Layout/Template.cs` |
| `ItemTemplateControl` record | `src/MeshWeaver.Layout/ItemTemplateControl.cs` |
| Blazor renderer | `src/MeshWeaver.Blazor/Components/ItemTemplate.razor` |
| Layout test examples | `test/MeshWeaver.Layout.Test/LayoutTest.cs` — see `TestItemTemplate`, `TestDataBoundCheckboxes` |
