---
Name: Layout Areas
Category: Documentation
Description: Named rendering slots on hubs that define what UI each area displays — and how to compose views across node boundaries
Icon: /static/NodeTypeIcons/code.svg
---

Layout areas are **named rendering slots** registered on a message hub. Each area pairs a name with a view function that produces UI controls. When a client requests an area, the hub runs the matching view function and streams the result — live updates included when the view returns an observable.
<svg viewBox="0 0 760 260" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity=".6"/>
    </marker>
  </defs>
  <rect x="20" y="20" width="200" height="220" rx="10" fill="#1e2a3a" stroke="currentColor" stroke-opacity=".25" stroke-width="1.5"/>
  <text x="120" y="45" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-size="11" font-weight="600" letter-spacing="1">NODE HUB A</text>
  <rect x="40" y="55" width="160" height="38" rx="8" fill="#1e88e5"/>
  <text x="120" y="70" text-anchor="middle" fill="#fff" font-weight="600">Overview</text>
  <text x="120" y="85" text-anchor="middle" fill="#fff" font-size="11" fill-opacity=".85">WithView("Overview", …)</text>
  <rect x="40" y="103" width="160" height="38" rx="8" fill="#26a69a"/>
  <text x="120" y="118" text-anchor="middle" fill="#fff" font-weight="600">Content</text>
  <text x="120" y="133" text-anchor="middle" fill="#fff" font-size="11" fill-opacity=".85">WithView("Content", …)</text>
  <rect x="40" y="151" width="160" height="38" rx="8" fill="#5c6bc0"/>
  <text x="120" y="166" text-anchor="middle" fill="#fff" font-weight="600">Edit</text>
  <text x="120" y="181" text-anchor="middle" fill="#fff" font-size="11" fill-opacity=".85">WithView("Edit", …)</text>
  <rect x="40" y="199" width="160" height="30" rx="8" fill="#e53935" fill-opacity=".85"/>
  <text x="120" y="219" text-anchor="middle" fill="#fff" font-size="11">default → "Content"</text>
  <rect x="280" y="20" width="200" height="220" rx="10" fill="#1e2a3a" stroke="currentColor" stroke-opacity=".25" stroke-width="1.5"/>
  <text x="380" y="45" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-size="11" font-weight="600" letter-spacing="1">NODE HUB B</text>
  <rect x="300" y="55" width="160" height="38" rx="8" fill="#1e88e5"/>
  <text x="380" y="70" text-anchor="middle" fill="#fff" font-weight="600">Overview</text>
  <text x="380" y="85" text-anchor="middle" fill="#fff" font-size="11" fill-opacity=".85">Splitter view</text>
  <rect x="300" y="103" width="160" height="38" rx="8" fill="#26a69a"/>
  <text x="380" y="118" text-anchor="middle" fill="#fff" font-weight="600">Thumbnail</text>
  <text x="380" y="133" text-anchor="middle" fill="#fff" font-size="11" fill-opacity=".85">WithView("Thumbnail", …)</text>
  <rect x="300" y="151" width="160" height="38" rx="8" fill="#f57c00"/>
  <text x="380" y="166" text-anchor="middle" fill="#fff" font-weight="600">LayoutAreaControl</text>
  <text x="380" y="181" text-anchor="middle" fill="#fff" font-size="11" fill-opacity=".85">embeds Hub A area</text>
  <rect x="540" y="70" width="200" height="100" rx="10" fill="#1a2530" stroke="#1e88e5" stroke-opacity=".6" stroke-width="1.5"/>
  <text x="640" y="95" text-anchor="middle" fill="currentColor" fill-opacity=".55" font-size="11" font-weight="600" letter-spacing="1">CLIENT</text>
  <text x="640" y="118" text-anchor="middle" fill="currentColor" fill-opacity=".75" font-size="12">requests area</text>
  <text x="640" y="137" text-anchor="middle" fill="currentColor" fill-opacity=".75" font-size="12">← streams UI controls</text>
  <text x="640" y="155" text-anchor="middle" fill="currentColor" fill-opacity=".75" font-size="12">live-updating</text>
  <line x1="460" y1="160" x2="535" y2="120" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="460" y1="75" x2="535" y2="100" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="200" y1="118" x2="295" y2="165" stroke="#f57c00" stroke-opacity=".8" stroke-width="1.5" stroke-dasharray="5,3" marker-end="url(#arr)"/>
  <text x="238" y="152" text-anchor="middle" fill="#f57c00" fill-opacity=".85" font-size="11">LayoutAreaControl</text>
</svg>
*Layout areas are named slots on each hub; `LayoutAreaControl` composes them across node boundaries by embedding one hub's area inside another.*

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
