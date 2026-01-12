---
Name: Schema Prefix
Category: Documentation
Description: Embed type schemas and data models
Icon: DataArea
---

# Schema Prefix

The `schema:` prefix embeds type schema definitions from a node as JSON schema.

## Syntax

```
@@{address}/schema:{typeName}
@@{address}/schema:         (current node's schema)
```

## Use Cases

| Reference | Description |
|-----------|-------------|
| `schema:` | Schema of the current node (self-reference) |
| `schema:MeshNode` | Built-in MeshNode schema |
| `schema:CustomType` | Custom type schema |

## Example: Node Schema

Embed the schema definition of MeshNode:

**Syntax:**
```
@@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/schema:MeshNode
```

**Result:**

@@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/schema:MeshNode

## Example: Self Schema

Show the schema of this node (empty path means self-reference):

**Syntax:**
```
@@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/SchemaPrefix/schema:
```

**Result:**

@@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/SchemaPrefix/schema:

## Schema Rendering

Schemas are rendered as JSON Schema:
- Property table with types
- Required/optional indicators
- Default values
- Nested type expansion
