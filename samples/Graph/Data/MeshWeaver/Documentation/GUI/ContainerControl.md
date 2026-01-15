---
Name: Container Control
Category: Documentation
Description: How to add controls to containers and make them update automatically
Icon: /static/storage/content/MeshWeaver/Documentation/GUI/ContainerControl/icon.svg
---

When building a UI, you combine small pieces (buttons, labels, inputs) into larger structures called containers. The `WithView` method is how you add content to these containers.

## Container Types

| Container | Purpose | Use when... |
|-----------|---------|-------------|
| `Controls.Stack` | Arrange items vertically or horizontally | Building forms, lists, button groups |
| `Controls.Tabs` | Organize content into switchable tabs | Grouping related content, settings pages |
| `Controls.LayoutGrid` | Arrange items in a responsive grid | Dashboards, card layouts, responsive designs |
| `Controls.Toolbar` | Group action buttons | Page headers, action bars |

---

## Stack

Arrange controls vertically (default) or horizontally.

```csharp --render StackExample --show-code
Controls.Stack                                  // Create a vertical container
    .WithView(Controls.Label("Welcome"))        // First item at top
    .WithView(Controls.Button("Get Started"))   // Second item below
    .WithView(Controls.Button("Learn More"))    // Third item at bottom
```

Horizontal layout with spacing:

```csharp --render HorizontalStack --show-code
Controls.Stack                                      // Create a container
    .WithOrientation(Orientation.Horizontal)        // Arrange items side by side
    .WithHorizontalGap("8px")                       // Add 8 pixels between items
    .WithView(Controls.Button("Save"))              // Left button
    .WithView(Controls.Button("Cancel"))            // Right button
```

---

## Tabs

Organize content into switchable panels. Each tab needs a label.

```csharp --render TabsExample --show-code
Controls.Tabs                                                       // Create a tabbed container
    .WithView(Controls.Label("General settings here"), s => s      // First tab content
        .WithLabel("General"))                                      // Tab label
    .WithView(Controls.Label("Advanced settings here"), s => s     // Second tab content
        .WithLabel("Advanced"))                                     // Tab label
    .WithView(Controls.Label("About information here"), s => s     // Third tab content
        .WithLabel("About"))                                        // Tab label
```

Set the active tab programmatically:

```csharp
Controls.Tabs                                                       // Create a tabbed container
    .WithSkin(skin => skin.WithActiveTabId("settings"))             // Start with "settings" tab active
    .WithView(Controls.Label("Home content"), "home", s => s        // First tab with ID "home"
        .WithLabel("Home"))                                         // Tab label
    .WithView(Controls.Label("Settings content"), "settings", s => s // Second tab with ID "settings"
        .WithLabel("Settings"))                                     // Tab label (this tab starts active)
```

---

## Layout Grid

Arrange items in a responsive grid that adapts to screen size.

```csharp --render GridExample --show-code
Controls.LayoutGrid                                 // Create a grid container
    .WithSkin(skin => skin.WithSpacing(3))          // Add spacing between items
    .WithView(Controls.Html("<div style='background:#e3f2fd;padding:16px'>Card 1</div>"), s => s  // First card
        .WithMd(6))                                 // Takes half width on medium screens
    .WithView(Controls.Html("<div style='background:#e3f2fd;padding:16px'>Card 2</div>"), s => s  // Second card
        .WithMd(6))                                 // Takes half width on medium screens
```

Responsive columns (12-column grid system):

```csharp --render GridResponsive --show-code
Controls.LayoutGrid                                 // Create a grid container
    .WithSkin(skin => skin.WithSpacing(2))          // Add spacing via skin configuration
    .WithView(Controls.Html("<div style='background:#c8e6c9;padding:12px'>Full on mobile, 1/3 on desktop</div>"), s => s
        .WithXs(12).WithMd(4))                      // Full width on mobile, 1/3 on medium+
    .WithView(Controls.Html("<div style='background:#c8e6c9;padding:12px'>Full on mobile, 1/3 on desktop</div>"), s => s
        .WithXs(12).WithMd(4))                      // Full width on mobile, 1/3 on medium+
    .WithView(Controls.Html("<div style='background:#c8e6c9;padding:12px'>Full on mobile, 1/3 on desktop</div>"), s => s
        .WithXs(12).WithMd(4))                      // Full width on mobile, 1/3 on medium+
```

