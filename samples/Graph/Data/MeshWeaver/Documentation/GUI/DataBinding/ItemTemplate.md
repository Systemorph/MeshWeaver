---
Name: Item Templates
Category: Documentation
Description: How to render collections of items using data-bound ItemTemplateControl with BindMany
---

# Item Templates

`ItemTemplateControl` repeats a view template for each item in a collection. It is the MeshWeaver equivalent of a "for-each" renderer, binding each element of an array to a template view.

## Usage Patterns

### 1. Static Collection

Use `BindMany` on an `IEnumerable<T>` to render a fixed list of items:

```csharp
var items = new[] { new User("Alice"), new User("Bob") };
var control = items.BindMany(item => Controls.Label(item.Name));
```

The expression tree `item => Controls.Label(item.Name)` is analyzed at build time: property accessors like `item.Name` are replaced with `JsonPointerReference("name")` bindings.

### 2. Observable Stream

Use `BindMany` on an `IObservable<IEnumerable<T>>` for reactive collections that update over time:

```csharp
IObservable<IEnumerable<AccessAssignment>> assignmentStream = ...;

var control = assignmentStream.BindMany("assignments", a =>
    Controls.Stack
        .WithOrientation(Orientation.Horizontal)
        .WithView(Controls.Label(a.DisplayLabel))
        .WithView(Controls.Switch(a.IsActive))
);
```

The `id` parameter (`"assignments"`) identifies the data stream. The observable pushes updates into the layout host's data store, and the client re-renders automatically.

### 3. Nested in Template.Bind

Combine `Template.Bind` with `Template.BindMany` to bind both a parent object and its child collection:

```csharp
return Template.Bind(
    filterEntity,
    x => Template.BindMany(x.Items, y => Controls.CheckBox(y.Value)),
    "myFilter"
);
```

This creates an `ItemTemplateControl` whose `DataContext` points to the parent object's data stream, and whose `Data` property is a `JsonPointerReference` pointing to the `items` sub-path within that context.

### 4. Workspace Stream

Use `GetStream<T>()` on a workspace to get a reactive stream of workspace-managed data, then bind it with `BindMany`:

```csharp
var stream = host.Workspace.GetStream<MyType>()
    ?.Select(items => items?.AsEnumerable() ?? Enumerable.Empty<MyType>())
    ?? Observable.Return(Enumerable.Empty<MyType>());

var control = stream.BindMany("my_stream_id", item =>
    Controls.Stack.WithView(Controls.Label(item.Name))
);
```

Unlike the plain observable pattern (pattern 2), the data here is managed by the workspace's `TypeSource`. The TypeSource handles loading data from a backing store on initialization and syncing changes back when the workspace receives a `DataChangeRequest`. You don't need to manually push data via `SetData` — the workspace pipeline handles it.

This pattern is useful when the data is already registered as a workspace type (e.g., via `WithTypeSource` on a `DataSource`), and you want the standard data binding pipeline to manage persistence and change tracking.

## How Data Binding Works

When the expression tree is compiled, MeshWeaver's `TemplateBuilderVisitor` replaces each property accessor with a `JsonPointerReference`:

```
Expression:  item => Controls.Label(item.DisplayLabel)
Compiled to: Controls.Label(new JsonPointerReference("displayLabel"))
```

On the Blazor client, `ItemTemplate.razor` iterates over the bound collection and renders one copy of the `View` template per item. Each item gets a `DataContext` path like:

```
/data/"assignments"/0    -- first item
/data/"assignments"/1    -- second item
```

The template's `JsonPointerReference("displayLabel")` resolves relative to each item's DataContext, producing paths like `/data/"assignments"/0/displayLabel`.

## Data Flow

```mermaid
graph LR
    A[Observable Stream] -->|Push data| B[Layout Host Data Store]
    B -->|JSON Patch| C[Client Workspace]
    C -->|DataBind| D[ItemTemplate.razor]
    D -->|For each item| E[Rendered View Copy]
```

1. The server-side observable pushes data into the layout host via `SetData`
2. Changes propagate to the client as JSON Patch operations
3. `ItemTemplate.razor` subscribes to the data at `DataContext` via `DataBind`
4. For each element in the array, a copy of `View` is rendered with item-scoped DataContext

## Reference

- `Template.BindMany` methods: `src/MeshWeaver.Layout/Template.cs`
- `ItemTemplateControl` record: `src/MeshWeaver.Layout/ItemTemplateControl.cs`
- Blazor renderer: `src/MeshWeaver.Blazor/Components/ItemTemplate.razor`
- Layout test examples: `test/MeshWeaver.Layout.Test/LayoutTest.cs` (see `TestItemTemplate`, `TestDataBoundCheckboxes`)
