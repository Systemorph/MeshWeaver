---
Name: Data Binding in MeshWeaver Layout
Category: Documentation
Description: How reactive two-way data binding works in MeshWeaver Layout using JSON Pointers
Icon: /static/DocContent/GUI/DataBinding/icon.svg
---

Data binding connects your data objects to UI controls. You bind an object to the UI, the user edits it, and changes sync back to the server. The binding is **two-way**: the server can push updates to the UI, and user input flows back to the server.

```mermaid
flowchart LR
    subgraph Server
        D[Data Object]
    end
    subgraph Client
        UI[UI Controls]
    end
    D -->|"Server pushes updates"| UI
    UI -->|"User edits sync back"| D
```

# 🚨 The GUI is fully data-bound (read this first)

**Backend layout areas declare *what* to render — they never load instances and they never put concrete values into controls.** All value resolution, every read of a `MeshNode`'s content, and every write-back of user input happens on the GUI side via a per-node `GetRemoteStream<MeshNode, MeshNodeReference>` subscription.

This is non-negotiable, for three reasons:

1. **No deadlocks.** Backend rendering stays purely synchronous (no `await`, no `Task<T>`, no `IAsyncEnumerable`). Every async/await/QueryAsync chain we've put in a layout area has eventually deadlocked the hub or returned stale content. Removing the backend fetch removes the entire problem class.
2. **Live updates.** The GUI subscription stays subscribed for the lifetime of the component. When the underlying node changes anywhere in the mesh, the view re-renders without a refresh. Backend-loaded values freeze on first render.
3. **CQRS-correct.** `GetRemoteStream<MeshNode, MeshNodeReference>(addr, new MeshNodeReference())` is the **authoritative** read path — it goes to the owning hub's workspace, never through the lagged read-side index. See [CQRS — Queries, Reads, Writes, Operations](xref:Architecture/CqrsAndContentAccess).

## The contract

| Side | Responsibility |
|---|---|
| **Backend layout area** | Build a `UiControl` tree. Pass *paths* (or `JsonPointerReference`s) into controls. Never call `meshQuery.QueryAsync(...)`, never `await` data, never call `PermissionHelper.GetEffectivePermissionsAsync(...)` to gate rendering. |
| **GUI Blazor view (.razor.cs)** | Hold an `ISynchronizationStream<MeshNode>` field. In `BindData()`, set it to `workspace.GetRemoteStream<MeshNode, MeshNodeReference>(new Address(path), new MeshNodeReference())`. Subscribe via `AddBinding(...)` and call `InvokeAsync(StateHasChanged)` on emissions. Write user edits back via `_nodeStream.Update(current => ...)`. |

## Backend: declare-only, no fetch

```csharp
// ❌ ANTI-PATTERN — backend loads node, builds control with concrete values
var userNode = await meshQuery.QueryAsync<MeshNode>($"path:{userPath}").FirstOrDefaultAsync();
var card = MeshNodeThumbnailControl.FromNode(userNode, userPath);

// ✅ CORRECT — backend declares the binding, GUI loads + displays
var card = new MeshNodeThumbnailControl { NodePath = userPath };
```

The backend layout-area method **must not** be `async Task<UiControl>`. Return `UiControl` directly. If it needs to reactively rebuild on workspace changes, return `IObservable<UiControl?>` and use `Observable.Return` / `Select` only — no `SelectMany(async ...)`, no `await`.

## GUI: hold a stream, subscribe, update

The canonical Blazor view template (lifted verbatim from `CollaborativeMarkdownView.razor.cs:70-146`):

```csharp
public partial class MyView : BlazorView<MyControl, MyView>
{
    private ISynchronizationStream<MeshNode>? _nodeStream;
    public string? Title { get; private set; }
    public string? ImageUrl { get; private set; }

    protected override void BindData()
    {
        base.BindData();

        // 1. Declare bindings from the control's own properties (DataContext / refs)
        DataBind(ViewModel.NodePath, x => x.NodePath);

        if (string.IsNullOrEmpty(NodePath)) return;

        // 2. Open the per-node stream — single source of truth, live, no lag
        var workspace = Hub.GetWorkspace();
        _nodeStream = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(NodePath), new MeshNodeReference());

        // 3. Subscribe — every emission re-renders this component
        AddBinding(_nodeStream
            .Where(change => change.Value != null)
            .Select(change => change.Value!)
            .DistinctUntilChanged()
            .Subscribe(node =>
            {
                Title = node.Name;
                ImageUrl = MeshNodeThumbnailControl.GetImageUrlForNode(node);
                InvokeAsync(StateHasChanged);
            }));
    }
}
```

