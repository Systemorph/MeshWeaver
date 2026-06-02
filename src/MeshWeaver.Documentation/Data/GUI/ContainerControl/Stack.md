---
Name: Arranging UI Controls in a Stack
Category: Documentation
Description: Arrange controls vertically or horizontally with configurable spacing and alignment
Icon: /static/DocContent/GUI/ContainerControl/Stack/icon.svg
---

`Controls.Stack` is the fundamental layout primitive in MeshWeaver's UI system. It arranges child controls in a single axis — vertical by default, or horizontal when you need a toolbar or button row — with full control over spacing, alignment, and wrapping. Stacks compose freely: nest them to build any layout from simple forms to multi-panel dashboards.

<svg viewBox="0 0 760 260" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity=".55"/>
    </marker>
  </defs>
  <rect x="20" y="20" width="160" height="220" rx="10" fill="#1e3a5f" stroke="currentColor" stroke-opacity=".25" stroke-width="1.5"/>
  <text x="100" y="44" text-anchor="middle" fill="#90caf9" font-size="12" font-weight="bold">Vertical Stack</text>
  <text x="100" y="58" text-anchor="middle" fill="currentColor" fill-opacity=".45" font-size="10">(default)</text>
  <rect x="36" y="68" width="128" height="36" rx="7" fill="#1e88e5"/>
  <text x="100" y="91" text-anchor="middle" fill="#fff" font-size="12">Item 1</text>
  <rect x="36" y="116" width="128" height="36" rx="7" fill="#1e88e5"/>
  <text x="100" y="139" text-anchor="middle" fill="#fff" font-size="12">Item 2</text>
  <rect x="36" y="164" width="128" height="36" rx="7" fill="#1e88e5"/>
  <text x="100" y="187" text-anchor="middle" fill="#fff" font-size="12">Item 3</text>
  <text x="100" y="225" text-anchor="middle" fill="currentColor" fill-opacity=".4" font-size="10">WithOrientation(Vertical)</text>
  <line x1="36" y1="108" x2="36" y2="112" stroke="currentColor" stroke-opacity=".3" stroke-width="1" stroke-dasharray="2,2"/>
  <line x1="36" y1="156" x2="36" y2="160" stroke="currentColor" stroke-opacity=".3" stroke-width="1" stroke-dasharray="2,2"/>
  <rect x="210" y="20" width="260" height="220" rx="10" fill="#1b3a2f" stroke="currentColor" stroke-opacity=".25" stroke-width="1.5"/>
  <text x="340" y="44" text-anchor="middle" fill="#a5d6a7" font-size="12" font-weight="bold">Horizontal Stack</text>
  <rect x="226" y="58" width="72" height="40" rx="7" fill="#43a047"/>
  <text x="262" y="83" text-anchor="middle" fill="#fff" font-size="12">Item 1</text>
  <rect x="314" y="58" width="72" height="40" rx="7" fill="#43a047"/>
  <text x="350" y="83" text-anchor="middle" fill="#fff" font-size="12">Item 2</text>
  <rect x="402" y="58" width="52" height="40" rx="7" fill="#43a047"/>
  <text x="428" y="83" text-anchor="middle" fill="#fff" font-size="12">Item 3</text>
  <text x="340" y="120" text-anchor="middle" fill="currentColor" fill-opacity=".4" font-size="10">WithOrientation(Horizontal)</text>
  <rect x="226" y="138" width="220" height="82" rx="8" fill="#1e3a5f" stroke="#5c6bc0" stroke-opacity=".5" stroke-width="1.5"/>
  <text x="336" y="158" text-anchor="middle" fill="#90caf9" font-size="11" font-weight="bold">Nested: Form + Action Bar</text>
  <rect x="238" y="166" width="196" height="24" rx="5" fill="#5c6bc0"/>
  <text x="336" y="183" text-anchor="middle" fill="#fff" font-size="11">Edit form (vertical)</text>
  <rect x="296" y="198" width="58" height="16" rx="4" fill="#43a047"/>
  <text x="325" y="210" text-anchor="middle" fill="#fff" font-size="10">Cancel</text>
  <rect x="362" y="198" width="58" height="16" rx="4" fill="#1e88e5"/>
  <text x="391" y="210" text-anchor="middle" fill="#fff" font-size="10">Submit</text>
  <text x="340" y="232" text-anchor="middle" fill="currentColor" fill-opacity=".4" font-size="10">Nested horizontal stack</text>
  <rect x="497" y="20" width="246" height="220" rx="10" fill="#2a1f3d" stroke="currentColor" stroke-opacity=".25" stroke-width="1.5"/>
  <text x="620" y="44" text-anchor="middle" fill="#ce93d8" font-size="12" font-weight="bold">Multi-Panel Dashboard</text>
  <rect x="513" y="56" width="214" height="24" rx="6" fill="#8e24aa"/>
  <text x="620" y="73" text-anchor="middle" fill="#fff" font-size="11">Header</text>
  <rect x="513" y="90" width="100" height="96" rx="6" fill="#5c6bc0"/>
  <text x="563" y="143" text-anchor="middle" fill="#fff" font-size="11">Left</text>
  <rect x="623" y="90" width="104" height="96" rx="6" fill="#5c6bc0"/>
  <text x="675" y="143" text-anchor="middle" fill="#fff" font-size="11">Right</text>
  <rect x="513" y="196" width="214" height="24" rx="6" fill="#8e24aa" fill-opacity=".6"/>
  <text x="620" y="213" text-anchor="middle" fill="#fff" font-size="11">Footer</text>
  <text x="620" y="232" text-anchor="middle" fill="currentColor" fill-opacity=".4" font-size="10">Vertical outer + horizontal inner</text>
</svg>

*Three stack layouts: vertical (items stacked top-to-bottom), horizontal (items arranged left-to-right), and composed nesting for real-world patterns like forms with action bars and dashboards.*

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
