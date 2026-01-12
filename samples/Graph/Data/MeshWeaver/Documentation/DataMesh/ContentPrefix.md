---
Name: Content Prefix
Category: Documentation
Description: Embed files like images, markdown, and documents
Icon: Document
---

# Content Prefix

The `content:` prefix embeds static files from a node's content folder.

## Syntax

```
@@{address}/content:{path}
```

## Supported File Types

| Type | Extension | Rendering |
|------|-----------|----------|
| Images | `.png`, `.jpg`, `.gif`, `.svg` | Inline image |
| Markdown | `.md` | Rendered markdown |
| PDF | `.pdf` | Embedded viewer |
| Text | `.txt`, `.json`, `.xml` | Code block |

## Example: Inline Image

Embed an SVG logo from this documentation's content folder:

**Syntax:**
```
@@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/content:logo.svg
```

**Result:**

@@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/content:logo.svg

## Example: Embedded Markdown

Embed a markdown file inline:

**Syntax:**
```
@@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/content:sample.md
```

**Result:**

@@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/content:sample.md

## Hyperlink to Content

Use single `@` to create a download link:

**Syntax:**
```
@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/content:logo.svg
```

**Result:**

@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/content:logo.svg

## Notes

- Content files are stored in `content/{namespace}/` folders
- Path is relative to the node's content directory
- Files are served via the content collection provider
