---
Name: Layout Areas
Category: Documentation
Description: Named rendering slots on hubs that define what UI each area displays — and how to compose views across node boundaries
Icon: /static/NodeTypeIcons/code.svg
---

Layout areas are **named rendering slots** registered on a message hub. Each area pairs a name with a view function that produces UI controls. When a client requests an area, the hub runs the matching view function and streams the result — live updates included when the view returns an observable.

# Registering Layout Areas

Add layout areas through the `AddLayout()` pipeline on `MessageHubConfiguration`:

```csharp
public static MessageHubConfiguration AddCodeViews(this MessageHubConfiguration configuration)
    => configuration.AddLayout(layout => layout
        .WithDefaultArea("Content")       // Rendered when no area name is specified
        .WithView("Content", Content)     // Simple read-only view
        .WithView("Overview", Overview)   // Splitter with navigation
        .WithView("Edit", Edit));         // Editor view
```

## Core Registration Methods

| Method | Purpose |
|--------|---------|
| `WithDefaultArea(name)` | Sets which area renders when the caller specifies an empty area name |
| `WithView(name, function)` | Registers a view function for the named area |

## View Function Signatures

A view function receives a `LayoutAreaHost` and `RenderingContext`. Return a static control for a one-time render, or an `IObservable<UiControl?>` for a live-updating view:

```csharp
// Static view — renders once
public static UiControl Edit(LayoutAreaHost host, RenderingContext ctx)
{
    return Controls.Stack.WithView(Controls.H1("Editor"));
}

// Reactive view — re-renders whenever data changes
public static IObservable<UiControl?> Content(LayoutAreaHost host, RenderingContext ctx)
{
    return host.Workspace.GetStream<MeshNode>()
        .Select(nodes => (UiControl?)BuildContent(nodes));
}
```

## Navigating Between Areas

Use `LayoutAreaReference` and `ToHref()` to build navigation links between areas on the same hub or across node boundaries:

```csharp
// Navigate to the Edit area of the current hub
var editHref = new LayoutAreaReference("Edit").ToHref(hubAddress);
Controls.Button("Edit").WithNavigateToHref(editHref);

// Navigate to a specific area on a different node
var overviewHref = new LayoutAreaReference("Overview").ToHref(otherNodePath);
new NavLinkControl("View Code", FluentIcons.Code(), overviewHref);
```

# Composing Views Across Hubs with LayoutAreaControl

`LayoutAreaControl` embeds a layout area from one hub inside another hub's view. This is the primary mechanism for composing UI across node boundaries:

```csharp
// Embed the default area of a target hub
new LayoutAreaControl(targetAddress, new LayoutAreaReference(""))

// Embed a specific named area
new LayoutAreaControl(targetAddress, new LayoutAreaReference("Thumbnail"))
```

## How the Default Area Resolves (and Avoids Infinite Recursion)

When the `LayoutAreaReference` has an empty area name, the target hub resolves its **default area** — the one registered via `WithDefaultArea`. This behaviour is the key to safe composition: an `Overview` area can embed a `Content` pane without looping back to itself.

> **Example — Code node Overview Splitter**
>
> The Code node sets `Content` as its default area (a simple markdown code block) and `Overview` as a Splitter. The Splitter's right pane uses `LayoutAreaControl(address, "")`, which resolves to `Content` — not back to `Overview` — so there is no infinite recursion:
>
> ```csharp
> // In AddCodeViews:
> .WithDefaultArea("Content")     // Default = simple code block
> .WithView("Content", Content)   // Simple markdown view
> .WithView("Overview", Overview) // Splitter with code list + embedded Content
>
> // In the Overview Splitter's right pane:
> new LayoutAreaControl(hubAddress, new LayoutAreaReference(""))
> // ^ Resolves to "Content" (the default), NOT "Overview"
> ```

# Principle: Define Layout Areas Close to Their Object

Layout areas belong in the same module as the object they represent. This keeps each node type **self-contained and composable** — a parent never needs to know how a child node renders itself, only which area to link to.

- **Code views** are defined in `CodeLayoutAreas` and registered via `AddCodeViews()` on Code node hubs
- **Markdown views** are defined in `MarkdownLayoutAreas` and registered via `AddMarkdownViews()`
- **NodeType views** are defined in `NodeTypeLayoutAreas` and registered via `AddNodeTypeView()`

```csharp
// In NodeTypeLayoutAreas — the parent just links to the Code node's own area:
var codeHref = new LayoutAreaReference(CodeLayoutAreas.OverviewArea)
    .ToHref(codeNode.Path);
new NavLinkControl(codeNode.Name, icon, codeHref);
```

The Code node handles all of its own display logic. The parent simply points at it.

# Common Patterns

## Splitter with Nav Menu and Content Pane

A horizontal splitter with a collapsible left navigation and a fluid content area — used by NodeType and Code nodes:

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

Display content alongside a button that navigates to the Edit area:

```csharp
var editHref = new LayoutAreaReference("Edit").ToHref(hubAddress);

Controls.Stack
    .WithView(Controls.H1(title))
    .WithView(Controls.Button("")
        .WithIconStart(FluentIcons.Edit())
        .WithNavigateToHref(editHref));
```

## Edit View with Save and Cancel

An editor that commits changes and redirects back to the Overview:

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

## Live Demo

The cell below shows a self-contained layout area composed from a stack of controls — the same building blocks used for every area above:

```csharp --render LayoutAreaDemo --show-code
MeshWeaver.Layout.Controls.Stack
    .WithView(MeshWeaver.Layout.Controls.Html("<strong>Layout Area — live render</strong>"))
    .WithView(MeshWeaver.Layout.Controls.Markdown(
        $"This area was rendered at **{DateTime.Now:HH:mm:ss}**. " +
        "In a real hub it would update reactively whenever its data changes."))
    .WithView(MeshWeaver.Layout.Controls.Button("Navigate to Edit area"))
```

# See Also

- [Adding Controls to a UI](../ContainerControl) — Container types and `WithView`
- [Data Binding](../DataBinding) — How data flows between server and UI
- [Adding Editable Forms](../Editor) — Auto-generated forms from records
- [Observables](../Observables) — Reactive views that update when data changes
