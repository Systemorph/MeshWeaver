---
Name: Schema Prefix
Category: Documentation
Description: Embed live type schemas and data models directly into documentation
Icon: /static/DocContent/DataMesh/UnifiedPath/SchemaPrefix/icon.svg
---

The `schema/` prefix lets you embed a type's JSON Schema definition directly into any node. Rather than maintaining separate schema documentation by hand, a single reference pulls the authoritative schema from the type itself — keeping your docs and your data model permanently in sync.
<svg viewBox="0 0 760 200" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="sarr" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity="0.6"/>
    </marker>
  </defs>
  <rect x="10" y="70" width="160" height="60" rx="10" fill="#1e88e5"/>
  <text x="90" y="96" text-anchor="middle" fill="#fff" font-weight="bold">@@addr/schema/</text>
  <text x="90" y="114" text-anchor="middle" fill="#fff" font-size="11">TypeName</text>
  <line x1="170" y1="100" x2="225" y2="100" stroke="currentColor" stroke-opacity="0.6" stroke-width="1.5" marker-end="url(#sarr)"/>
  <rect x="227" y="70" width="150" height="60" rx="10" fill="#5c6bc0"/>
  <text x="302" y="96" text-anchor="middle" fill="#fff" font-weight="bold">Schema</text>
  <text x="302" y="114" text-anchor="middle" fill="#fff" font-size="11">Resolver</text>
  <line x1="377" y1="100" x2="432" y2="100" stroke="currentColor" stroke-opacity="0.6" stroke-width="1.5" marker-end="url(#sarr)"/>
  <rect x="434" y="70" width="150" height="60" rx="10" fill="#43a047"/>
  <text x="509" y="96" text-anchor="middle" fill="#fff" font-weight="bold">Type</text>
  <text x="509" y="114" text-anchor="middle" fill="#fff" font-size="11">Registry</text>
  <line x1="584" y1="100" x2="639" y2="100" stroke="currentColor" stroke-opacity="0.6" stroke-width="1.5" marker-end="url(#sarr)"/>
  <rect x="641" y="55" width="109" height="40" rx="8" fill="#f57c00"/>
  <text x="695" y="71" text-anchor="middle" fill="#fff" font-weight="bold">Property</text>
  <text x="695" y="87" text-anchor="middle" fill="#fff" font-size="11">table</text>
  <rect x="641" y="105" width="109" height="40" rx="8" fill="#8e24aa"/>
  <text x="695" y="121" text-anchor="middle" fill="#fff" font-weight="bold">Types &amp;</text>
  <text x="695" y="137" text-anchor="middle" fill="#fff" font-size="11">defaults</text>
  <line x1="639" y1="100" x2="626" y2="75" stroke="currentColor" stroke-opacity="0.35" stroke-width="1" marker-end="url(#sarr)"/>
  <line x1="639" y1="100" x2="626" y2="125" stroke="currentColor" stroke-opacity="0.35" stroke-width="1" marker-end="url(#sarr)"/>
  <text x="90" y="158" text-anchor="middle" fill="currentColor" fill-opacity="0.55" font-size="11">reference in doc</text>
  <text x="302" y="158" text-anchor="middle" fill="currentColor" fill-opacity="0.55" font-size="11">looks up type</text>
  <text x="509" y="158" text-anchor="middle" fill="currentColor" fill-opacity="0.55" font-size="11">returns live schema</text>
  <text x="695" y="158" text-anchor="middle" fill="currentColor" fill-opacity="0.55" font-size="11">rendered output</text>
</svg>
*Schema prefix resolution: the reference is resolved at render time against the live type registry, so documentation always reflects the current data model.*

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
