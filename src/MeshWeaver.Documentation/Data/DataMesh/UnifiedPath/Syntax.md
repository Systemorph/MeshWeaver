---
Name: Unified Path Syntax
Category: Documentation
Description: The address/prefix:path pattern explained
Icon: /static/DocContent/DataMesh/UnifiedPath/Syntax/icon.svg
---

Unified Path references use the pattern:

```
{address}/{prefix}:{path}
```

# Components

| Component | Description | Example |
|-----------|-------------|----------|
| `address` | The MeshNode address (resolved via MeshCatalog) | `Doc/DataMesh` |
| `prefix` | Content type selector | `content:`, `data:`, `area:`, `schema:`, `model:` |
| `path` | Resource path within the address | `docs/readme.md`, `Posts`, `Thumbnail` |

# Address Resolution

The `address` portion is matched against nodes in the MeshCatalog using score-based matching:

1. The path is split into segments
2. Each segment is matched against registered namespace patterns
3. The best match (highest score) determines the target node
4. Remaining segments become the `path` portion

# Prefix Types

| Prefix | Purpose | Renders As |
|--------|---------|------------|
| `content:` | Static files (images, markdown, etc.) | File content inline |
| `data:` | Data collections/entities | Data grid or entity view |
| `area:` | Layout areas | Layout component |
| `schema:` | Type schemas | JSON schema code block |
| `model:` | Data model diagrams | Mermaid class diagram |
| *(none)* | Default area reference | Layout component |

# Single @ vs Double @@

| Syntax | Behavior |
|--------|----------|
| `@path` | Creates a **hyperlink** to the referenced content |
| `@@path` | **Embeds** the content inline |

# Examples

## Simple Reference (hyperlink)

```
@Doc/DataMesh/QuerySyntax
```

Result:

@Doc/DataMesh/QuerySyntax

## Embedded Content

```
@@Doc/DataMesh/UnifiedPath/Thumbnail
```

Result:

@@Doc/DataMesh/UnifiedPath/Thumbnail

# Related

@Doc/DataMesh/UnifiedPath/ContentPrefix

@Doc/DataMesh/UnifiedPath/DataPrefix

@Doc/DataMesh/UnifiedPath/AreaPrefix
