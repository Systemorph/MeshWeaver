---
Name: Data Prefix
Category: Documentation
Description: Embed live data collections and individual entities directly in any document
Icon: /static/DocContent/DataMesh/UnifiedPath/DataPrefix/icon.svg
---

The `data/` prefix lets you embed live data directly into any document — a full collection rendered as a grid, a single entity shown in detail, or a self-reference listing all available collections on the current node.

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
@@Doc/DataMesh/UnifiedPath/DataPrefix/data:
```

**Result:**

@@data:

---

## Example: Parent collection reference

You can walk up the path hierarchy to embed collections from a parent or sibling node:

**Syntax:**
```
@Doc/DataMesh/UnifiedPath/data:
```

**Result:**

@../data:

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
