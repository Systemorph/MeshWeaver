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
<svg viewBox="0 0 760 300" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="sarr" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity=".55"/>
    </marker>
  </defs>
  <rect x="0" y="0" width="760" height="300" rx="12" fill="currentColor" fill-opacity=".04"/>
  <text x="380" y="24" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="bold" fill="currentColor" fill-opacity=".7">Address Resolution Pipeline</text>
  <text x="380" y="46" text-anchor="middle" font-family="sans-serif" font-size="12" fill="currentColor" fill-opacity=".5">Input: Doc/DataMesh/QuerySyntax</text>
  <rect x="30" y="64" width="152" height="50" rx="10" fill="#5c6bc0"/>
  <text x="106" y="84" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff">1. Split segments</text>
  <text x="106" y="102" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#fff" fill-opacity=".85">Doc / DataMesh / QuerySyntax</text>
  <line x1="182" y1="89" x2="208" y2="89" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#sarr)"/>
  <rect x="210" y="64" width="152" height="50" rx="10" fill="#1e88e5"/>
  <text x="286" y="84" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff">2. Match namespaces</text>
  <text x="286" y="102" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#fff" fill-opacity=".85">Score each registered pattern</text>
  <line x1="362" y1="89" x2="388" y2="89" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#sarr)"/>
  <rect x="390" y="64" width="152" height="50" rx="10" fill="#43a047"/>
  <text x="466" y="84" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff">3. Best-match wins</text>
  <text x="466" y="102" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#fff" fill-opacity=".85">Highest score → target node</text>
  <line x1="542" y1="89" x2="568" y2="89" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#sarr)"/>
  <rect x="570" y="64" width="160" height="50" rx="10" fill="#f57c00"/>
  <text x="650" y="84" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff">4. Remainder → path</text>
  <text x="650" y="102" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#fff" fill-opacity=".85">Leftover segments = resource path</text>
  <line x1="286" y1="114" x2="286" y2="148" stroke="currentColor" stroke-opacity=".35" stroke-width="1.2" stroke-dasharray="4 3"/>
  <line x1="466" y1="114" x2="466" y2="148" stroke="currentColor" stroke-opacity=".35" stroke-width="1.2" stroke-dasharray="4 3"/>
  <line x1="650" y1="114" x2="650" y2="148" stroke="currentColor" stroke-opacity=".35" stroke-width="1.2" stroke-dasharray="4 3"/>
  <rect x="210" y="148" width="152" height="44" rx="8" fill="currentColor" fill-opacity=".07" stroke="#1e88e5" stroke-opacity=".45" stroke-width="1.2"/>
  <text x="286" y="166" text-anchor="middle" font-family="sans-serif" font-size="11" fill="currentColor" fill-opacity=".75">Candidates:</text>
  <text x="286" y="182" text-anchor="middle" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".55">Doc, Doc/DataMesh, …</text>
  <rect x="390" y="148" width="152" height="44" rx="8" fill="currentColor" fill-opacity=".07" stroke="#43a047" stroke-opacity=".45" stroke-width="1.2"/>
  <text x="466" y="166" text-anchor="middle" font-family="sans-serif" font-size="11" fill="currentColor" fill-opacity=".75">Winner:</text>
  <text x="466" y="182" text-anchor="middle" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".55">Doc/DataMesh (score 2)</text>
  <rect x="570" y="148" width="160" height="44" rx="8" fill="currentColor" fill-opacity=".07" stroke="#f57c00" stroke-opacity=".45" stroke-width="1.2"/>
  <text x="650" y="166" text-anchor="middle" font-family="sans-serif" font-size="11" fill="currentColor" fill-opacity=".75">Resource path:</text>
  <text x="650" y="182" text-anchor="middle" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".55">QuerySyntax</text>
  <rect x="120" y="230" width="520" height="50" rx="10" fill="currentColor" fill-opacity=".06" stroke="currentColor" stroke-opacity=".15" stroke-width="1"/>
  <text x="380" y="250" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="bold" fill="currentColor" fill-opacity=".7">Resolved reference</text>
  <text x="380" y="268" text-anchor="middle" font-family="sans-serif" font-size="11" fill="currentColor" fill-opacity=".55">node: Doc/DataMesh   |   resource path: QuerySyntax</text>
</svg>

*Address resolution pipeline: the input string is split into segments, scored against registered namespace patterns, and the best match becomes the target node — any leftover segments become the resource path.*

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