Key points:
- `_nodeStream` is a **field**, not a local. It survives across emissions; the same stream serves both reads and writes.
- `AddBinding(...)` registers the subscription with the base class — it auto-disposes on component teardown.
- **No `.Take(1)`** — that snapshots once and the view freezes. Stay subscribed.
- No `try`/`catch` swallowing — let errors propagate; they surface in `Subscribe(onNext, onError)` or in the framework's binding error handler.

## Writing user edits back

The same `_nodeStream` is the write path. Use the synchronous `Update` overload:

```csharp
private void OnTitleChanged(string newTitle)
{
    if (_nodeStream == null) return;
    _nodeStream.Update(current =>
    {
        if (current == null) return null;
        var updated = current with { Name = newTitle };
        return new ChangeItem<MeshNode>(updated, _nodeStream.StreamId,
            _nodeStream.StreamId, ChangeType.Patch, _nodeStream.Hub.Version,
            [new EntityUpdate(nameof(MeshNode), updated.Id, updated) { OldValue = current }]);
    });
}
```

The framework propagates the patch to the owning hub, persists, and notifies all other subscribers — no separate `DataChangeRequest` needed for own-node edits inside a bound view.

## Anti-patterns — never do these

| ❌ Wrong | Why | ✅ Right |
|---|---|---|
| `await meshQuery.QueryAsync<MeshNode>($"path:{x}").FirstOrDefaultAsync()` in a layout area | Lagged index, deadlock-prone, freezes view | Pass path; GUI subscribes to `GetRemoteStream<MeshNode, MeshNodeReference>` |
| `SelectMany(async nodes => await ...)` for data resolution | async lambda inside an observable chain — same deadlock surface | Pass paths; bind in GUI |
| `MeshNodeThumbnailControl.FromNode(loadedNode, ...)` after a backend fetch | Concrete values frozen at render time | `new MeshNodeThumbnailControl { NodePath = path }` |
| `.Take(1)` on a display stream | View stops updating after first emission | Stay subscribed for the lifetime of the component |
| `await PermissionHelper.GetEffectivePermissionsAsync(...)` in a layout area | Hub deadlock candidate | Bind permissions on the GUI side via the user's permission stream |
| `try { ... } catch { /* swallowed */ }` around backend reads | Errors disappear, debugging impossible | Propagate via `OnError`; framework handles it |

## Where to look for working examples

- **`src/MeshWeaver.Blazor/Components/CollaborativeMarkdownView.razor.cs`** — the reference for `_nodeStream` + `AddBinding` + `Update`.
- **`src/MeshWeaver.Blazor/BlazorView.razor.cs`** — the base class. Read it once. Key API: `AddBinding`, `DataBind<T>`, `BindData()` lifecycle.



# Layout Area Structure

A layout area consists of two parts: **areas** (the UI controls) and **data** (the bound objects). Controls reference data locations using `JsonPointerReference`.

```mermaid
flowchart TB
    subgraph LayoutArea["Layout Area"]
        subgraph Areas["areas/"]
            TF1["TextFieldControl<br/>Value: → /data/person/name"]
            TF2["NumberFieldControl<br/>Value: → /data/person/age"]
        end
        subgraph Data["data/"]
            P["person/<br/>{ name: 'Alice', age: 30 }"]
        end
    end
    TF1 -.->|JsonPointerReference| P
    TF2 -.->|JsonPointerReference| P
```

When the user types in the TextFieldControl, the value at `/data/person/name` updates. When server code updates the data, the TextFieldControl displays the new value.

# DataContext

The `DataContext` property sets the base path for data binding. All `JsonPointerReference` values are resolved relative to this path.

