---
Name: Unified Content References
Category: Documentation
Description: Reference and embed content using @ and @@ syntax
Icon: /static/storage/content/MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/icon.svg
---

# Unified Content References (UCR)

UCR allows you to **reference** and **embed** content from anywhere in your MeshWeaver application using a simple `@` notation.

## Pattern

```
{address}/{prefix}:{path}
```

| Component | Description |
|-----------|-------------|
| `address` | MeshNode path (resolved via MeshCatalog) |
| `prefix` | Content type: `content:`, `data:`, `area:`, `schema:`, `model:`, `metadata:` |
| `path` | Resource within the address |

## @ vs @@

| Syntax | Behavior |
|--------|----------|
| `@path` | **Hyperlink** - navigates to content |
| `@@path` | **Inline** - embeds content in place |

**Important:** References must be at the **start of a line**.

---

## Quick Examples

### 1. Inline Image (content:)

**Syntax:**
```
@@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/content:logo.svg
```

**Result:**

@@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/content:logo.svg

---

### 2. Embedded Markdown (content:)

**Syntax:**
```
@@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/content:sample.md
```

**Result:**

@@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/content:sample.md

---

### 3. Layout Area (Thumbnail)

**Syntax:**
```
@@Systemorph/Thumbnail
```

**Result:**

@@Systemorph/Thumbnail

---

### 4. Data Entity (Self)

**Syntax:**
```
@@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/data:
```

**Result:**

@@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/data:

---

### 5. Schema (Self)

**Syntax:**
```
@@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/schema:
```

**Result:**

@@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/schema:

---

### 6. Metadata (MeshNode without Content)

**Syntax:**
```
@@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/metadata:
```

Returns the MeshNode with `Content` set to null, reducing payload size when you only need node metadata (Path, NodeType, etc.).

**Result:**

@@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/metadata:

---

### 7. Hyperlinks

**Syntax:**
```
@Systemorph/Marketing
@ACME/ProductLaunch
```

**Result:**

@Systemorph/Marketing

@ACME/ProductLaunch

---

## Detailed Documentation

@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/Syntax

@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/ContentPrefix

@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/DataPrefix

@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/AreaPrefix

@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/SchemaPrefix

---

## Autocomplete

Type `@` in any editor to see suggestions. Autocomplete is **case-insensitive**.

1. `@` - Shows available namespaces
2. `@Sys` - Filters to matches like `Systemorph`
3. `@Systemorph/` - Shows child nodes
