---
Name: Collection Prefix
Category: Documentation
Description: Embed files from content collections using any collection name
Icon: /static/DocContent/DataMesh/UnifiedPath/ContentPrefix/icon.svg
---

Content collections store files (images, documents, markdown, etc.) associated with mesh nodes. Use the `content/` prefix to access files.

# Reserved Keywords

The following prefixes have special meaning:
- `data/` - Access data entities
- `schema/` - Access type schemas
- `model/` - Access data models
- `area/` - Access layout areas
- `content/` - Access content files
- `menu/` - Access menu structure

# Collection Names

| Syntax | Description |
|--------|-------------|
| `content/file.svg` | File from the "content" collection |
| `assets/logo.png` | File from the "assets" collection |
| `docs/readme.md` | File from the "docs" collection |

> **Note:** The legacy colon syntax (`content:file.svg`) is still supported for backward compatibility.

# Syntax

```
@@{address}/content/{path}
@{address}/content/{path}
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
@@Doc/DataMesh/UnifiedPath/content/logo.svg
```

**Result:**

@@../content:logo.svg

## Example 2: Custom Collection Name

If you have configured an "assets" collection, you can reference it:

```
@@MyApp/assets/images/banner.png
```

## Example 3: Hyperlink to File

Use single `@` to create a navigation link:

**Syntax:**
```
@Doc/DataMesh/UnifiedPath/content/sample.md
```

**Result:**

@../content:sample.md

# Notes

- The collection name is **not** hard-coded - use any name you've configured
- Collections are registered using `AddFileSystemContentCollection`, `MapContentCollection`, etc.
- Path is relative to the collection's base directory
- Files are served via the content collection provider
