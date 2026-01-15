---
Name: Stack Control
Category: Documentation
Description: Arrange controls vertically or horizontally with configurable spacing
Icon: /static/storage/content/MeshWeaver/GUI/Controls/Stack/icon.svg
---

The Stack control arranges child controls in a vertical or horizontal layout with configurable spacing and alignment.

## Source Files

| File | Purpose |
|------|---------|
| `src/MeshWeaver.Layout/StackControl.cs` | The control record and skin definition |

## Basic Usage

### Example 1: Vertical Stack (Default)

```csharp
Controls.Stack
    .WithView(Controls.Html("<h1>Title</h1>"))
    .WithView(Controls.Label("Description text"))
    .WithView(Controls.Button("Submit"))
```

**Result:** Three controls stacked vertically, top to bottom.

### Example 2: Horizontal Stack

```csharp
Controls.Stack
    .WithOrientation(Orientation.Horizontal)
    .WithHorizontalGap("8px")
    .WithView(Controls.Button("Save"))
    .WithView(Controls.Button("Cancel"))
```

**Result:** Two buttons side-by-side with 8 pixels between them.

### Example 3: Right-Aligned Button Group

```csharp
Controls.Stack
    .WithOrientation(Orientation.Horizontal)
    .WithHorizontalGap("8px")
    .WithHorizontalAlignment("end")
    .WithView(Controls.Button("Cancel"))
    .WithView(Controls.Button("Save"))
```

**Result:** Buttons aligned to the right of their container.

### Example 4: Form Layout

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

**Result:** A form editor followed by right-aligned action buttons.

## Configuration Methods

All methods return a new `StackControl` instance (immutable pattern). See `StackControl.cs:95-164` for extension methods.

| Method | Purpose | Example Values |
|--------|---------|----------------|
| `WithOrientation(orientation)` | Layout direction | `Orientation.Vertical`, `Orientation.Horizontal` |
| `WithVerticalGap(gap)` | Space between items (vertical) | `"8px"`, `"1rem"`, `"16px"` |
| `WithHorizontalGap(gap)` | Space between items (horizontal) | `"8px"`, `"1rem"`, `"16px"` |
| `WithHorizontalAlignment(align)` | Horizontal alignment | `"start"`, `"center"`, `"end"` |
| `WithVerticalAlignment(align)` | Vertical alignment | `"start"`, `"center"`, `"end"` |
| `WithWidth(width)` | Stack width | `"300px"`, `"100%"` |
| `WithHeight(height)` | Stack height | `"200px"`, `"100%"` |
| `WithWrap(wrap)` | Enable wrapping | `true`, `false` |

## Adding Child Controls

Use `WithView` to add controls (inherited from `ContainerControl`):

```csharp
// Static control
.WithView(Controls.Label("Text"))

// Named area
.WithView(Controls.Button("Click"), "buttonArea")

// Dynamic from observable
.WithView(dataStream.Select(d => Controls.Label(d.Name)))

// Function with context
.WithView((host, ctx) => Controls.Label($"Area: {ctx.Area}"))
```

## Nesting Stacks

Stacks can contain other stacks for complex layouts:

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

## LayoutStackSkin Properties

The skin (StackControl.cs:21-93) defines visual properties:

| Property | Type | Default |
|----------|------|---------|
| `Orientation` | `object?` | `Orientation.Vertical` |
| `HorizontalAlignment` | `object?` | null |
| `VerticalAlignment` | `object?` | null |
| `HorizontalGap` | `object?` | null |
| `VerticalGap` | `object?` | null |
| `Wrap` | `object?` | null |
| `Width` | `object?` | null |
| `Height` | `object?` | null |

## See Also

- [WithView Patterns](MeshWeaver/GUI/Concepts/WithView) - Understanding view patterns
- [Editor Control](MeshWeaver/GUI/Controls/Editor) - Form generation
- [DataGrid Control](MeshWeaver/GUI/Controls/DataGrid) - Tabular data display