```csharp
// EditorControl with DataContext pointing to /data/person
new EditorControl { DataContext = "/data/person" }
```

When you call `Edit(instance, "person")`, the data is stored at `/data/person` and the generated controls have `DataContext = "/data/person"`.

# JsonPointerReference

`JsonPointerReference` points a control's value to a location in the data section. The pointer is **relative to DataContext**:

```csharp
// TextFieldControl bound to the "name" property
new TextFieldControl(new JsonPointerReference("name"))

// NumberFieldControl bound to the "age" property
new NumberFieldControl(new JsonPointerReference("age"))
```

With `DataContext = "/data/person"`:
- `JsonPointerReference("name")` → `/data/person/name`
- `JsonPointerReference("age")` → `/data/person/age`

```mermaid
flowchart LR
    subgraph Control
        DC["DataContext: /data/person"]
        Ref["Value: JsonPointerReference('name')"]
    end
    subgraph Resolved
        Path["/data/person/name"]
    end
    DC --> Path
    Ref --> Path
```

# Updating Data

To update the bound data from server code, use `UpdateData`:

```csharp
// Push new data to the stream
host.UpdateData("person", new Person { Name = "Bob", Age = 25 });
```

This updates `/data/person` and all bound controls automatically reflect the change.

# The Edit Macro

The `Edit` method is the easiest way to create a data-bound editor. It generates controls for each property automatically:

```csharp
// Create an editor for a Calculator
host.Hub.Edit(new Calculator(), "calc");
```

## Property Type Mapping

| Property Type | Generated Control |
|---------------|-------------------|
| `double`, `int`, numeric | `NumberFieldControl` |
| `string` | `TextFieldControl` |
| `DateTime` | `DateTimeControl` |
| `bool` | `CheckBoxControl` |
| `[Dimension<T>]` | `SelectControl` |
| `[UiControl<T>]` | Custom control |

## Example

```csharp
public record Calculator
{
    [Description("The X value")]
    public double X { get; init; }

    [Description("The Y value")]
    public double Y { get; init; }
}

// Creates EditorControl with two NumberFieldControls
// bound to /data/calc/x and /data/calc/y
host.Hub.Edit(new Calculator(), "calc");
```

# Edit with Result Callback

Add a result callback to compute derived values from user input:

```csharp
// Editor that displays X + Y as the user types
host.Hub.Edit(new Calculator(), c => Controls.Markdown($"Result: {c.X + c.Y}"));
```

This creates:
1. Editor controls for X and Y
2. A result area that recalculates whenever either value changes

```mermaid
sequenceDiagram
    participant User
    participant Client
    participant Server

    User->>Client: Type "5" in X field
    Client->>Server: Update /data/{id}/x = 5
    Server->>Server: Invoke callback with Calculator{X=5, Y=0}
    Server->>Client: Return Markdown("Result: 5")
    Client->>User: Display "Result: 5"
```

# Two-Way Sync Details

Changes are synchronized using JSON Patch (RFC 6902) for efficient delta updates:

```json
[{"op": "replace", "path": "/data/calc/x", "value": 5}]
```

- **Client → Server**: User edits create patches sent to the server
- **Server → Client**: Server updates create patches sent to clients

# Control-Specific Bindings

## Dimension Attribute

Properties with `[Dimension]` create select controls with options loaded from the workspace:

```csharp
public record MyForm
{
    [Dimension<Country>]
    public string CountryCode { get; init; }
}
```

## Custom Control Attribute

Use `[UiControl<T>]` to specify which control type to generate:

```csharp
public record MyForm
{
    [UiControl<RadioGroupControl>(Options = new[] { "chart", "table" })]
    public string DisplayMode { get; init; }

    [UiControl<TextAreaControl>]
    public string Notes { get; init; }
}
```

# Best Practices

1. **Use Records**: Immutable records with `init` properties work best for data binding
2. **Add Metadata**: Use `[Description]` and `[Display]` attributes for better generated UIs
3. **Use Edit for Forms**: Let Edit generate controls automatically for standard forms
4. **Callbacks for Computed Values**: Use the result callback pattern for derived values
