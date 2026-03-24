---
Name: Adding Controls to a UI
Category: Documentation
Description: How to add controls to containers and make them update automatically
Icon: /static/DocContent/GUI/ContainerControl/icon.svg
---

When building a UI, you combine small pieces (buttons, labels, inputs) into larger structures called containers. The `WithView` method is how you add content to these containers.

# Container Types

| Container | Purpose | Use when... |
|-----------|---------|-------------|
| [Stack](Stack) | Arrange items vertically or horizontally | Building forms, lists, button groups |
| [Tabs](Tabs) | Organize content into switchable tabs | Grouping related content, settings pages |
| [Toolbar](Toolbar) | Group action buttons | Page headers, action bars |
| [Splitter](Splitter) | Create resizable, collapsible panes | Sidebars, IDE layouts, adjustable panels |

For responsive grid layouts, see [Layout Grid](../LayoutGrid).

---

# Adding Content with WithView

Use `WithView` to add controls to any container:

```csharp --render BasicWithView --show-code
Controls.Stack                                              // Create a container
    .WithView(Controls.Html("<h2>Dashboard</h2>"))          // Add a heading
    .WithView(Controls.Label("Welcome to the app"))         // Add a label
    .WithView(Controls.Button("Get Started"))               // Add a button
```

---

# WithView Patterns

| I want to... | Use this |
|--------------|----------|
| Show fixed text, icons, or buttons | Pass a control directly |
| Show content that updates when data changes | Pass an observable stream |
| Load data before showing content | Pass an async function |
| Access the current data store | Pass a function with store parameter |

See [Static vs Dynamic Views](../Observables) for details on reactive patterns.

---

# Example: Dashboard with Tabs

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

---

# See Also

- [Static vs Dynamic Views](../Observables) - When and how content updates
- [Data Binding](../DataBinding) - How data flows through the UI
