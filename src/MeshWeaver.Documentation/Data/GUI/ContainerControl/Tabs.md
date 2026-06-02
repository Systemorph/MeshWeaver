---
Name: Organizing Content Into Switchable Tabs
Category: Documentation
Description: Organize content into switchable tabbed panels that keep your UI clean and navigable
Icon: /static/DocContent/GUI/ContainerControl/Tabs/icon.svg
---

The **Tabs** control groups related content into labelled panels that the user can switch between. Rather than stacking everything vertically, tabs let you present multiple sections in the same screen area — keeping layouts compact and navigation intuitive.
<svg viewBox="0 0 760 260" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="tab-arrow" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="#90a4ae"/>
    </marker>
  </defs>
  <rect x="0" y="0" width="760" height="260" rx="12" fill="#1a2030" opacity="0.6"/>
  <text x="380" y="28" text-anchor="middle" fill="currentColor" fill-opacity="0.5" font-size="12" font-style="italic">Controls.Tabs — one container, multiple switchable panels</text>
  <rect x="40" y="44" width="680" height="196" rx="10" fill="#1e2a3a" stroke="#5c6bc0" stroke-width="1.5"/>
  <rect x="40" y="44" width="140" height="38" rx="8" fill="#1e88e5"/>
  <text x="110" y="68" text-anchor="middle" fill="#fff" font-weight="bold" font-size="13">General</text>
  <rect x="184" y="44" width="130" height="38" rx="8" fill="#1e2a3a" stroke="#5c6bc0" stroke-width="1"/>
  <text x="249" y="68" text-anchor="middle" fill="currentColor" fill-opacity="0.6" font-size="13">Advanced</text>
  <rect x="318" y="44" width="110" height="38" rx="8" fill="#1e2a3a" stroke="#5c6bc0" stroke-width="1"/>
  <text x="373" y="68" text-anchor="middle" fill="currentColor" fill-opacity="0.6" font-size="13">About</text>
  <line x1="40" y1="82" x2="720" y2="82" stroke="#5c6bc0" stroke-width="1.5"/>
  <rect x="56" y="96" width="648" height="128" rx="8" fill="#111827" stroke="#1e88e5" stroke-width="1"/>
  <text x="380" y="126" text-anchor="middle" fill="currentColor" fill-opacity="0.4" font-size="11">Active panel content (General)</text>
  <rect x="100" y="140" width="160" height="34" rx="6" fill="#1e2a3a" stroke="#1e88e5" stroke-width="1"/>
  <text x="180" y="162" text-anchor="middle" fill="#b0bec5" font-size="11">Text field: Name</text>
  <rect x="280" y="140" width="160" height="34" rx="6" fill="#1e2a3a" stroke="#1e88e5" stroke-width="1"/>
  <text x="360" y="162" text-anchor="middle" fill="#b0bec5" font-size="11">Dropdown: Region</text>
  <rect x="460" y="140" width="160" height="34" rx="6" fill="#1e2a3a" stroke="#1e88e5" stroke-width="1"/>
  <text x="540" y="162" text-anchor="middle" fill="#b0bec5" font-size="11">Toggle: Active</text>
  <text x="56" y="216" fill="currentColor" fill-opacity="0.35" font-size="11" font-style="italic">Advanced and About panels hidden — rendered only when their tab is activated</text>
</svg>

*A Tabs container with three labeled tabs; the active tab (General) displays its panel while the others remain hidden.*

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
