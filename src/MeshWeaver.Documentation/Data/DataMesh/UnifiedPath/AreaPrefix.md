---
Name: Area Prefix
Category: Documentation
Description: Embed layout areas — Thumbnail, Details, Catalog, and custom views — directly in markdown content
Icon: /static/DocContent/DataMesh/UnifiedPath/AreaPrefix/icon.svg
---

The `area/` prefix (or its shorthand) embeds a rendered layout area from any mesh node right inside a markdown document. Use it to pull in a thumbnail card, a full details pane, a child-node catalog, or any custom area — live and interactive, not a static screenshot.
<svg viewBox="0 0 760 260" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L8,3.5 L0,7 Z" fill="currentColor" fill-opacity=".55"/>
    </marker>
  </defs>
  <rect x="0" y="0" width="760" height="260" rx="12" fill="currentColor" fill-opacity=".04" stroke="currentColor" stroke-opacity=".1" stroke-width="1"/>
  <text x="380" y="28" text-anchor="middle" font-size="14" font-weight="bold" fill="currentColor" fill-opacity=".75">Area Prefix — Embed vs Navigate</text>
  <rect x="40" y="50" width="200" height="54" rx="10" fill="#5c6bc0"/>
  <text x="140" y="73" text-anchor="middle" font-weight="bold" fill="#fff" font-size="13">@@Node/AreaName</text>
  <text x="140" y="92" text-anchor="middle" fill="#dce0ff" font-size="11">(double @  — embed)</text>
  <rect x="40" y="148" width="200" height="54" rx="10" fill="#26a69a"/>
  <text x="140" y="171" text-anchor="middle" font-weight="bold" fill="#fff" font-size="13">@Node/AreaName</text>
  <text x="140" y="190" text-anchor="middle" fill="#ccefec" font-size="11">(single @  — hyperlink)</text>
  <line x1="240" y1="77" x2="330" y2="77" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="240" y1="175" x2="330" y2="175" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="330" y="48" width="210" height="58" rx="10" fill="#1e88e5"/>
  <text x="435" y="71" text-anchor="middle" font-weight="bold" fill="#fff" font-size="12">Inline rendered area</text>
  <text x="435" y="88" text-anchor="middle" fill="#c8e4ff" font-size="11">Thumbnail · Details · Catalog …</text>
  <rect x="330" y="146" width="210" height="58" rx="10" fill="#43a047"/>
  <text x="435" y="169" text-anchor="middle" font-weight="bold" fill="#fff" font-size="12">Navigation link</text>
  <text x="435" y="186" text-anchor="middle" fill="#c8f5cb" font-size="11">Navigates to the target node</text>
  <line x1="540" y1="77" x2="620" y2="77" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="540" y1="175" x2="620" y2="175" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="620" y="48" width="112" height="58" rx="10" fill="#f57c00"/>
  <text x="676" y="71" text-anchor="middle" font-weight="bold" fill="#fff" font-size="12">Live widget</text>
  <text x="676" y="88" text-anchor="middle" fill="#ffe8cc" font-size="11">in markdown</text>
  <rect x="620" y="146" width="112" height="58" rx="10" fill="#8e24aa"/>
  <text x="676" y="169" text-anchor="middle" font-weight="bold" fill="#fff" font-size="12">Clickable URL</text>
  <text x="676" y="186" text-anchor="middle" fill="#f0d6ff" font-size="11">in markdown</text>
  <text x="380" y="240" text-anchor="middle" font-size="11" fill="currentColor" fill-opacity=".5">@@Node/area/AreaName  and  @@Node/AreaName  are equivalent shorthands</text>
</svg>
*`@@` embeds a live, interactive area inline; `@` produces a navigation hyperlink to the same node.*

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
