---
Name: Creating Resizable Panels With Splitter
Category: Documentation
Description: Divide the UI into resizable, collapsible panes
Icon: /static/storage/content/MeshWeaver/Documentation/GUI/ContainerControl/Splitter/icon.svg
---

# Creating Resizable Panels With Splitter

The Splitter control divides the UI into multiple resizable panes. Users can drag the divider between panes to resize them interactively.

## Basic Usage

```csharp --render SplitterBasic --show-code
Controls.Splitter                                           // Create a splitter
    .WithView(Controls.Label("Left Panel"))                 // First pane
    .WithView(Controls.Label("Right Panel"))                // Second pane
```

---

## Vertical Splitter

```csharp --render SplitterVertical --show-code
Controls.Splitter                                           // Create a splitter
    .WithOrientation(Orientation.Vertical)                  // Stack panes vertically
    .WithView(Controls.Label("Top Panel"))                  // Top pane
    .WithView(Controls.Label("Bottom Panel"))               // Bottom pane
```

---

## Setting Pane Sizes

Control the initial size of each pane:

```csharp --render SplitterSizes --show-code
Controls.Splitter                                                       // Create a splitter
    .WithView(Controls.Label("Sidebar"), s => s.WithSize("250px"))      // Fixed width sidebar
    .WithView(Controls.Label("Main Content"), s => s.WithSize("1fr"))   // Flexible main area
```

---

## Size Constraints

Limit how small or large a pane can be resized:

```csharp --render SplitterConstraints --show-code
Controls.Splitter                                                       // Create a splitter
    .WithView(Controls.Label("Navigation"), s => s
        .WithSize("200px")                                              // Initial size
        .WithMin("100px")                                               // Can't shrink below 100px
        .WithMax("400px"))                                              // Can't grow beyond 400px
    .WithView(Controls.Label("Content"))                                // Second pane
```

---

## Collapsible Panes

Allow users to collapse a pane completely:

```csharp --render SplitterCollapsible --show-code
Controls.Splitter                                                       // Create a splitter
    .WithView(Controls.Label("Sidebar"), s => s
        .WithSize("250px")                                              // Initial size
        .WithCollapsible(true))                                         // User can collapse this pane
    .WithView(Controls.Label("Main Content"))                           // Main content area
```

---

## Initially Collapsed Pane

Start with a pane already collapsed:

```csharp --render SplitterCollapsed --show-code
Controls.Splitter                                                       // Create a splitter
    .WithView(Controls.Label("Panel Info"), s => s
        .WithCollapsible(true)                                          // Can be collapsed
        .WithCollapsed(true))                                           // Start collapsed
    .WithView(Controls.Label("Main Area"))                              // Main content
```

---

## Non-Resizable Pane

Fix a pane's size so it cannot be resized:

```csharp --render SplitterFixed --show-code
Controls.Splitter                                                       // Create a splitter
    .WithView(Controls.Label("Fixed Header"), s => s
        .WithSize("60px")                                               // Fixed size
        .WithResizable(false))                                          // Cannot be resized
    .WithView(Controls.Label("Scrollable Content"))                     // Resizable content area
```

---

## Three-Pane Layout

Create layouts with multiple panes:

```csharp --render SplitterThreePanes --show-code
Controls.Splitter                                                       // Create a splitter
    .WithView(Controls.Label("Left"), s => s.WithSize("200px"))         // Left panel
    .WithView(Controls.Label("Center"), s => s.WithSize("1fr"))         // Center (flexible)
    .WithView(Controls.Label("Right"), s => s.WithSize("200px"))        // Right panel
```

---

## Common Patterns

### IDE-Style Layout

```csharp
Controls.Splitter                                                       // Outer horizontal splitter
    .WithView(Controls.Label("File Explorer"), s => s
        .WithSize("250px")
        .WithMin("150px")
        .WithCollapsible(true))                                         // Left sidebar
    .WithView(
        Controls.Splitter                                               // Inner vertical splitter
            .WithOrientation(Orientation.Vertical)
            .WithView(Controls.Label("Editor"), s => s.WithSize("70%")) // Main editor
            .WithView(Controls.Label("Terminal"), s => s
                .WithSize("30%")
                .WithCollapsible(true)))                                // Bottom panel
```

### Sidebar + Content

```csharp
Controls.Splitter
    .WithView(BuildNavigationMenu(), s => s
        .WithSize("240px")
        .WithMin("180px")
        .WithMax("400px")
        .WithCollapsible(true))
    .WithView(BuildMainContent())
```

---

## Configuration Methods

### Splitter Container

| Method | Purpose | Example |
|--------|---------|---------|
| `.WithOrientation(orientation)` | Pane arrangement | `Orientation.Horizontal`, `Orientation.Vertical` |
| `.WithWidth(width)` | Container width | `"100%"`, `"800px"` |
| `.WithHeight(height)` | Container height | `"100%"`, `"600px"` |

### Pane Configuration

| Method | Purpose | Example |
|--------|---------|---------|
| `.WithSize(size)` | Initial pane size | `"200px"`, `"30%"`, `"1fr"` |
| `.WithMin(min)` | Minimum size | `"100px"`, `"20%"` |
| `.WithMax(max)` | Maximum size | `"500px"`, `"80%"` |
| `.WithResizable(bool)` | Allow resizing | `true`, `false` |
| `.WithCollapsible(bool)` | Allow collapsing | `true`, `false` |
| `.WithCollapsed(bool)` | Start collapsed | `true`, `false` |

---

## Skin Properties

### SplitterSkin

| Property | Type | Default |
|----------|------|---------|
| `Orientation` | `object?` | `Orientation.Horizontal` |
| `Width` | `object?` | null |
| `Height` | `object?` | null |

### SplitterPaneSkin

| Property | Type | Default |
|----------|------|---------|
| `Size` | `object?` | null |
| `Min` | `object?` | null |
| `Max` | `object?` | null |
| `Resizable` | `object?` | `true` |
| `Collapsible` | `object?` | null |
| `Collapsed` | `object?` | null |

---

## See Also

- [Container Control](MeshWeaver/Documentation/UserInterface/ContainerControl) - Overview of all containers
- [Stack Control](MeshWeaver/Documentation/UserInterface/ContainerControl/Stack) - Simple vertical/horizontal layouts
- [Layout Grid](MeshWeaver/Documentation/UserInterface/LayoutGrid) - Responsive grid layouts

