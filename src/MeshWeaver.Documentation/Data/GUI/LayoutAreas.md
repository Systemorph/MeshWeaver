---
Name: Layout Areas
Category: Documentation
Description: Named rendering slots on hubs that define what UI each area displays
Icon: /static/NodeTypeIcons/code.svg
---

Layout areas are **named rendering slots** registered on a message hub. Each area has a name and a view function that produces UI controls. When a client requests a specific area, the hub runs the corresponding view function and streams the result.

# How Layout Areas Work

Register layout areas using the `AddLayout()` pipeline on `MessageHubConfiguration`:

```csharp
public static MessageHubConfiguration AddCodeViews(this MessageHubConfiguration configuration)
    => configuration.AddLayout(layout => layout
        .WithDefaultArea("Content")       // Which area renders when none is specified
        .WithView("Content", Content)     // Simple read-only view
        .WithView("Overview", Overview)   // Splitter with navigation
        .WithView("Edit", Edit));         // Editor view
```

## Key Methods

| Method | Purpose |
|--------|---------|
| `WithDefaultArea(name)` | Sets which area renders when the caller specifies an empty area name |
| `WithView(name, function)` | Registers a view function for the named area |

## View Function Signatures

A view function takes a `LayoutAreaHost` and `RenderingContext` and returns either a static control or a reactive observable:

```csharp
// Static view — renders once
public static UiControl Edit(LayoutAreaHost host, RenderingContext ctx)
{
    return Controls.Stack.WithView(Controls.H1("Editor"));
}

// Reactive view — updates when data changes
public static IObservable<UiControl?> Content(LayoutAreaHost host, RenderingContext ctx)
{
    return host.Workspace.GetStream<MeshNode>()
        .Select(nodes => (UiControl?)BuildContent(nodes));
}
```

## Navigation Between Areas

Use `LayoutAreaReference` and `ToHref()` to build navigation links between areas:

```csharp
// Navigate to the Edit area of the current hub
var editHref = new LayoutAreaReference("Edit").ToHref(hubAddress);
Controls.Button("Edit").WithNavigateToHref(editHref);

// Navigate to a specific area on a different node
var overviewHref = new LayoutAreaReference("Overview").ToHref(otherNodePath);
new NavLinkControl("View Code", FluentIcons.Code(), overviewHref);
```

# LayoutAreaControl: Embedding One Hub's Area Inside Another

`LayoutAreaControl` embeds a layout area from one hub inside another hub's view. This is how you compose views across node boundaries.

```csharp
// Embed the default area of a target hub
new LayoutAreaControl(targetAddress, new LayoutAreaReference(""))

// Embed a specific area
new LayoutAreaControl(targetAddress, new LayoutAreaReference("Thumbnail"))
```

## How the Default Area Resolves

When the `LayoutAreaReference` has an empty area name, the target hub's **default area** is rendered (the one set by `WithDefaultArea`). This is essential for composition without recursion.

**Example: Code node Overview Splitter**

The Code node registers `Content` as its default area (simple markdown code block) and `Overview` as a Splitter. The Splitter's right pane uses `LayoutAreaControl(address, "")` which resolves to `Content` — not back to `Overview` — avoiding infinite recursion:

```csharp
// In AddCodeViews:
.WithDefaultArea("Content")     // Default = simple code block
.WithView("Content", Content)   // Simple markdown view
.WithView("Overview", Overview) // Splitter with code list + embedded Content

// In the Overview Splitter's right pane:
new LayoutAreaControl(hubAddress, new LayoutAreaReference(""))
// ^ Resolves to "Content" (the default), NOT "Overview"
```

# Principle: Define Layout Areas Close to Their Object

Layout areas should be defined in the same module as the object they represent. This keeps each node type **self-contained and composable**.

- **Code views** are defined in `CodeLayoutAreas` and registered via `AddCodeViews()` on Code node hubs
- **Markdown views** are defined in `MarkdownLayoutAreas` and registered via `AddMarkdownViews()`
- **NodeType views** are defined in `NodeTypeLayoutAreas` and registered via `AddNodeTypeView()`

Each node type owns its own rendering. The parent NodeType doesn't need to know how Code nodes display their content — it just links to the Code node's Overview area, and the Code node handles the rest:

```csharp
// In NodeTypeLayoutAreas — the parent just links to Code node's own area:
var codeHref = new LayoutAreaReference(CodeLayoutAreas.OverviewArea)
    .ToHref(codeNode.Path);
new NavLinkControl(codeNode.Name, icon, codeHref);
```

# Common Patterns

## Splitter with NavMenu and Content Pane

Used by NodeType and Code nodes to show a left navigation with a main content area:

```csharp
Controls.Splitter
    .WithSkin(s => s.WithOrientation(Orientation.Horizontal)
        .WithWidth("100%").WithHeight("calc(100vh - 100px)"))
    .WithView(
        BuildNavMenu(),   // Left: navigation menu
        skin => skin.WithSize("280px").WithCollapsible(true))
    .WithView(
        BuildContent(),   // Right: main content
        skin => skin.WithSize("*"));
```

## Read-Only View with Edit Button

Show content with a button that navigates to the Edit area:

```csharp
var editHref = new LayoutAreaReference("Edit").ToHref(hubAddress);

Controls.Stack
    .WithView(Controls.H1(title))
    .WithView(Controls.Button("")
        .WithIconStart(FluentIcons.Edit())
        .WithNavigateToHref(editHref));
```

## Edit View with Save and Cancel

An editor that saves data and navigates back to the Overview:

```csharp
Controls.Button("Save").WithClickAction(async ctx =>
{
    // ... save logic ...
    var viewHref = new LayoutAreaReference("Overview").ToHref(hubAddress);
    ctx.Host.UpdateArea(ctx.Area, new RedirectControl(viewHref));
});

var cancelHref = new LayoutAreaReference("Overview").ToHref(hubAddress);
Controls.Button("Cancel").WithNavigateToHref(cancelHref);
```

# See Also

- [Adding Controls to a UI](../ContainerControl) - Container types and WithView
- [Data Binding](../DataBinding) - How data flows between server and UI
- [Adding Editable Forms](../Editor) - Auto-generated forms from records
- [Observables](../Observables) - Reactive views that update when data changes
