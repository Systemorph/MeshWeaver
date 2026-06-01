---
Name: Schema Prefix
Category: Documentation
Description: Embed live type schemas and data models directly into documentation
Icon: /static/DocContent/DataMesh/UnifiedPath/SchemaPrefix/icon.svg
---

The `schema/` prefix lets you embed a type's JSON Schema definition directly into any node. Rather than maintaining separate schema documentation by hand, a single reference pulls the authoritative schema from the type itself — keeping your docs and your data model permanently in sync.

# Syntax

```
@@{address}/schema/{typeName}
@@{address}/schema/         (current node's schema — self-reference)
```

> **Legacy syntax:** The colon form (`schema:TypeName`) is still supported for backward compatibility.

# Common References

| Reference | What it embeds |
|-----------|----------------|
| `@@schema:` | Schema of the current node (self-reference) |
| `@@Doc/DataMesh/UnifiedPath/schema:MeshNode` | Built-in `MeshNode` schema |
| `@@Doc/DataMesh/UnifiedPath/schema:CustomType` | Any registered custom type |

# Embedding the MeshNode Schema

The following reference embeds the full schema for the built-in `MeshNode` type:

```
@@Doc/DataMesh/UnifiedPath/schema:MeshNode
```

**Live result:**

@@../schema:MeshNode

# Embedding a Self-Schema

Use an empty type name to embed the schema of the node you are currently editing. This is useful for NodeType documentation pages that need to describe their own shape:

```
@@Doc/DataMesh/UnifiedPath/SchemaPrefix/schema:
```

**Live result:**

@@schema:

# How Schemas Are Rendered

An embedded schema is rendered as a structured JSON Schema block containing:

- A property table listing each field with its type
- Required / optional indicators
- Default values where defined
- Inline expansion of nested types
