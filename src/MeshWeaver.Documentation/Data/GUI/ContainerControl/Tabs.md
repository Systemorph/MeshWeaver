---
Name: Organizing Content Into Switchable Tabs
Category: Documentation
Description: Organize content into switchable tabbed panels
Icon: /static/DocContent/GUI/ContainerControl/Tabs/icon.svg
---

The Tabs control organizes content into switchable panels. Each tab has a label and displays its content when selected.

# Basic Usage

```csharp --render TabsBasic --show-code
Controls.Tabs                                                       // Create a tabbed container
    .WithView(Controls.Label("General settings here"), s => s      // First tab content
        .WithLabel("General"))                                      // Tab label
    .WithView(Controls.Label("Advanced settings here"), s => s     // Second tab content
        .WithLabel("Advanced"))                                     // Tab label
    .WithView(Controls.Label("About information here"), s => s     // Third tab content
        .WithLabel("About"))                                        // Tab label
```

---

# Setting the Active Tab

Control which tab is selected programmatically using the auto-generated tab ID (1, 2, 3, etc.):

```csharp --render TabsActiveTab --show-code
Controls.Tabs                                                       // Create a tabbed container
    .WithSkin(skin => skin.WithActiveTabId("2"))                    // Start with second tab active
    .WithView(Controls.Label("Home content"), s => s                // First tab (ID: "1")
        .WithLabel("Home"))                                         // Tab label
    .WithView(Controls.Label("Settings content"), s => s            // Second tab (ID: "2") - active
        .WithLabel("Settings"))                                     // Tab label
```

---

# Tabs with Complex Content

Each tab can contain any control, including nested containers:

```csharp --render TabsComplex --show-code
var overviewContent = Controls.Stack                                // Build overview tab content
    .WithView(Controls.Html("<h3>Overview</h3>"))                   // Heading
    .WithView(Controls.Label("Dashboard content here"));            // Content

var settingsContent = Controls.Stack                                // Build settings tab content
    .WithView(Controls.Html("<h3>Settings</h3>"))                   // Heading
    .WithView(Controls.Label("Configuration options"));             // Content

Controls.Tabs                                                       // Create a tabbed container
    .WithView(overviewContent, s => s.WithLabel("Overview"))        // First tab
    .WithView(settingsContent, s => s.WithLabel("Settings"))        // Second tab
```

---

# Configuration Methods

| Method | Purpose | Example |
|--------|---------|---------|
| `.WithSkin(skin => skin.WithActiveTabId(id))` | Set initially active tab | `"home"`, `"settings"` |
| `.WithSkin(skin => skin.WithOrientation(o))` | Tab orientation | `Orientation.Horizontal` |
| `.WithSkin(skin => skin.WithHeight(h))` | Tab panel height | `"300px"` |

---

# Tab Skin Properties

Configure individual tabs via the skin function:

| Method | Purpose | Example |
|--------|---------|---------|
| `.WithLabel(label)` | Tab header text | `"General"`, `"Settings"` |

---

# See Also

- [Container Control](Doc/GUI/ContainerControl) - Overview of all containers
- [Stack Control](Doc/GUI/ContainerControl/Stack) - Vertical/horizontal layouts
