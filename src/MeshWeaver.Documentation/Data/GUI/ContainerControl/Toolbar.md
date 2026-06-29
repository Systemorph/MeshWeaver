---
Name: Organizing Buttons in a Toolbar
Category: Documentation
Description: Group action buttons in a horizontal or vertical strip for page headers, action bars, and form footers
Icon: /static/DocContent/GUI/ContainerControl/Toolbar/icon.svg
---

The **Toolbar** control arranges action buttons into a compact horizontal (default) or vertical strip. It is the standard building block for page headers, action bars, and form footers — anywhere you need a tight, consistent row of buttons without writing layout code by hand.
<svg viewBox="0 0 720 260" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <rect width="720" height="260" rx="12" fill="#1a1a2e" stroke="currentColor" stroke-opacity=".15" stroke-width="1"/>
  <text x="360" y="28" text-anchor="middle" fill="currentColor" fill-opacity=".55" font-size="12" font-weight="600" letter-spacing="1">TOOLBAR ORIENTATIONS</text>
  <rect x="30" y="44" width="300" height="100" rx="10" fill="#1e2a3a" stroke="#1e88e5" stroke-opacity=".6" stroke-width="1.5"/>
  <text x="180" y="64" text-anchor="middle" fill="#1e88e5" font-size="11" font-weight="700" letter-spacing="1">HORIZONTAL (default)</text>
  <rect x="46" y="76" width="68" height="32" rx="7" fill="#1e88e5"/>
  <text x="80" y="97" text-anchor="middle" fill="#fff" font-size="13" font-weight="600">New</text>
  <rect x="126" y="76" width="68" height="32" rx="7" fill="#1e88e5"/>
  <text x="160" y="97" text-anchor="middle" fill="#fff" font-size="13" font-weight="600">Edit</text>
  <rect x="206" y="76" width="68" height="32" rx="7" fill="#e53935" fill-opacity=".85"/>
  <text x="240" y="97" text-anchor="middle" fill="#fff" font-size="13" font-weight="600">Delete</text>
  <text x="180" y="130" text-anchor="middle" fill="currentColor" fill-opacity=".45" font-size="11">Orientation.Horizontal</text>
  <rect x="390" y="44" width="110" height="160" rx="10" fill="#1e2a3a" stroke="#43a047" stroke-opacity=".6" stroke-width="1.5"/>
  <text x="445" y="64" text-anchor="middle" fill="#43a047" font-size="11" font-weight="700" letter-spacing="1">VERTICAL</text>
  <rect x="406" y="74" width="78" height="32" rx="7" fill="#1e88e5"/>
  <text x="445" y="95" text-anchor="middle" fill="#fff" font-size="13" font-weight="600">New</text>
  <rect x="406" y="114" width="78" height="32" rx="7" fill="#1e88e5"/>
  <text x="445" y="135" text-anchor="middle" fill="#fff" font-size="13" font-weight="600">Edit</text>
  <rect x="406" y="154" width="78" height="32" rx="7" fill="#e53935" fill-opacity=".85"/>
  <text x="445" y="175" text-anchor="middle" fill="#fff" font-size="13" font-weight="600">Delete</text>
  <text x="445" y="214" text-anchor="middle" fill="currentColor" fill-opacity=".45" font-size="11">Orientation.Vertical</text>
  <text x="360" y="185" text-anchor="middle" fill="currentColor" fill-opacity=".45" font-size="12">Page Header · Action Bar · Form Footer</text>
  <line x1="30" y1="220" x2="690" y2="220" stroke="currentColor" stroke-opacity=".12" stroke-width="1"/>
  <text x="30" y="238" fill="#1e88e5" fill-opacity=".7" font-size="11" font-weight="600">Controls.Toolbar</text>
  <text x="140" y="238" fill="currentColor" fill-opacity=".5" font-size="11">.WithView(Controls.Button(…))  .WithView(…)</text>
  <text x="30" y="254" fill="currentColor" fill-opacity=".35" font-size="10">[optional]</text>
  <text x="70" y="254" fill="#43a047" fill-opacity=".7" font-size="10">.WithOrientation(Orientation.Vertical)</text>
</svg>

*Horizontal (default) and vertical toolbar layouts — both built from the same `Controls.Toolbar` API.*

---

# Basic Usage

The simplest toolbar wraps a few buttons and renders them side by side:

```csharp --render ToolbarBasic --show-code
Controls.Toolbar
    .WithView(Controls.Button("New"))
    .WithView(Controls.Button("Edit"))
    .WithView(Controls.Button("Delete"))
```

---

# Vertical Toolbar

Pass `Orientation.Vertical` to stack buttons top-to-bottom instead — useful for side-panels and narrow column layouts:

```csharp --render ToolbarVertical --show-code
Controls.Toolbar
    .WithOrientation(Orientation.Vertical)
    .WithView(Controls.Button("New"))
    .WithView(Controls.Button("Edit"))
    .WithView(Controls.Button("Delete"))
```

---

# Toolbar with Icons

Icon-only buttons keep toolbars compact. Combine an empty label with `WithIconStart` to replace text with a symbol:

```csharp --render ToolbarIcons --show-code
Controls.Toolbar
    .WithView(Controls.Button("").WithIconStart(FluentIcons.Add()))
    .WithView(Controls.Button("").WithIconStart(FluentIcons.Edit()))
    .WithView(Controls.Button("").WithIconStart(FluentIcons.Delete()))
```

---

# Common Patterns

## Page Header Toolbar

Place a toolbar on the right side of a page title by combining it with a horizontal `Stack` that distributes space between the two sides:

```csharp --render ToolbarPageHeader --show-code
Controls.Stack
    .WithOrientation(Orientation.Horizontal)
    .WithHorizontalAlignment("space-between")
    .WithView(Controls.Html("<h2>Users</h2>"))
    .WithView(
        Controls.Toolbar
            .WithView(Controls.Button("Add User"))
            .WithView(Controls.Button("Export"))
    )
```

## Form Action Bar

Anchor the Save / Cancel buttons to the bottom of a form by nesting a toolbar inside a vertical stack:

```csharp --render ToolbarFormActions --show-code
Controls.Stack
    .WithVerticalGap("16px")
    .WithView(Controls.Label("Form content goes here"))
    .WithView(
        Controls.Toolbar
            .WithView(Controls.Button("Cancel"))
            .WithView(Controls.Button("Save"))
    )
```

---

# Configuration Reference

| Method | Purpose | Values |
|---|---|---|
| `.WithOrientation(orientation)` | Control the button layout direction | `Orientation.Horizontal` (default), `Orientation.Vertical` |

---

# Skin Properties

`ToolbarSkin` exposes one layout property:

| Property | Type | Default |
|---|---|---|
| `Orientation` | `object?` | `Orientation.Horizontal` |

---

# See Also

- [Container Control](../../ContainerControl) — Overview of all container controls
- [Stack Control](../Stack) — General-purpose layout container for custom arrangements
