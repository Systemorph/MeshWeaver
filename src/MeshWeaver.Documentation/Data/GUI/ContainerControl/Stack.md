---
Name: Arranging UI Controls in a Stack
Category: Documentation
Description: Arrange controls vertically or horizontally with configurable spacing and alignment
Icon: /static/DocContent/GUI/ContainerControl/Stack/icon.svg
---

`Controls.Stack` is the fundamental layout primitive in MeshWeaver's UI system. It arranges child controls in a single axis — vertical by default, or horizontal when you need a toolbar or button row — with full control over spacing, alignment, and wrapping. Stacks compose freely: nest them to build any layout from simple forms to multi-panel dashboards.

---

# Quick Start

The simplest stack is vertical and requires no configuration at all — just chain `WithView` calls:

```csharp --render StackVertical --show-code
Controls.Stack
    .WithView(Controls.Html("<b>Stack layout demo</b>"))
    .WithView(Controls.Html("First item"))
    .WithView(Controls.Html("Second item, directly below"))
    .WithView(Controls.Html("Third item, with default spacing"))
```

Switch to horizontal by adding a single `.WithOrientation` call:

```csharp --render StackHorizontal --show-code
Controls.Stack
    .WithOrientation(Orientation.Horizontal)
    .WithHorizontalGap("12px")
    .WithView(Controls.Button("Save"))
    .WithView(Controls.Button("Cancel"))
```

---

# Configuration Reference

Every `With*` method returns a **new** `StackControl` instance — the stack is immutable and composable.

| Method | Purpose | Example values |
|---|---|---|
| `WithOrientation(orientation)` | Layout axis | `Orientation.Vertical` (default), `Orientation.Horizontal` |
| `WithVerticalGap(gap)` | Space between items on the vertical axis | `"8px"`, `"1rem"`, `"16px"` |
| `WithHorizontalGap(gap)` | Space between items on the horizontal axis | `"8px"`, `"1rem"`, `"16px"` |
| `WithHorizontalAlignment(align)` | Cross-axis or main-axis horizontal alignment | `"start"`, `"center"`, `"end"` |
| `WithVerticalAlignment(align)` | Cross-axis or main-axis vertical alignment | `"start"`, `"center"`, `"end"` |
| `WithWidth(width)` | Explicit stack width | `"300px"`, `"100%"` |
| `WithHeight(height)` | Explicit stack height | `"200px"`, `"100%"` |
| `WithWrap(wrap)` | Allow items to wrap onto the next row/column | `true`, `false` |

---

# Adding Child Controls

`WithView` has several overloads to cover static, dynamic, and context-aware content:

```csharp
// Static control
.WithView(Controls.Label("Text"))

// Named area (useful for targeted updates)
.WithView(Controls.Button("Click"), "buttonArea")

// Dynamic — updates whenever the stream emits a new value
.WithView(dataStream.Select(d => Controls.Label(d.Name)))

// Context-aware — access the render context inside the factory
.WithView((host, ctx) => Controls.Label($"Area: {ctx.Area}"))
```

---

# Common Patterns

## Right-Aligned Button Group

Pair `Orientation.Horizontal` with `WithHorizontalAlignment("end")` to push a button row to the right edge — the standard footer pattern for dialogs and forms:

```csharp
Controls.Stack
    .WithOrientation(Orientation.Horizontal)
    .WithHorizontalGap("8px")
    .WithHorizontalAlignment("end")
    .WithView(Controls.Button("Cancel"))
    .WithView(Controls.Button("Save"))
```

## Form with Action Bar

Nest a horizontal button stack inside a vertical form stack to separate the editor from its actions cleanly:

```csharp
Controls.Stack
    .WithVerticalGap("16px")
    .WithView(host.Edit(new UserData()))
    .WithView(
        Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap("8px")
            .WithHorizontalAlignment("end")
            .WithView(Controls.Button("Cancel"))
            .WithView(Controls.Button("Submit"))
    )
```

## Multi-Panel Dashboard

Outer vertical stack for header/content/footer, inner horizontal stack for side-by-side panels:

```csharp
Controls.Stack
    .WithView(Controls.Html("<h1>Dashboard</h1>"))
    .WithView(
        Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap("16px")
            .WithView(BuildLeftPanel())
            .WithView(BuildRightPanel())
    )
    .WithView(Controls.Html("<footer>Footer</footer>"))
```

---

# Skin Properties

The underlying `LayoutStackSkin` record maps directly to the `With*` methods above. You will encounter these property names when inspecting serialized layout state or writing custom renderers:

| Property | Type | Default |
|---|---|---|
| `Orientation` | `object?` | `Orientation.Vertical` |
| `HorizontalAlignment` | `object?` | `null` |
| `VerticalAlignment` | `object?` | `null` |
| `HorizontalGap` | `object?` | `null` |
| `VerticalGap` | `object?` | `null` |
| `Wrap` | `object?` | `null` |
| `Width` | `object?` | `null` |
| `Height` | `object?` | `null` |

---

# See Also

- [Container Control](../../ContainerControl) — overview of all container types
- [Editor Control](../../Editor) — auto-generated form editors
- [DataGrid Control](../../DataGrid) — tabular data display
