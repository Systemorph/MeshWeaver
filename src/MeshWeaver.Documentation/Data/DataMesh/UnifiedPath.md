---
Name: Unified Path
Category: Documentation
Description: Reference and embed content using @ and @@ syntax
Icon: /static/DocContent/DataMesh/UnifiedPath/icon.svg
---

Unified Path allows you to **reference** and **embed** content from anywhere in your MeshWeaver application using a simple `@` notation.

# Path Resolution

Paths can be **relative** or **absolute**:

| Style | Syntax | Example (from `Doc/DataMesh/CRUD`) |
|-------|--------|------------------------------------|
| **Child** | `ChildName` | `SubPage` → `Doc/DataMesh/CRUD/SubPage` |
| **Sibling** | `../SiblingName` | `../QuerySyntax` → `Doc/DataMesh/QuerySyntax` |
| **Parent's sibling** | `../../Name` | `../../GUI/Editor` → `Doc/GUI/Editor` |
| **Absolute** | `/Full/Path` | `/Doc/GUI/Editor` → `Doc/GUI/Editor` |

Relative paths resolve from the **current node** (treating it as a container). Use `../` to navigate up. Absolute paths start with `/`.

This applies to both `@`/`@@` references and standard markdown links `[text](path)`.

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
| `layoutAreas:` | List available layout areas (reports, views, charts) |

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

---

# Understanding Unified Path Syntax

## Without Prefix = Layout Area

When you use `@path` or `@@path` **without a prefix**, it refers to a **layout area** of the target node:

```
@../Thumbnail                   --> navigates to sibling node's Thumbnail area
@@Thumbnail                     --> embeds child's Thumbnail area inline
@../../DataMesh/Thumbnail       --> navigate up and across to another node
```

## With Prefix = Specific Resource Type

Reserved keywords (`data:`, `schema:`, `area:`) access specific resource types.
Any other prefix (like `content:`, `assets:`, `files:`) accesses files from a content collection:

```
@@node/content:file.md    --> embeds file from "content" collection
@@node/assets:logo.svg    --> embeds file from "assets" collection
@@node/data:              --> embeds the node's Content data as JSON
@@node/schema:            --> embeds the ContentType schema
```

## Content Collections

Any prefix that is **not a reserved keyword** is treated as a content collection name. The collection must be configured in the hub setup using `AddFileSystemContentCollection`, `MapContentCollection`, or similar methods.

```csharp
// Example hub configuration with content collections
config.AddFileSystemContentCollection("content", sp => "./content")
      .AddFileSystemContentCollection("assets", sp => "./assets")
      .MapContentCollection("avatars", "storage", "persons/avatars");
```

This allows flexible naming of content collections to match your project structure.

---

# Quick Examples

## 1. Inline Image (content:)

**Syntax** (relative to this node):
```
@@content:logo.svg
```

**Result:**

@@content:logo.svg

---

## 2. Embedded Markdown (content:)

**Syntax** (relative to this node):
```
@@content:sample.md
```

**Result:**

@@content:sample.md

---

## 3. Layout Area (Thumbnail)

**Syntax** (relative child):
```
@@Thumbnail
```

**Result:**

@@Thumbnail

---

## 4. Data Entity (Self)

**Syntax** (own data):
```
@@data:
```

**Result:**

@@data:

---

## 5. Schema (Self)

**Syntax** (own schema):
```
@@schema:
```

**Result:**

@@schema:

---

## 6. Hyperlinks

**Syntax** (sibling and child references):
```
@../QuerySyntax
@Syntax
```

**Result:**

@../QuerySyntax

@Syntax

---

# Detailed Documentation

@Syntax

@ContentPrefix

@DataPrefix

@AreaPrefix

@SchemaPrefix

---

# Autocomplete

Type `@` in any editor to see suggestions. Autocomplete is **case-insensitive**.

1. `@` - Shows available namespaces
2. `@Doc` - Filters to matches like `Doc`
3. `@Doc/DataMesh/` - Shows child nodes
