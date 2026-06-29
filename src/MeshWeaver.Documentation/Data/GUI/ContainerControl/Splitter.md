---
Name: Creating Resizable Panels With Splitter
Category: Documentation
Description: Divide the UI into resizable, collapsible panes with a draggable divider
Icon: /static/DocContent/GUI/ContainerControl/Splitter/icon.svg
---

The `Splitter` control divides the UI into two or more panes separated by a draggable divider. Users can resize panes interactively, collapse them out of the way, or lock them to a fixed size — all through a clean fluent API.
<svg viewBox="0 0 760 220" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity="0.55"/>
    </marker>
  </defs>
  <rect x="0" y="0" width="760" height="220" rx="10" fill="none" stroke="currentColor" stroke-opacity="0.15" stroke-width="1"/>
  <text x="380" y="22" text-anchor="middle" fill="currentColor" fill-opacity="0.5" font-size="11" font-weight="600" letter-spacing="1">SPLITTER — HORIZONTAL (outer)</text>
  <rect x="20" y="34" width="160" height="166" rx="8" fill="#1e88e5"/>
  <text x="100" y="112" text-anchor="middle" fill="#fff" font-weight="600">File Explorer</text>
  <text x="100" y="130" text-anchor="middle" fill="#fff" font-size="11" fill-opacity="0.8">250px · collapsible</text>
  <line x1="185" y1="34" x2="185" y2="200" stroke="currentColor" stroke-opacity="0.45" stroke-width="3" stroke-dasharray="4,3"/>
  <rect x="186" y="92" width="12" height="36" rx="3" fill="currentColor" fill-opacity="0.22"/>
  <rect x="200" y="34" width="540" height="166" rx="8" fill="none" stroke="currentColor" stroke-opacity="0.18" stroke-width="1.5"/>
  <text x="470" y="22" text-anchor="middle" fill="currentColor" fill-opacity="0" font-size="1"/>
  <text x="340" y="52" text-anchor="middle" fill="currentColor" fill-opacity="0.45" font-size="11" font-weight="600" letter-spacing="1">SPLITTER — VERTICAL (inner)</text>
  <rect x="208" y="60" width="524" height="82" rx="8" fill="#43a047"/>
  <text x="470" y="96" text-anchor="middle" fill="#fff" font-weight="600">Editor</text>
  <text x="470" y="114" text-anchor="middle" fill="#fff" font-size="11" fill-opacity="0.8">70% · resizable</text>
  <line x1="208" y1="147" x2="732" y2="147" stroke="currentColor" stroke-opacity="0.45" stroke-width="3" stroke-dasharray="4,3"/>
  <rect x="446" y="142" width="48" height="12" rx="3" fill="currentColor" fill-opacity="0.22"/>
  <rect x="208" y="153" width="524" height="40" rx="8" fill="#5c6bc0"/>
  <text x="470" y="178" text-anchor="middle" fill="#fff" font-weight="600">Terminal</text>
  <text x="470" y="192" text-anchor="middle" fill="#fff" font-size="10" fill-opacity="0.8">30% · collapsible</text>
</svg>

*Nested splitters: a horizontal outer splitter with a file explorer pane and a vertical inner splitter holding editor and terminal.*

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

# Scrolling Panes

A common goal is a splitter whose panes each scroll **independently** — a side menu that scrolls on its own while the content area scrolls on its own — with the splitter filling the page rather than growing past it.

The instinct is to set `height: 100%` on the pane content. **Don't.** A splitter pane gets its height from flex *stretch*, and a flex-stretched height is *indefinite* for the purpose of resolving a child's percentage height. So `height: 100%` on the content collapses to its natural (content) height, and the pane's `overflow: hidden` then clips it — the content is cut off with nothing to scroll.

Instead, make the pane content fill via the flex chain and scroll within it:

```css
/* The splitter fills its (definite-height, flex-column) parent. */
.my-splitter { flex: 1 1 auto; min-height: 0; }

/* Each pane becomes a definite-height flex column. */
.my-splitter .fluent-multi-splitter-pane {
    display: flex; flex-direction: column; min-height: 0; overflow: hidden;
}

/* The pane's content FILLS via flex (not height:100%) and scrolls within. */
.my-splitter .fluent-multi-splitter-pane > div {
    flex: 1 1 auto; min-height: 0; overflow-y: auto;
}
```

The rule of thumb: in a flex column, use `flex: 1 1 auto; min-height: 0` to fill, never `height: 100%`. The framework's Settings and node-type shell pages use exactly this pattern — see [The Node Settings Page](/Doc/GUI/SettingsPage).

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
- [The Node Settings Page](/Doc/GUI/SettingsPage) — a real two-pane, independently-scrolling splitter with a collapsible menu
