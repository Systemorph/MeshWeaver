---
Name: Data Prefix
Category: Documentation
Description: Embed data collections and entities
Icon: /static/DocContent/DataMesh/UnifiedPath/DataPrefix/icon.svg
---

The `data:` prefix embeds data collections or individual entities from a node's data store.

# Syntax

```
@@{address}/data:{collection}[/{entityId}]
@@{address}/data:
```

# Components

| Component | Description | Example |
|-----------|-------------|---------|
| `collection` | Name of the data collection | `Posts`, `Users`, `Products` |
| `entityId` | (Optional) Specific entity ID | `post-123`, `user-456` |
| (empty) | Self-reference to node's data | |

# Example: Self-Reference

Embed the data collections of this node:

**Syntax:**
```
@@Doc/DataMesh/UnifiedPath/DataPrefix/data:
```

Empty path after `data:` means self-reference - shows available data collections.

**Result:**

@@Doc/DataMesh/UnifiedPath/DataPrefix/data:

# Example: Collection Reference

Reference a specific data collection:

**Syntax:**
```
@Doc/DataMesh/UnifiedPath/data:
```

**Result:**

@Doc/DataMesh/UnifiedPath/data:

# Rendering

| Reference Type | Renders As |
|----------------|------------|
| Collection | Data grid with all items |
| Single entity | Entity detail view |
| (empty) | List of available collections |

# Notes

- Data is fetched from the node's workspace
- Collections use the registered data providers
- Supports filtering via query parameters
