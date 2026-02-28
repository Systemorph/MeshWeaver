---
Name: Area Prefix
Category: Documentation
Description: Embed layout areas like Thumbnail, Details, or Catalog
Icon: /static/storage/content/Doc/DataMesh/UnifiedPath/AreaPrefix/icon.svg
---

The `area:` prefix (or no prefix) embeds layout areas from a node.

# Syntax

```
@@{address}/area:{areaName}
@@{address}/{areaName}          (shorthand)
```

# Standard Areas

MeshNodes typically have these standard areas:

| Area | Description |
|------|-------------|
| `Thumbnail` | Compact card view (image, title, description) |
| `Details` | Full content with action menu |
| `Catalog` | Grid of child nodes with search |
| `Metadata` | Node properties display |
| `Settings` | Configuration view |

# Example: Thumbnail Area

Embed the thumbnail card of this documentation:

**Syntax:**
```
@@Doc/DataMesh/UnifiedPath/Thumbnail
```

**Result:**

@@Doc/DataMesh/UnifiedPath/Thumbnail

# Example: Catalog Area

Embed the catalog (child nodes grid) of an organization:

**Syntax:**
```
@@Systemorph/Catalog
```

**Result:**

@@Systemorph/Catalog

# Hyperlinks to Areas

Single `@` creates a navigation link:

**Syntax:**
```
@Systemorph/Marketing/Details
```

**Result:**

@Systemorph/Marketing/Details

# Custom Areas

Nodes can register custom areas:

```csharp
configuration.AddLayout(layout => layout
    .WithView("MyCustomArea", MyCustomView));
```

Then reference with:
```
@@MyNode/MyCustomArea
```
