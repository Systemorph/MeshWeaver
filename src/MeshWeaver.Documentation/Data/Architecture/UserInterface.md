---
Name: User Interface Architecture
Category: Documentation
Description: How UI components are generated where data lives and streamed to browsers with two-way binding
Icon: /static/DocContent/Architecture/UserInterface/icon.svg
---

MeshWeaver generates UI where the data lives. Instead of transferring large datasets to clients, we compute visualizations server-side and stream only the rendered components. This dramatically reduces network traffic and enables real-time interactivity.

# The Data Compression Principle

Consider displaying a million-row dataset as a 10x10 summary table. Rather than transferring all rows to create a 10x10 grid, we want to transfer only 100 numbers:

@@content:data-compression.svg

# Controls Language

In MessageHubs, we define UI using a **Controls Language** - an immutable, declarative API that serializes to JSON:

```csharp
// Server-side control definition
Controls.Stack
    .WithView(Controls.Text("Welcome!"), "Welcome")
    .WithView(Controls.Button("Click Me").WithClickAction(OnClick), "Button")
    .WithView(Controls.DataGrid(salesData), "Sales")
```

This serializes to JSON and streams to the Portal, which renders it as HTML for the browser.

# Two-Way Data Binding

The synchronization uses a "walkie-talkie" pattern where both sides hold an `ISynchronizationStream`:

```mermaid
flowchart TB
    subgraph Hub["MessageHub"]
        C[Controls Language]
        C --> J[JSON Serialization]
        J --> HS[ISynchronizationStream]
        CH[Click Handler]
        DH[Data Change Handler]
    end
    subgraph Portal["Portal"]
        PS[ISynchronizationStream]
        PS --> R[HTML Renderer]
    end
    subgraph Browser["Browser"]
        V[View Display]
    end
    HS <-->|JSON / JSON Patch| PS
    R -->|HTML| V
    V -->|OnClick / OnChange| PS
    PS -->|ClickedEvent| CH
    PS -->|DataChangedEvent| DH
```

**Key Features:**
- Controls defined server-side where data resides
- `ISynchronizationStream` on both Hub and Portal sides (walkie-talkie pattern)
- HTML rendering happens in the Portal
- Browser is a thin display layer showing HTML and forwarding user events
- Two-way binding: UI changes stream back to hubs as events

# Control Lifecycle

```mermaid
sequenceDiagram
    participant Hub as MessageHub
    participant Portal
    participant Browser
    Hub->>Portal: Stream (controls + data via JSON)
    Portal->>Portal: Render to HTML
    Portal->>Browser: HTML
    Browser->>Browser: Display
    Browser->>Portal: OnClick
    Portal->>Hub: ClickedEvent
    Hub->>Hub: Execute ClickAction
```

## Incremental Updates

After initial load, only changes are transmitted using **JSON Patch** (RFC 6902):

```json
[{"op": "replace", "path": "/areas/counter/Data", "value": 42}]
```

This minimizes bandwidth for real-time updates.

# Available Controls

MeshWeaver provides a rich control library. See the [complete controls reference](AvailableControls) for details.

**Common Controls:**

| Control | Purpose |
|---------|----------|
| `TextFieldControl` | Text input with validation |
| `SelectControl` | Dropdown selection |
| `DataGridControl` | Tabular data display |
| `ButtonControl` | Clickable actions |
| `DialogControl` | Modal dialogs |
| `EditFormControl` | Form containers |
| `LayoutAreaControl` | Nested layout regions |

# Interaction Handling

User interactions become messages:

```csharp
// Define a button with click handler
Controls.Button("Save")
    .WithClickAction(async context =>
    {
        // context.Area - which control was clicked
        // context.Payload - custom data
        // context.Hub - for posting messages
        await context.Hub.Post(new SaveRequest(data));
    })
```

When clicked, the browser sends an `OnClick` to the Portal, which forwards a `ClickedEvent` message to the hub, invoking the registered action.

# Benefits

1. **Bandwidth Efficiency**: Transfer summaries, not raw data
2. **Real-time Updates**: JSON Patch for incremental changes
3. **Security**: Data never leaves the server unnecessarily
4. **Consistency**: Single source of truth on server
5. **Flexibility**: Any control can be data-bound
