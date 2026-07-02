---
Name: Adding Controls to a UI
Category: Documentation
Description: How to compose containers from controls and wire them to live data streams
Icon: /static/DocContent/GUI/ContainerControl/icon.svg
---

Every MeshWeaver screen is built by combining small, self-contained controls — buttons, labels, inputs — into larger **containers**. Containers manage layout, coordinate named areas, and decide when their children re-render. `WithView` is the single method that connects a control (or a live data stream) to a slot inside a container.

# Container Types

Five containers cover the majority of layout needs:

| Container | What it does | Reach for it when... |
|-----------|-------------|----------------------|
| [Stack](Stack) | Arranges children vertically or horizontally | Building forms, lists, button groups |
| [Tabs](Tabs) | Displays one child panel at a time behind tab buttons | Grouping related content, settings pages |
| [Toolbar](Toolbar) | Lays out action buttons in a bar | Page headers, action bars |
| [Splitter](Splitter) | Creates resizable, collapsible panes | Sidebars, IDE-style layouts |
| [Layout](Layout) | Assigns children header / body / footer roles | Whole-page scaffolds with persistent chrome |

For responsive multi-column grid layouts, see [Layout Grid](../LayoutGrid).

---

# Adding Content with WithView

`WithView` is the universal entry point for placing content inside any container. Pass a control directly for static content:

```csharp --render BasicWithView --show-code
Controls.Stack                                              // Create a container
    .WithView(Controls.Html("<h2>Dashboard</h2>"))          // Add a heading
    .WithView(Controls.Label("Welcome to the app"))         // Add a label
    .WithView(Controls.Button("Get Started"))               // Add a button
```

Every call to `WithView` appends a new named area to the container. The framework allocates area identifiers automatically (`"1"`, `"2"`, …) unless you supply your own via the `options` overload or the `string area` overload.

---

# WithView Overloads at a Glance

`WithView` is heavily overloaded so the same method name covers every rendering pattern:

| I want to… | Overload to use |
|------------|-----------------|
| Show fixed text, icons, or buttons | `WithView(UiControl?)` — pass a control directly |
| Show content that re-renders when data changes | `WithView(IObservable<UiControl?>)` — pass a reactive stream |
| Load data asynchronously before rendering | `WithView(async (host, ctx, ct) => …)` — pass an async function |
| Access the current data store at render time | `WithView((host, ctx, store) => …)` — pass a store function |
| Pin content to a specific named slot | `WithView(control, "slotName")` — pass an area string |
| Customise the slot with skins or options | `WithView(control, area => area.WithLabel("…"))` — pass an options lambda |

See [Static vs Dynamic Views](../Observables) for a deep dive into reactive patterns.

---

# Example: Tabbed Dashboard

Containers nest freely. A `Stack` inside a `Tabs` panel is a common pattern for adding a heading and body to each tab:

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

The second argument to `WithView` — `s => s.WithLabel("Overview")` — configures the `NamedAreaControl` that represents the slot. Each container type exposes its own skin properties through this lambda (e.g. `WithLabel` on tabs, `WithIcon` on toolbar items).

---

# How Areas Work

Internally, every `WithView` call adds a `NamedAreaControl` to the container's `Areas` collection and registers a renderer. During the render pass, each renderer writes its output into the entity store under the area's path (`{parentArea}/{areaId}`). Only the leaf areas that actually changed are patched in the client.

This means:
- **Adding a new `WithView`** never forces a re-render of existing siblings.
- **Passing an observable** subscribes it with `DistinctUntilChanged()` — duplicate emissions are suppressed automatically.
- **Containers are immutable records** — every `WithView` call returns a new container instance, so snapshots are safe to cache.

---

# See Also

- [Static vs Dynamic Views](../Observables) — When and how content updates
- [Data Binding](../DataBinding) — How data flows through the UI
- [Stack](Stack) — Vertical and horizontal arrangement
- [Tabs](Tabs) — Tabbed navigation
- [Toolbar](Toolbar) — Action button bars
- [Splitter](Splitter) — Resizable pane layouts
