---
Name: Node Cards & Catalogs
Category: Documentation
Description: Present mesh nodes as cards, thumbnails, live query-driven collections, and grouped catalogs — the building blocks of every content overview page.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="8" height="8" rx="1"/><rect x="13" y="3" width="8" height="8" rx="1"/><rect x="3" y="13" width="8" height="8" rx="1"/><rect x="13" y="13" width="8" height="8" rx="1"/></svg>
---

# Node Cards & Catalogs

Content overview pages — space home pages, search results, "related nodes" panels — are composed
from four controls that all take mesh-node **paths** (never copies of node content):

| Control | Renders |
|---|---|
| `MeshNodeCardControl` | One node as a card with title, description, image; click navigates |
| `MeshNodeThumbnailControl` | A compact thumbnail card, used in grids |
| `MeshNodeCollectionControl` | A **live** query-driven node list |
| `CatalogControl` | Grouped, collapsible sections of any controls |

All the examples below point at pages of this documentation, so they render the same in every
deployment.

---

# Node Card

A card takes the node path plus optional display overrides. Clicking it navigates to the node.

```csharp --render NodeCardDemo --show-code
using MeshWeaver.Graph;

Controls.Stack
    .WithOrientation(Orientation.Horizontal)
    .WithHorizontalGap("12px")
    .WithView(new MeshNodeCardControl(
        "Doc/GUI/DataGrid",
        "Displaying Data in a UI",
        "Sortable, resizable data tables"))
    .WithView(new MeshNodeCardControl(
        "Doc/GUI/Editor",
        "Editor",
        "Auto-generated forms from records"))
```

For a card whose title/description should track the node itself, build it with
`MeshNodeCardControl.FromNode(node, fallbackPath)` inside a layout area that observes the node
stream.

---

# Node Thumbnail

The compact variant used in card grids — same path-first contract, plus the node type shown as a
tag.

```csharp --render NodeThumbnailDemo --show-code
using MeshWeaver.Graph;

Controls.Stack
    .WithOrientation(Orientation.Horizontal)
    .WithHorizontalGap("12px")
    .WithView(new MeshNodeThumbnailControl(
        "Doc/GUI/LayoutGrid", "Layout Grid",
        "Responsive grid layouts", null, "Markdown"))
    .WithView(new MeshNodeThumbnailControl(
        "Doc/GUI/ContainerControl/Splitter", "Splitter",
        "Resizable panes", null, "Markdown"))
```

---

# Live Node Collection

`MeshNodeCollectionControl` runs one or more [queries](/Doc/DataMesh/QuerySyntax) and renders the
matching nodes as a **live** list — nodes created, renamed, or deleted while you watch update the
view without a reload. `WithShowAdd(false)` makes it read-only; leave the add button on to let
users attach nodes picked via the built-in picker dialog.

```csharp --render NodeCollectionDemo --show-code
new MeshNodeCollectionControl()
    .WithQueries("namespace:Doc/GUI/ContainerControl nodeType:Markdown")
    .WithShowAdd(false)
```

---

# Catalog

`CatalogControl` arranges any controls into titled, collapsible, counted sections — the layout
behind the grouped search results. Groups hold plain `UiControl`s, so cards, thumbnails, and labels
mix freely.

```csharp --render CatalogDemo --show-code
using System.Collections.Immutable;
using MeshWeaver.Graph;
using MeshWeaver.Layout.Catalog;

new CatalogControl()
    .WithGroup(new CatalogGroup
    {
        Key = "containers",
        Label = "Container Controls",
        Emoji = "📦",
        Order = 1,
        Items =
        [
            new MeshNodeCardControl("Doc/GUI/ContainerControl/Stack", "Stack", "Vertical and horizontal flow"),
            new MeshNodeCardControl("Doc/GUI/ContainerControl/Tabs", "Tabs", "One panel at a time"),
            new MeshNodeCardControl("Doc/GUI/ContainerControl/Toolbar", "Toolbar", "Action bars")
        ]
    })
    .WithGroup(new CatalogGroup
    {
        Key = "data",
        Label = "Data Controls",
        Emoji = "📊",
        Order = 2,
        Items =
        [
            new MeshNodeCardControl("Doc/GUI/DataGrid", "Data Grid", "Tabular data"),
            new MeshNodeCardControl("Doc/GUI/DataBinding", "Data Binding", "Two-way reactive binding")
        ]
    })
    .WithSkin(s => s.WithShowCounts(true).WithCardHeight(120))
```

---

# See Also

- [Mesh Search & Catalogs](../MeshSearch) — the URL-driven `Search` area built on these controls
- [Query Syntax](/Doc/DataMesh/QuerySyntax) — the query language behind collections
- [Form Input Controls](../InputControls) — includes the `MeshNodePicker` for *selecting* nodes
