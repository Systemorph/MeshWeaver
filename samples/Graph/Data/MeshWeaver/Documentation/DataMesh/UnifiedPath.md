---
Name: Unified Path
Category: Documentation
Description: Reference and embed content using @ and @@ syntax
Icon: /static/storage/content/MeshWeaver/Documentation/DataMesh/UnifiedPath/icon.svg
---

# Unified Path

Unified Path allows you to **reference** and **embed** content from anywhere in your MeshWeaver application using a simple `@` notation.

## Pattern

```
{address}/{prefix}:{path}
```

| Component | Description |
|-----------|-------------|
| `address` | MeshNode path (resolved via MeshCatalog) |
| `prefix` | Either a **reserved keyword** or a **content collection name** |
| `path` | Resource within the address |

### Reserved Keywords

These prefixes have special meaning and map to specific layout areas:

| Prefix | Description |
|--------|-------------|
| `data:` | Access the node's Content data as JSON |
| `schema:` | Access the ContentType schema |
| `model:` | Access the data model |
| `metadata:` | Access MeshNode without Content |
| `area:` | Access a specific layout area |

### Content Collection Prefixes

**Any other prefix** is treated as a **content collection name**. The collection is configured in the hub setup:

| Example | Description |
|---------|-------------|
| `content:` | Files from the "content" collection |
| `assets:` | Files from the "assets" collection |
| `files:` | Files from the "files" collection |
| `docs:` | Files from the "docs" collection |

## @ vs @@

| Syntax | Behavior |
|--------|----------|
| `@path` | **Hyperlink** - navigates to content |
| `@@path` | **Inline** - embeds content in place |

**Important:** References must be at the **start of a line**.

---

## Understanding Unified Path Syntax

### Without Prefix = Layout Area

When you use `@path` or `@@path` **without a prefix**, it refers to a **layout area** of the target node:

```
@Systemorph/Thumbnail     --> navigates to Systemorph node's Thumbnail area
@@Systemorph/Thumbnail    --> embeds Systemorph node's Thumbnail area inline
```

### With Prefix = Specific Resource Type

Reserved keywords (`data:`, `schema:`, `metadata:`, `area:`) access specific resource types.
Any other prefix (like `content:`, `assets:`, `files:`) accesses files from a content collection:

```
@@node/content:file.md    --> embeds file from "content" collection
@@node/assets:logo.svg    --> embeds file from "assets" collection
@@node/data:              --> embeds the node's Content data as JSON
@@node/schema:            --> embeds the ContentType schema
@@node/metadata:          --> embeds MeshNode without Content
```

### Content Collections

Any prefix that is **not a reserved keyword** is treated as a content collection name. The collection must be configured in the hub setup using `AddFileSystemContentCollection`, `MapContentCollection`, or similar methods.

```csharp
// Example hub configuration with content collections
config.AddFileSystemContentCollection("content", sp => "./content")
      .AddFileSystemContentCollection("assets", sp => "./assets")
      .MapContentCollection("avatars", "storage", "persons/avatars");
```

This allows flexible naming of content collections to match your project structure.

---

## Quick Examples

### 1. Inline Image (content:)

**Syntax:**
```
@@MeshWeaver/Documentation/DataMesh/UnifiedPath/content:logo.svg
```

**Result:**

@@MeshWeaver/Documentation/DataMesh/UnifiedPath/content:logo.svg

---

### 2. Embedded Markdown (content:)

**Syntax:**
```
@@MeshWeaver/Documentation/DataMesh/UnifiedPath/content:sample.md
```

**Result:**

@@MeshWeaver/Documentation/DataMesh/UnifiedPath/content:sample.md

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
@@MeshWeaver/Documentation/DataMesh/UnifiedPath/data:
```

**Result:**

@@MeshWeaver/Documentation/DataMesh/UnifiedPath/data:

---

### 5. Schema (Self)

**Syntax:**
```
@@MeshWeaver/Documentation/DataMesh/UnifiedPath/schema:
```

**Result:**

@@MeshWeaver/Documentation/DataMesh/UnifiedPath/schema:

---

### 6. Metadata (MeshNode without Content)

**Syntax:**
```
@@MeshWeaver/Documentation/DataMesh/UnifiedPath/metadata:
```

Returns the MeshNode with `Content` set to null, reducing payload size when you only need node metadata (Path, NodeType, etc.).

**Result:**

@@MeshWeaver/Documentation/DataMesh/UnifiedPath/metadata:

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

@MeshWeaver/Documentation/DataMesh/UnifiedPath/Syntax

@MeshWeaver/Documentation/DataMesh/UnifiedPath/ContentPrefix

@MeshWeaver/Documentation/DataMesh/UnifiedPath/DataPrefix

@MeshWeaver/Documentation/DataMesh/UnifiedPath/AreaPrefix

@MeshWeaver/Documentation/DataMesh/UnifiedPath/SchemaPrefix

---

## Autocomplete

Type `@` in any editor to see suggestions. Autocomplete is **case-insensitive**.

1. `@` - Shows available namespaces
2. `@Sys` - Filters to matches like `Systemorph`
3. `@Systemorph/` - Shows child nodes
