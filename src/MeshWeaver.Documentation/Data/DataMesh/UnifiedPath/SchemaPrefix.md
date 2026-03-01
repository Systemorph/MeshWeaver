---
Name: Schema Prefix
Category: Documentation
Description: Embed type schemas and data models
Icon: /static/DocContent/DataMesh/UnifiedPath/SchemaPrefix/icon.svg
---

The `schema:` prefix embeds type schema definitions from a node as JSON schema.

# Syntax

```
@@{address}/schema:{typeName}
@@{address}/schema:         (current node's schema)
```

# Use Cases

| Reference | Description |
|-----------|-------------|
| `schema:` | Schema of the current node (self-reference) |
| `schema:MeshNode` | Built-in MeshNode schema |
| `schema:CustomType` | Custom type schema |

# Example: Node Schema

Embed the schema definition of MeshNode:

**Syntax:**
```
@@Doc/DataMesh/UnifiedPath/schema:MeshNode
```

**Result:**

@@Doc/DataMesh/UnifiedPath/schema:MeshNode

# Example: Self Schema

Show the schema of this node (empty path means self-reference):

**Syntax:**
```
@@Doc/DataMesh/UnifiedPath/SchemaPrefix/schema:
```

**Result:**

@@Doc/DataMesh/UnifiedPath/SchemaPrefix/schema:

# Schema Rendering

Schemas are rendered as JSON Schema:
- Property table with types
- Required/optional indicators
- Default values
- Nested type expansion
