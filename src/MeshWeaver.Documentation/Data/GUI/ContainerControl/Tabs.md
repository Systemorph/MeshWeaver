---
Name: Organizing Content Into Switchable Tabs
Category: Documentation
Description: Organize content into switchable tabbed panels that keep your UI clean and navigable
Icon: /static/DocContent/GUI/ContainerControl/Tabs/icon.svg
---

The **Tabs** control groups related content into labelled panels that the user can switch between. Rather than stacking everything vertically, tabs let you present multiple sections in the same screen area — keeping layouts compact and navigation intuitive.

---

## Basic Usage

The simplest form adds a series of views, each with a `.WithLabel()` to supply the tab header text. Tabs are shown in order, and the first tab is active by default.

```csharp --render TabsBasic --show-code
Controls.Tabs
    .WithView(Controls.Label("General settings here"), s => s
        .WithLabel("General"))
    .WithView(Controls.Label("Advanced settings here"), s => s
        .WithLabel("Advanced"))
    .WithView(Controls.Label("About information here"), s => s
        .WithLabel("About"))
```

---

## Setting the Active Tab

To open on a specific tab rather than the first, pass its auto-generated ID (assigned sequentially as `"1"`, `"2"`, `"3"`, …) to `WithActiveTabId`. This is useful when a page link should land the user directly on a relevant section.

```csharp --render TabsActiveTab --show-code
Controls.Tabs
    .WithSkin(skin => skin.WithActiveTabId("2"))        // Open on the second tab
    .WithView(Controls.Label("Home content"), s => s
        .WithLabel("Home"))
    .WithView(Controls.Label("Settings content"), s => s
        .WithLabel("Settings"))
```

---

## Tabs with Complex Content

Each tab accepts any control as its view — including nested stacks, data grids, or other containers. Build the content separately for clarity, then pass it to `.WithView()`.

```csharp --render TabsComplex --show-code
var overviewContent = Controls.Stack
    .WithView(Controls.Html("<h3>Overview</h3>"))
    .WithView(Controls.Label("Dashboard content here"));

var settingsContent = Controls.Stack
    .WithView(Controls.Html("<h3>Settings</h3>"))
    .WithView(Controls.Label("Configuration options"));

Controls.Tabs
    .WithView(overviewContent, s => s.WithLabel("Overview"))
    .WithView(settingsContent, s => s.WithLabel("Settings"))
```

---

## Configuration Reference

### Container-level options (`WithSkin`)

These are applied once on the `Controls.Tabs` itself and affect the whole tab strip.

| Method | Purpose | Example value |
|--------|---------|---------------|
| `.WithSkin(skin => skin.WithActiveTabId(id))` | Tab to open on load | `"1"`, `"2"` |
| `.WithSkin(skin => skin.WithOrientation(o))` | Tab strip orientation | `Orientation.Horizontal` |
| `.WithSkin(skin => skin.WithHeight(h))` | Height of the tab panel | `"300px"` |

### Per-tab options (second argument of `WithView`)

These are passed as the skin function in the second argument to `.WithView()`.

| Method | Purpose | Example value |
|--------|---------|---------------|
| `.WithLabel(label)` | Tab header text | `"General"`, `"Settings"` |

---

## See Also

- [Container Control](../../ContainerControl) — Overview of all container controls
- [Stack Control](../Stack) — Vertical and horizontal layout stacks
