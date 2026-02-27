---
Name: Collection Prefix
Category: Documentation
Description: Embed files from content collections using any collection name
Icon: /static/storage/content/MeshWeaver/Documentation/DataMesh/UnifiedPath/ContentPrefix/icon.svg
---

Any prefix that is **not a reserved keyword** is treated as a **content collection name**. This allows you to embed files from any configured content collection.

# Reserved Keywords

The following prefixes have special meaning and are **not** collection names:
- `data:` - Access data entities
- `schema:` - Access type schemas
- `model:` - Access data models
- `metadata:` - Access node metadata
- `area:` - Access layout areas

# Collection Names

Any **other prefix** is treated as a content collection name:

| Syntax | Description |
|--------|-------------|
| `content:file.svg` | File from the "content" collection |
| `assets:logo.png` | File from the "assets" collection |
| `docs:readme.md` | File from the "docs" collection |
| `images:photo.jpg` | File from the "images" collection |

# Syntax

```
@@{address}/{collectionName}:{path}
@{address}/{collectionName}:{path}
```

# Configuring Collections

Collections must be configured in the hub setup:

```csharp
config.AddFileSystemContentCollection("content", sp => "./content")
      .AddFileSystemContentCollection("assets", sp => "./assets")
      .MapContentCollection("avatars", "storage", "persons/avatars");
```

# Supported File Types

| Type | Extension | Rendering |
|------|-----------|----------|
| Images | `.png`, `.jpg`, `.gif`, `.svg` | Inline image |
| Markdown | `.md` | Rendered markdown |
| PDF | `.pdf` | Embedded viewer |
| Code | `.cs`, `.js`, `.ts`, `.json` | Syntax-highlighted code block |
| Text | `.txt`, `.xml`, `.yaml` | Code block |

# Examples

## Example 1: Content Collection

Embed an SVG logo from the "content" collection:

**Syntax:**
```
@@MeshWeaver/Documentation/DataMesh/UnifiedPath/content:logo.svg
```

**Result:**

@@MeshWeaver/Documentation/DataMesh/UnifiedPath/content:logo.svg

## Example 2: Custom Collection Name

If you have configured an "assets" collection, you can reference it:

```
@@MyApp/assets:images/banner.png
```

## Example 3: Hyperlink to File

Use single `@` to create a navigation link:

**Syntax:**
```
@MeshWeaver/Documentation/DataMesh/UnifiedPath/content:sample.md
```

**Result:**

@MeshWeaver/Documentation/DataMesh/UnifiedPath/content:sample.md

# Notes

- The collection name is **not** hard-coded - use any name you've configured
- Collections are registered using `AddFileSystemContentCollection`, `MapContentCollection`, etc.
- Path is relative to the collection's base directory
- Files are served via the content collection provider
