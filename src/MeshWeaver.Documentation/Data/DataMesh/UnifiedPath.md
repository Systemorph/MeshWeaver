---
Name: Unified Path
Category: Documentation
Description: Reference and embed any mesh content using the @ and @@ notation
Icon: /static/DocContent/DataMesh/UnifiedPath/icon.svg
---

Unified Path gives you a single, consistent way to **reference** or **embed** anything in your MeshWeaver application — images, markdown files, layout areas, data schemas, and more — using a compact `@` notation that works in markdown, autocomplete, and agent tool calls.

<svg viewBox="0 0 760 260" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity=".55"/>
    </marker>
  </defs>
  <rect x="0" y="0" width="760" height="260" rx="12" fill="currentColor" fill-opacity=".04"/>
  <text x="380" y="26" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="bold" fill="currentColor" fill-opacity=".7">Unified Path Anatomy</text>
  <text x="380" y="52" text-anchor="middle" font-family="sans-serif" font-size="15" font-weight="bold" fill="#fff" fill-opacity=".9" letter-spacing="1">@address / prefix / path</text>
  <rect x="40" y="72" width="180" height="44" rx="10" fill="#1e88e5"/>
  <text x="130" y="89" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff">address</text>
  <text x="130" y="107" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff" fill-opacity=".85">MeshNode path</text>
  <rect x="290" y="72" width="180" height="44" rx="10" fill="#43a047"/>
  <text x="380" y="89" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff">prefix</text>
  <text x="380" y="107" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff" fill-opacity=".85">reserved keyword or collection</text>
  <rect x="540" y="72" width="180" height="44" rx="10" fill="#f57c00"/>
  <text x="630" y="89" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff">path</text>
  <text x="630" y="107" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff" fill-opacity=".85">specific resource</text>
  <line x1="220" y1="94" x2="286" y2="94" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="470" y1="94" x2="536" y2="94" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="40" y="148" width="320" height="88" rx="10" fill="currentColor" fill-opacity=".07" stroke="#1e88e5" stroke-opacity=".5" stroke-width="1.2"/>
  <text x="200" y="168" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="#1e88e5">@ — Hyperlink (navigate)</text>
  <text x="200" y="188" text-anchor="middle" font-family="sans-serif" font-size="11" fill="currentColor" fill-opacity=".75">@Doc/DataMesh/QuerySyntax</text>
  <text x="200" y="206" text-anchor="middle" font-family="sans-serif" font-size="11" fill="currentColor" fill-opacity=".55">Opens the target node in the browser</text>
  <text x="200" y="224" text-anchor="middle" font-family="sans-serif" font-size="11" fill="currentColor" fill-opacity=".55">Works in markdown links and autocomplete</text>
  <rect x="400" y="148" width="320" height="88" rx="10" fill="currentColor" fill-opacity=".07" stroke="#f57c00" stroke-opacity=".5" stroke-width="1.2"/>
  <text x="560" y="168" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="#f57c00">@@ — Embed (render inline)</text>
  <text x="560" y="188" text-anchor="middle" font-family="sans-serif" font-size="11" fill="currentColor" fill-opacity=".75">@@content/logo.svg</text>
  <text x="560" y="206" text-anchor="middle" font-family="sans-serif" font-size="11" fill="currentColor" fill-opacity=".55">Renders content directly in the page</text>
  <text x="560" y="224" text-anchor="middle" font-family="sans-serif" font-size="11" fill="currentColor" fill-opacity=".55">Images, markdown, layout areas, data</text>
</svg>

*Unified Path anatomy: every `@` or `@@` reference is composed of an address, a prefix, and a resource path.*

---

# Path Resolution

Every `@` reference resolves to a mesh node path. Paths can be **relative** (to the current node) or **absolute**.

| Style | Syntax | Example from `Doc/DataMesh/CRUD` |
|---|---|---|
| **Child** | `ChildName` | `SubPage` → `Doc/DataMesh/CRUD/SubPage` |
| **Sibling** | `../SiblingName` | `../QuerySyntax` → `Doc/DataMesh/QuerySyntax` |
| **Parent's sibling** | `../../Name` | `../../GUI/Editor` → `Doc/GUI/Editor` |
| **Absolute** | `/Full/Path` | `/Doc/GUI/Editor` → `Doc/GUI/Editor` |

Relative paths treat the current node as a container — use `../` to navigate up. Absolute paths start with `/`.

These rules apply equally to `@`/`@@` references and to standard markdown links `[text](path)`.

---

# Pattern

Every Unified Path follows this structure:

```
{address}/{prefix}/{path}
```