---

## Toolbar

Group action buttons horizontally (default) or vertically.

```csharp --render ToolbarExample --show-code
Controls.Toolbar                            // Create a toolbar
    .WithView(Controls.Button("New"))       // First action
    .WithView(Controls.Button("Edit"))      // Second action
    .WithView(Controls.Button("Delete"))    // Third action
```

---

## Static vs Dynamic Content

Some content never changes (like a page title). Other content needs to update when data changes.

**Static content** - rendered once, never updates:

```csharp --render StaticExample --show-code
Controls.Stack                                              // Create a container
    .WithView(Controls.Html("<h2>Dashboard</h2>"))          // Static heading
    .WithView(Controls.Label("This text never changes"))   // Static label
```

**Dynamic content** - updates automatically when data changes:

```csharp --render DynamicExample --show-code
var counter = Observable.Interval(TimeSpan.FromSeconds(1));  // Stream that emits every second

Controls.Stack                                                   // Create a container
    .WithView(Controls.Label("Page Title"))                      // Static - never changes
    .WithView(counter.Select(n => Controls.Label($"Count: {n}"))) // Dynamic - updates each second
```

The key difference: static content is a control, dynamic content is a stream that produces controls.

---

## When to Use Each Pattern

| I want to... | Use this |
|--------------|----------|
| Show fixed text, icons, or buttons | Pass a control directly |
| Show content that updates when data changes | Pass an observable stream |
| Load data before showing content | Pass an async function |
| Access the current data store | Pass a function with store parameter |

---

## Common Examples

### Dashboard with Tabs

```csharp --render DashboardTabs --show-code
Controls.Tabs                                                               // Tabbed dashboard
    .WithView(                                                              // Overview tab
        Controls.Stack                                                      // Stack inside tab
            .WithView(Controls.Html("<h3>Overview</h3>"))                   // Tab heading
            .WithView(Controls.Label("Welcome to the dashboard")),          // Tab content
        s => s.WithLabel("Overview"))                                       // Tab label
    .WithView(Controls.Label("Detailed analytics here"), s => s            // Analytics tab
        .WithLabel("Analytics"))                                            // Tab label
```

### Real-time Clock

```csharp --render Clock --show-code
var tick = Observable.Interval(TimeSpan.FromSeconds(1));  // Stream that emits every second

Controls.Stack                                                                  // Container
    .WithView(tick.Select(_ => Controls.Label(DateTime.Now.ToString("HH:mm:ss"))))  // Updates every second
```

### Loading Data First

When you need to fetch data before showing content:

```csharp
Controls.Stack                                      // Create a container
    .WithView(async (host, ctx, ct) => {            // Async function - runs once when rendering
        var user = await LoadUserAsync(ct);         // Fetch user data from server
        return Controls.Label($"Hello, {user.Name}"); // Return control after data loaded
    })
```

### Reacting to Data Changes

When content depends on data in the store:

```csharp
Controls.Stack                                          // Create a container
    .WithView((host, ctx, store) =>                     // Function with access to data store
        host.GetDataStream<User>("currentUser")         // Get stream of user updates
            .Select(user => Controls.Label($"Logged in as {user.Name}")))  // Update when user changes
```

---

## See Also

- [Static vs Dynamic Views](MeshWeaver/Documentation/GUI/Observables) - More on when content updates
- [Data Binding](MeshWeaver/Documentation/GUI/DataBinding) - How data flows through the UI
- [Stack Control](MeshWeaver/Documentation/GUI/Stack) - Detailed Stack documentation
