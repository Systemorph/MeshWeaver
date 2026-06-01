---
Name: Area Prefix
Category: Documentation
Description: Embed layout areas — Thumbnail, Details, Catalog, and custom views — directly in markdown content
Icon: /static/DocContent/DataMesh/UnifiedPath/AreaPrefix/icon.svg
---

The `area/` prefix (or its shorthand) embeds a rendered layout area from any mesh node right inside a markdown document. Use it to pull in a thumbnail card, a full details pane, a child-node catalog, or any custom area — live and interactive, not a static screenshot.

# Syntax

```
@@{address}/area/{areaName}
@@{address}/{areaName}          (shorthand — preferred)
```

> **Backward compatibility:** The legacy colon syntax (`area:AreaName`) is still accepted.

A single `@` (no doubling) produces a **navigation hyperlink** to the node instead of embedding it — see [Hyperlinks to Areas](#hyperlinks-to-areas) below.

# Standard Areas

Every MeshNode exposes a set of built-in areas out of the box:

| Area | What it renders |
|------|-----------------|
| `Thumbnail` | Compact card — image, title, short description |
| `Details` | Full content view with action menu |
| `Catalog` | Grid of child nodes with search |
| `Metadata` | Node property display |
| `Settings` | Configuration interface |

# Example: Thumbnail Area

Embed the thumbnail card for a documentation node:

**Markdown source:**
```
@@Doc/DataMesh/UnifiedPath/Thumbnail
```

**Rendered result:**

@@../Thumbnail

# Example: Catalog Area

Embed the browsable child-node grid for a section:

**Markdown source:**
```
@@Doc/DataMesh/Search
```

**Rendered result:**

@@../../Search

# Hyperlinks to Areas

A single `@` creates a clickable navigation link — the user is taken to that node rather than seeing an inline embed:

**Markdown source:**
```
@Doc/Architecture/Overview
```

**Rendered result:**

@../../../Architecture/Overview

# Custom Areas

Nodes can register any number of custom layout areas. Declare the area in the node's hub configuration:

```csharp
configuration.AddLayout(layout => layout
    .WithView("MyCustomArea", MyCustomView));
```

Then embed it anywhere with the standard shorthand:

```
@@MyNode/MyCustomArea
```

# Live Demo

The cell below builds a small layout stack that mirrors what an embedded area looks like — a header card followed by descriptive text. Because `@@` references depend on the live mesh, this snippet uses the MeshWeaver layout API directly to illustrate the same visual result:

```csharp --render AreaPrefixDemo --show-code
MeshWeaver.Layout.Controls.Stack
    .WithView(MeshWeaver.Layout.Controls.Html(
        "<div style='padding:12px;border:1px solid #ddd;border-radius:6px;background:#f9f9f9'>" +
        "<strong>Thumbnail area</strong> &mdash; compact card view" +
        "</div>"))
    .WithView(MeshWeaver.Layout.Controls.Html(
        "<div style='padding:12px;border:1px solid #ddd;border-radius:6px;background:#f9f9f9;margin-top:8px'>" +
        "<strong>Catalog area</strong> &mdash; searchable child-node grid" +
        "</div>"))
    .WithView(MeshWeaver.Layout.Controls.Markdown(
        "_Use_ `@@Node/AreaName` _in any markdown document to embed either of these live._"))
```