| Component | Description |
|---|---|
| `address` | The MeshNode path, resolved via MeshCatalog |
| `prefix` | A **reserved keyword** or a **content collection name** |
| `path` | The specific resource within that address |

> **Compatibility note:** The legacy `{prefix}:{path}` colon format is still supported for backward compatibility. The slash form is preferred for new content.

## Reserved Keywords

These prefixes have built-in meaning and map to specific layout areas:

| Prefix | What it accesses |
|---|---|
| `data/` | The node's Content data as JSON |
| `schema/` | The ContentType schema |
| `model/` | The data model |
| `area/` | A named layout area |
| `content/` | Files from the "content" collection |
| `menu/` | The menu structure |

## Content Collection Prefixes

Any prefix that is **not** a reserved keyword is treated as a content collection name. Collections store files (images, documents, markdown, etc.) associated with a mesh node.

| Example prefix | Description |
|---|---|
| `content/` | The "content" collection |
| `assets/` | The "assets" collection |
| `files/` | The "files" collection |

Collections are registered in hub setup using `AddFileSystemContentCollection`, `MapContentCollection`, or similar methods:

```csharp
config.AddFileSystemContentCollection("content", sp => "./content")
      .AddFileSystemContentCollection("assets", sp => "./assets")
      .MapContentCollection("avatars", "storage", "persons/avatars");
```

---

# `@` vs `@@` — Navigate or Embed

Two operators, one simple distinction:

| Syntax | Behavior |
|---|---|
| `@path` | **Hyperlink** — navigates to the content |
| `@@path` | **Inline embed** — renders the content in place |

> **Important:** References must appear at the **start of a line**.

---

# ⚠️ `@/` Is Local-Only — Never Use It in URLs or `href` Attributes

The `@` prefix is a **Unified Content Reference** — it exists inside markdown, autocomplete, and agent tool calls. It is **never** part of an HTTP URL or an HTML `href` attribute.

| Context | Correct | Wrong |
|---|---|---|
| Native markdown link | `[Reinsurance](@/Systemorph/Reinsurance)` | — *(Markdig strips the `@` automatically)* |
| Raw HTML inside markdown | `<a href="/Systemorph/Reinsurance">` | `<a href="@/Systemorph/Reinsurance">` |
| HTTP URL / shared link | `https://memex.meshweaver.cloud/Systemorph/Reinsurance` | `https://memex.meshweaver.cloud/@/Systemorph/Reinsurance` |
| Agent tool call | `Get('@/Systemorph/Reinsurance')` | — |
| Autocomplete search | `@Syst…` | — |

**Why it matters:** Markdig's `LinkUrlCleanupExtension` strips the leading `@` from `[text](@/X)` and resolves it into a proper `/X` URL at render time. That extension does **not** reach inside raw HTML — any `<a href="@/X">` in an HTML block passes through verbatim, producing a broken `https://host/@/X` link.

**Safety net:** The portal registers a redirect middleware that permanently redirects `GET /@/X` → `GET /X` (301). Broken links will still navigate correctly, but fix the source whenever you spot `@/` inside an `href`.

---

# Syntax in Practice

## No Prefix → Layout Area

When you use `@path` or `@@path` **without a prefix**, it refers to a **layout area** of the target node:

```
@../Thumbnail              navigates to the sibling node's Thumbnail area
@@Thumbnail                embeds a child's Thumbnail area inline
@../../DataMesh/Thumbnail  navigates up and across to another node
```

## With Prefix → Specific Resource Type

Reserved keywords let you target a precise resource type on any node:

```
@@node/content/file.md    embeds a file from the "content" collection
@@node/data/              embeds the node's Content data as JSON
@@node/schema/            embeds the ContentType schema
@@node/area/Details       embeds a named layout area
```

---

# Quick Examples

## 1. Inline Image

```
@@content/logo.svg
```

@@content/logo.svg

---

## 2. Embedded Markdown

```
@@content/sample.md
```

@@content/sample.md

---

## 3. Layout Area (Thumbnail)

```
@@Thumbnail
```

@@Thumbnail

---

## 4. Node Data (Self)

```
@@data/
```

@@data/

---

## 5. Schema (Self)

```
@@schema/
```

@@schema/

---

## 6. Hyperlinks

```
@../QuerySyntax
@Syntax
```

@../QuerySyntax

@Syntax

---

# Autocomplete

Type `@` in any editor to trigger path suggestions. Autocomplete is **case-insensitive**.

| Input | What you see |
|---|---|
| `@` | Available namespaces |
| `@Doc` | Nodes matching `Doc` |
| `@Doc/DataMesh/` | Child nodes under `Doc/DataMesh` |

---

# Detailed Documentation

@Syntax

@ContentPrefix

@DataPrefix

@AreaPrefix

@SchemaPrefix
