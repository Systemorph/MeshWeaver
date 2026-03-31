---
Name: Unified Path
Category: Documentation
Description: Reference and embed content using @ and @@ syntax
Icon: /static/storage/content/MeshWeaver/Documentation/DataMesh/UnifiedPath/icon.svg
---

Unified Path allows you to **reference** and **embed** content from anywhere in your MeshWeaver application using a simple `@` notation.

# Pattern

```
{address}/{prefix}:{path}
```

| Component | Description |
|-----------|-------------|
| `address` | MeshNode path (resolved via MeshCatalog) |
| `prefix` | Either a **reserved keyword** or a **content collection name** |
| `path` | Resource within the address |

## Reserved Keywords

These prefixes have special meaning and map to specific layout areas:

| Prefix | Description |
|--------|-------------|
| `data:` | Access the node's Content data as JSON |
| `schema:` | Access the ContentType schema |
| `model:` | Access the data model |
| `area:` | Access a specific layout area |

## Content Collection Prefixes

**Any other prefix** is treated as a **content collection name**. The collection is configured in the hub setup:

| Example | Description |
|---------|-------------|
| `content:` | Files from the "content" collection |
| `assets:` | Files from the "assets" collection |
| `files:` | Files from the "files" collection |
| `docs:` | Files from the "docs" collection |

# @ vs @@

| Syntax | Behavior |
|--------|----------|
| `@path` | **Hyperlink** - navigates to content |
| `@@path` | **Inline** - embeds content in place |

**Important:** References must be at the **start of a line**.
