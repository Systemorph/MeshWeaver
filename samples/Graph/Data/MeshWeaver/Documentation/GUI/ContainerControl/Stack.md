---
Name: Arranging UI Controls in a Stack
Category: Documentation
Description: Arrange controls vertically or horizontally with configurable spacing
Icon: /static/storage/content/MeshWeaver/Documentation/GUI/ContainerControl/Stack/icon.svg
---

# Arranging UI Controls in a Stack

The Stack control arranges child controls in a vertical or horizontal layout with configurable spacing and alignment.

## Basic Usage

### Vertical Stack (Default)

```csharp --render StackVertical --show-code
Controls.Stack                                  // Create a vertical container
    .WithView(Controls.Label("Welcome"))        // First item at top
    .WithView(Controls.Button("Get Started"))   // Second item below
    .WithView(Controls.Button("Learn More"))    // Third item at bottom
```

---

### Horizontal Stack

```csharp --render StackHorizontal --show-code
Controls.Stack                                      // Create a container
    .WithOrientation(Orientation.Horizontal)        // Arrange items side by side
    .WithHorizontalGap("8px")                       // Add 8 pixels between items
    .WithView(Controls.Button("Save"))              // Left button
    .WithView(Controls.Button("Cancel"))            // Right button
```

---

### Right-Aligned Button Group

```csharp
Controls.Stack                                      // Create a container
    .WithOrientation(Orientation.Horizontal)        // Arrange items side by side
    .WithHorizontalGap("8px")                       // Add spacing between buttons
    .WithHorizontalAlignment("end")                 // Align to the right
    .WithView(Controls.Button("Cancel"))            // Left button
    .WithView(Controls.Button("Save"))              // Right button
```

### Form Layout

```csharp
Controls.Stack                                      // Outer vertical stack
    .WithVerticalGap("16px")                        // Space between form and buttons
    .WithView(host.Edit(new UserData()))            // Form editor
    .WithView(
        Controls.Stack                              // Inner horizontal stack for buttons
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap("8px")
            .WithHorizontalAlignment("end")
            .WithView(Controls.Button("Cancel"))
            .WithView(Controls.Button("Submit"))
    )
```

---

## Configuration Methods

All methods return a new `StackControl` instance (immutable pattern).

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

---

## Adding Child Controls

Use `WithView` to add controls:

```csharp
// Static control
.WithView(Controls.Label("Text"))                               // Add a label

// Named area
.WithView(Controls.Button("Click"), "buttonArea")               // Add with area name

// Dynamic from observable
.WithView(dataStream.Select(d => Controls.Label(d.Name)))       // Updates when data changes

// Function with context
.WithView((host, ctx) => Controls.Label($"Area: {ctx.Area}"))   // Access render context
```

---

## Nesting Stacks

Stacks can contain other stacks for complex layouts:

```csharp
Controls.Stack                                      // Outer vertical stack
    .WithView(Controls.Html("<h1>Dashboard</h1>"))  // Header
    .WithView(
        Controls.Stack                              // Inner horizontal stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap("16px")
            .WithView(BuildLeftPanel())             // Left panel
            .WithView(BuildRightPanel())            // Right panel
    )
    .WithView(Controls.Html("<footer>Footer</footer>"))  // Footer
```

---

## Skin Properties

The `LayoutStackSkin` defines visual properties:

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

---

## See Also

- [Container Control](MeshWeaver/Documentation/GUI/ContainerControl) - Overview of all containers
- [Editor Control](MeshWeaver/Documentation/GUI/Editor) - Form generation
- [DataGrid Control](MeshWeaver/Documentation/GUI/DataGrid) - Tabular data display
