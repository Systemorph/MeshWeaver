---
Name: Data Prefix
Category: Documentation
Description: Embed live data collections and individual entities directly in any document
Icon: /static/DocContent/DataMesh/UnifiedPath/DataPrefix/icon.svg
---

The `data/` prefix lets you embed live data directly into any document — a full collection rendered as a grid, a single entity shown in detail, or a self-reference listing all available collections on the current node.
<svg viewBox="0 0 760 280" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="darr" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L8,3.5 L0,7 Z" fill="currentColor" fill-opacity=".55"/>
    </marker>
  </defs>
  <rect x="0" y="0" width="760" height="280" rx="12" fill="currentColor" fill-opacity=".04" stroke="currentColor" stroke-opacity=".1" stroke-width="1"/>
  <text x="380" y="26" text-anchor="middle" font-size="14" font-weight="bold" fill="currentColor" fill-opacity=".75">data/ Prefix — Three Reference Forms</text>
  <rect x="30" y="48" width="230" height="52" rx="10" fill="#5c6bc0"/>
  <text x="145" y="70" text-anchor="middle" font-weight="bold" fill="#fff" font-size="13">@@node/data/Products</text>
  <text x="145" y="88" text-anchor="middle" fill="#dce0ff" font-size="11">collection reference</text>
  <rect x="30" y="116" width="230" height="52" rx="10" fill="#1e88e5"/>
  <text x="145" y="138" text-anchor="middle" font-weight="bold" fill="#fff" font-size="12">@@node/data/Products/p-42</text>
  <text x="145" y="156" text-anchor="middle" fill="#c8e4ff" font-size="11">single-entity reference</text>
  <rect x="30" y="184" width="230" height="52" rx="10" fill="#26a69a"/>
  <text x="145" y="206" text-anchor="middle" font-weight="bold" fill="#fff" font-size="13">@@node/data/</text>
  <text x="145" y="224" text-anchor="middle" fill="#ccefec" font-size="11">self-reference (empty path)</text>
  <line x1="260" y1="74" x2="360" y2="74" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#darr)"/>
  <line x1="260" y1="142" x2="360" y2="142" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#darr)"/>
  <line x1="260" y1="210" x2="360" y2="210" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#darr)"/>
  <rect x="360" y="48" width="190" height="52" rx="10" fill="#5c6bc0" fill-opacity=".25" stroke="#5c6bc0" stroke-opacity=".6" stroke-width="1.5"/>
  <text x="455" y="70" text-anchor="middle" font-weight="bold" fill="currentColor" fill-opacity=".85" font-size="12">Data grid</text>
  <text x="455" y="88" text-anchor="middle" fill="currentColor" fill-opacity=".6" font-size="11">all items in collection</text>
  <rect x="360" y="116" width="190" height="52" rx="10" fill="#1e88e5" fill-opacity=".2" stroke="#1e88e5" stroke-opacity=".6" stroke-width="1.5"/>
  <text x="455" y="138" text-anchor="middle" font-weight="bold" fill="currentColor" fill-opacity=".85" font-size="12">Detail view</text>
  <text x="455" y="156" text-anchor="middle" fill="currentColor" fill-opacity=".6" font-size="11">single entity</text>
  <rect x="360" y="184" width="190" height="52" rx="10" fill="#26a69a" fill-opacity=".2" stroke="#26a69a" stroke-opacity=".6" stroke-width="1.5"/>
  <text x="455" y="206" text-anchor="middle" font-weight="bold" fill="currentColor" fill-opacity=".85" font-size="12">Collection list</text>
  <text x="455" y="224" text-anchor="middle" fill="currentColor" fill-opacity=".6" font-size="11">all available collections</text>
  <line x1="550" y1="74" x2="640" y2="74" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#darr)"/>
  <line x1="550" y1="142" x2="640" y2="142" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#darr)"/>
  <line x1="550" y1="210" x2="640" y2="210" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#darr)"/>
  <rect x="640" y="48" width="102" height="52" rx="10" fill="#f57c00"/>
  <text x="691" y="70" text-anchor="middle" font-weight="bold" fill="#fff" font-size="12">Live grid</text>
  <text x="691" y="88" text-anchor="middle" fill="#ffe8cc" font-size="11">in document</text>
  <rect x="640" y="116" width="102" height="52" rx="10" fill="#8e24aa"/>
  <text x="691" y="138" text-anchor="middle" font-weight="bold" fill="#fff" font-size="12">Live card</text>
  <text x="691" y="156" text-anchor="middle" fill="#f0d6ff" font-size="11">in document</text>
  <rect x="640" y="184" width="102" height="52" rx="10" fill="#43a047"/>
  <text x="691" y="206" text-anchor="middle" font-weight="bold" fill="#fff" font-size="12">Browsable</text>
  <text x="691" y="224" text-anchor="middle" fill="#c8f5cb" font-size="11">index page</text>
  <text x="380" y="265" text-anchor="middle" font-size="11" fill="currentColor" fill-opacity=".5">Data is fetched live from the node's workspace — the embedded view always reflects current state</text>
</svg>
*Three reference forms of the `data/` prefix: collection, single-entity, and self-reference — each resolves to a different live embedded view.*

## Syntax

```
@@{address}/data/{collection}[/{entityId}]
@@{address}/data/
```

> **Note:** The legacy colon syntax (`data:collection`) is still supported for backward compatibility.

## Reference components

| Component | Description | Example |
|-----------|-------------|---------|
| `collection` | Name of the data collection to embed | `Posts`, `Users`, `Products` |
| `entityId` | *(Optional)* A specific entity within that collection | `post-123`, `user-456` |
| *(empty)* | Self-reference — shows all available collections on the node | |

## Rendering behaviour

The prefix resolves to one of three views depending on how much path you supply:

| Reference type | What renders |
|----------------|--------------|
| Collection reference | Data grid with all items in the collection |
| Single-entity reference | Detail view for that entity |
| Empty (self-reference) | Browsable list of all available collections |

Data is fetched live from the node's workspace using registered data providers, so the embedded view always reflects the current state.

---

## Example: Self-reference

An empty path after `data:` is a self-reference — it surfaces all data collections registered on the current node.

**Syntax:**
```
@@Doc/DataMesh/UnifiedPath/DataPrefix/data/
```

**Result:**

@@data/

---

## Example: Parent collection reference

You can walk up the path hierarchy to embed collections from a parent or sibling node:

**Syntax:**
```
@Doc/DataMesh/UnifiedPath/data/
```

**Result:**

@../data/

---

## Live example

The cell below demonstrates how data references are described at runtime — it renders a simple summary table of the three reference forms and their output shapes:

```csharp --render DataPrefixDemo --show-code
var rows = new[]
{
    ("@@node/data/Products",       "All Products",         "Data grid"),
    ("@@node/data/Products/p-42",  "Product p-42",         "Detail view"),
    ("@@node/data/",               "(self-reference)",      "Collection list"),
};

var table = string.Join("\n", rows.Select(r =>
    $"| `{r.Item1}` | {r.Item2} | {r.Item3} |"));

MeshWeaver.Layout.Controls.Markdown($"""
| Reference | Resolves to | Renders as |
|-----------|-------------|------------|
{table}
""")
```
