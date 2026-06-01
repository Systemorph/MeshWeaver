---
Name: Organizing Buttons in a Toolbar
Category: Documentation
Description: Group action buttons in a horizontal or vertical strip for page headers, action bars, and form footers
Icon: /static/DocContent/GUI/ContainerControl/Toolbar/icon.svg
---

The **Toolbar** control arranges action buttons into a compact horizontal (default) or vertical strip. It is the standard building block for page headers, action bars, and form footers — anywhere you need a tight, consistent row of buttons without writing layout code by hand.

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
