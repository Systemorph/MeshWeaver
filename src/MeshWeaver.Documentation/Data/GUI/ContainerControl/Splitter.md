---
Name: Creating Resizable Panels With Splitter
Category: Documentation
Description: Divide the UI into resizable, collapsible panes with a draggable divider
Icon: /static/DocContent/GUI/ContainerControl/Splitter/icon.svg
---

The `Splitter` control divides the UI into two or more panes separated by a draggable divider. Users can resize panes interactively, collapse them out of the way, or lock them to a fixed size — all through a clean fluent API.

---

# Basic Usage

The simplest splitter places two views side by side. Drag the divider to redistribute space between them.

```csharp --render SplitterBasic --show-code
Controls.Splitter                                           // Create a splitter
    .WithView(Controls.Label("Left Panel"))                 // First pane
    .WithView(Controls.Label("Right Panel"))                // Second pane
```

---

# Vertical Splitter

By default panes sit horizontally. Pass `Orientation.Vertical` to stack them top-to-bottom instead.

```csharp --render SplitterVertical --show-code
Controls.Splitter
    .WithSkin(s => s.WithOrientation(Orientation.Vertical)) // Stack panes vertically
    .WithView(Controls.Label("Top Panel"))
    .WithView(Controls.Label("Bottom Panel"))
```

---

# Setting Pane Sizes

Control the initial size of each pane with CSS units — pixels, percentages, or CSS `fr` fractions.

```csharp --render SplitterSizes --show-code
Controls.Splitter
    .WithView(Controls.Label("Sidebar"), s => s.WithSize("250px"))      // Fixed-width sidebar
    .WithView(Controls.Label("Main Content"), s => s.WithSize("1fr"))   // Flexible main area
```

> **Tip:** Mix `px`/`%` and `fr` units freely. A pane sized `1fr` takes whatever space remains after fixed-width panes are allocated.

---

# Size Constraints

Prevent a pane from becoming too narrow or too wide by combining `.WithMin()` and `.WithMax()`.

```csharp --render SplitterConstraints --show-code
Controls.Splitter
    .WithView(Controls.Label("Navigation"), s => s
        .WithSize("200px")                                              // Initial size
        .WithMin("100px")                                               // Can't shrink below 100px
        .WithMax("400px"))                                              // Can't grow beyond 400px
    .WithView(Controls.Label("Content"))
```

---

# Collapsible Panes

Mark a pane as collapsible and users get a toggle to hide it entirely — ideal for sidebars and panels that are useful but not always needed.

```csharp --render SplitterCollapsible --show-code
Controls.Splitter
    .WithView(Controls.Label("Sidebar"), s => s
        .WithSize("250px")
        .WithCollapsible(true))                                         // User can collapse this pane
    .WithView(Controls.Label("Main Content"))
```

---

# Initially Collapsed Pane

Sometimes a pane should start hidden and only be revealed on demand. Combine `.WithCollapsible(true)` with `.WithCollapsed(true)` to do this.

```csharp --render SplitterCollapsed --show-code
Controls.Splitter
    .WithView(Controls.Label("Panel Info"), s => s
        .WithCollapsible(true)                                          // Can be collapsed
        .WithCollapsed(true))                                           // Start collapsed
    .WithView(Controls.Label("Main Area"))
```

---

# Non-Resizable Pane

Lock a pane to a fixed size with `.WithResizable(false)`. The divider handle is hidden for that pane and the size never changes.

```csharp --render SplitterFixed --show-code
Controls.Splitter
    .WithView(Controls.Label("Fixed Header"), s => s
        .WithSize("60px")
        .WithResizable(false))                                          // Cannot be resized
    .WithView(Controls.Label("Scrollable Content"))
```

---

# Three-Pane Layout

Add as many panes as you need — each gets its own independent size and configuration.

```csharp --render SplitterThreePanes --show-code
Controls.Splitter
    .WithView(Controls.Label("Left"), s => s.WithSize("200px"))
    .WithView(Controls.Label("Center"), s => s.WithSize("1fr"))        // Flexible center
    .WithView(Controls.Label("Right"), s => s.WithSize("200px"))
```

---

# Common Patterns

## IDE-Style Layout

Nest a vertical splitter inside a horizontal one to build classic three-region IDE layouts — file explorer on the left, editor at the top, terminal at the bottom.

```csharp
Controls.Splitter                                                       // Outer horizontal splitter
    .WithView(Controls.Label("File Explorer"), s => s
        .WithSize("250px")
        .WithMin("150px")
        .WithCollapsible(true))
    .WithView(
        Controls.Splitter                                               // Inner vertical splitter
            .WithOrientation(Orientation.Vertical)
            .WithView(Controls.Label("Editor"), s => s.WithSize("70%"))
            .WithView(Controls.Label("Terminal"), s => s
                .WithSize("30%")
                .WithCollapsible(true)))
```

## Sidebar + Content

A collapsible navigation sidebar with bounded resize — a staple of application UIs.

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

# Configuration Reference

## Splitter Container

| Method | Purpose | Example values |
|---|---|---|
| `.WithOrientation(orientation)` | Pane arrangement direction | `Orientation.Horizontal`, `Orientation.Vertical` |
| `.WithWidth(width)` | Container width | `"100%"`, `"800px"` |
| `.WithHeight(height)` | Container height | `"100%"`, `"600px"` |

## Pane Configuration

| Method | Purpose | Example values |
|---|---|---|
| `.WithSize(size)` | Initial pane size | `"200px"`, `"30%"`, `"1fr"` |
| `.WithMin(min)` | Minimum size constraint | `"100px"`, `"20%"` |
| `.WithMax(max)` | Maximum size constraint | `"500px"`, `"80%"` |
| `.WithResizable(bool)` | Allow user resizing | `true`, `false` |
| `.WithCollapsible(bool)` | Allow user collapsing | `true`, `false` |
| `.WithCollapsed(bool)` | Start in collapsed state | `true`, `false` |

---

# Skin Properties

These properties are set internally when you call the fluent methods above. They are listed here for reference when working with the underlying skin objects directly.

## SplitterSkin

| Property | Type | Default |
|---|---|---|
| `Orientation` | `object?` | `Orientation.Horizontal` |
| `Width` | `object?` | null |
| `Height` | `object?` | null |

## SplitterPaneSkin

| Property | Type | Default |
|---|---|---|
| `Size` | `object?` | null |
| `Min` | `object?` | null |
| `Max` | `object?` | null |
| `Resizable` | `object?` | `true` |
| `Collapsible` | `object?` | null |
| `Collapsed` | `object?` | null |

---

# See Also

- [Container Control](../../ContainerControl) — Overview of all container controls
- [Stack Control](../Stack) — Simple vertical and horizontal stacking
- [Layout Grid](../../LayoutGrid) — Responsive grid-based layouts
