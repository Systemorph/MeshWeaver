---
Name: Unified Path Syntax
Category: Documentation
Description: How the address/prefix/path pattern works — and when to use @ vs @@
Icon: /static/DocContent/DataMesh/UnifiedPath/Syntax/icon.svg
---

Every content reference in MeshWeaver follows one compact pattern:

```
{address}/{prefix}/{path}
```

Three parts, one slash between each. Once you know what each part does, you can construct any reference by inspection.

> **Backward compatibility.** The legacy `{prefix}:{path}` colon format is still accepted wherever Unified Paths are parsed.

---

## The Three Parts

| Part | What it identifies | Example |
|------|--------------------|---------|
| `address` | A MeshNode in the catalog, resolved by best-match scoring | `Doc/DataMesh` |
| `prefix` | The kind of content you want from that node | `content/`, `data/`, `area/` … |
| `path` | The specific resource within the node | `docs/readme.md`, `Posts`, `Thumbnail` |

### Address Resolution

The address segment is not a hard-coded node ID — it is resolved dynamically against the MeshCatalog using score-based matching:

1. The path is split into segments.
2. Each segment is matched against registered namespace patterns.
3. The highest-scoring match identifies the target node.
4. Any remaining segments become the `path` portion.

This means short, human-readable addresses like `Doc/DataMesh` resolve correctly even as the catalog grows.

---

## Prefix Reference

The prefix tells MeshWeaver how to interpret the resource at the resolved address.

| Prefix | Purpose | Rendered as |
|--------|---------|-------------|
| `content/` | Static files — images, markdown, attachments | File content, inline |
| `data/` | Data collections or entities | Data grid or entity view |
| `area/` | Layout areas | Layout component |
| `schema/` | Type schemas | JSON schema code block |
| `model/` | Data model diagrams | Mermaid class diagram |
| `menu/` | Menu structure | Menu items |
| *(none)* | Default area reference | Layout component |

---

## @ vs @@

The leading `@` character controls whether the reference becomes a **link** or an **inline embed**.

| Syntax | Behavior |
|--------|----------|
| `@path` | Creates a **hyperlink** to the referenced content |
| `@@path` | **Embeds** the content inline at that point in the document |

Use `@` when you want the reader to navigate to the content. Use `@@` when the content should appear directly in the page — for thumbnails, summaries, or reusable snippets.

---

## Examples

### Simple Reference (hyperlink)

```
@Doc/DataMesh/QuerySyntax
```

Result:

@../../QuerySyntax

### Embedded Content

```
@@Doc/DataMesh/UnifiedPath/Thumbnail
```

Result:

@@../Thumbnail

---

## Related Pages

@../ContentPrefix

@../DataPrefix

@../AreaPrefix
