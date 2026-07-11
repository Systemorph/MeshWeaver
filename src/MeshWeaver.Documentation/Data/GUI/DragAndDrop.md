---
Name: Drag & Drop
Category: Documentation
Description: Generic Draggable and DropTarget controls — make any control a drag source, any container a drop zone, and handle the drop server-side.
Icon: /static/DocContent/GUI/icon.svg
---

`DraggableControl` and `DropTargetControl` are two **generic primitives**: wrap any control to make it a drag source, wrap any container to make it a drop zone. They carry no opinion about *what* you're building — compose them into reorderable lists, kanban boards, trees, file drop areas, and so on. The drop is handled **server-side** the same way a button click is: it flows back to the layout hub as a message and invokes a handler on the target.

Like every MeshWeaver control they render identically across the Blazor portal, the React web client, and React Native — the payload is carried between drag source and drop target by the native drag data-transfer, and only the final drop crosses back to the server.

---

# The primitives

```csharp
// A drag source: wrap any control, attach a payload that identifies it.
Controls.Draggable(Controls.Html("<div class=\"card\">Design API</div>"), payload: "card-1")

// A drop zone: wrap any control, handle the drop server-side.
Controls.DropTarget(Controls.Html("<h4>Done</h4>"))
    .WithDropAction(context => Console.WriteLine($"dropped {context.Payload}"))
```

- **`Controls.Draggable(content, payload)`** — `content` is any `UiControl`, rendered inside the draggable wrapper; `payload` is a serializable value delivered to the target on drop.
- **`Controls.DropTarget(content)`** — `content` is the drop-zone body (any `UiControl`); `.WithDropAction(...)` registers the server-side handler.

The example below renders a draggable card next to a drop zone. Drag the card onto the zone:

```csharp --render DragDropPrimitive --show-code
Controls.Stack
    .WithSkin(s => s.WithOrientation(Orientation.Horizontal).WithHorizontalGap(24))
    .WithView(
        Controls.Draggable(
            Controls.Html("<div style=\"padding:12px 16px;border:1px solid #b0bec5;border-radius:8px;background:#fff;cursor:grab\">Drag me</div>"),
            payload: "card-1"))
    .WithView(
        Controls.DropTarget(
            Controls.Html("<div style=\"padding:24px;border:2px dashed #90a4ae;border-radius:8px;color:#607d8b\">Drop here</div>")))
```

---

# A worked example: a kanban board

Drag-and-drop is only as good as the state it rearranges. The reusable logic is a **pure reducer** — moving a card between columns — and a **composition** that wraps each card in a `Draggable` and each column in a `DropTarget`. This is the example shipped in `MeshWeaver.Documentation.GUI.DragDropExample`, pinned by `DragDropExampleTest`.

The state and the reducer — no framework types, just data:

```csharp
public record BoardState(ImmutableDictionary<string, ImmutableList<string>> Columns)
{
    public string? ColumnOf(string card)
        => Columns.FirstOrDefault(kv => kv.Value.Contains(card)).Key;
}

// Move a card to the end of a column, removing it from wherever it was.
// Pure and total: same-column, unknown-column, and unknown-card drops are no-ops.
public static BoardState Move(BoardState state, string card, string toColumn)
{
    if (!state.Columns.ContainsKey(toColumn)) return state;
    var from = state.ColumnOf(card);
    if (from is null || from == toColumn) return state;
    return state with
    {
        Columns = state.Columns
            .SetItem(from, state.Columns[from].Remove(card))
            .SetItem(toColumn, state.Columns[toColumn].Add(card))
    };
}
```

The composition — each card a `Draggable` carrying its id, each column a `DropTarget` whose handler folds `Move` into the board:

```csharp
public static UiControl Board(BoardState state, Action<string, string> onDrop)
{
    var board = Controls.Stack.WithSkin(s => s.WithOrientation(Orientation.Horizontal));
    foreach (var column in BoardState.ColumnOrder)
    {
        var cards = Controls.Stack;
        foreach (var card in state.Columns[column])
            cards = cards.WithView(Controls.Draggable(Controls.Html($"<div class=\"card\">{card}</div>"), card));

        var target = column; // capture per iteration
        board = board.WithView(
            Controls.DropTarget(Controls.Stack.WithView(Controls.Html($"<h4>{column}</h4>")).WithView(cards))
                .WithDropAction(context => onDrop((string)context.Payload!, target)));
    }
    return board;
}
```

In a live area, `onDrop` folds `Move` into the area's state stream and the affected columns re-render — reactively, with no `async`/`await`. The drop handler receives the dragged card as `context.Payload`, exactly the value passed to `Controls.Draggable(..., payload: card)`.

---

# How the drop travels back

A drop is a hub message, not a callback across the wire:

1. On `dragstart`, the client stashes the draggable's **payload** in the native drag data-transfer. No server round-trip yet.
2. On `drop`, the drop target reads the payload and posts a **`DropEvent`** (carrying the payload and the target's area) to the layout hub — the same mechanism `ButtonControl` uses for `ClickedEvent`.
3. `LayoutAreaHost` routes the event to the target control and invokes its **`DropAction`** with a `DropContext { Area, Payload, Hub, Host }`.

The `DropAction` is a server-side delegate — it never serializes to the client. In the **React** and **React Native** renderers the payload rides the drag data-transfer, so dragging generates no server traffic — only the drop crosses the wire. The **Blazor Server** renderer instead records the payload on `dragstart` over the circuit (a lightweight round-trip) and posts the `DropEvent` on drop.

---

# See also

- [Container Controls](../ContainerControl) — Stack, Tabs, Toolbar, Splitter for composing the zones
- [Item Templates](../DataBinding/ItemTemplate) — render a bound collection as draggable items
- [Data Binding](../DataBinding) — fold the drop into a reactive state stream
- [Custom React Controls](../ReactCustomControls) — how the same `$type` renders in the React clients
