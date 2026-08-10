---
Name: Node Operations
Category: Documentation
Description: Export, import, copy, and move node subtrees — round-trip file formats and programmatic APIs
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="5 9 2 12 5 15"/><polyline points="9 5 12 2 15 5"/><polyline points="15 19 12 22 9 19"/><polyline points="19 9 22 12 19 15"/><line x1="2" y1="12" x2="22" y2="12"/><line x1="12" y1="2" x2="12" y2="22"/></svg>
---

MeshWeaver provides four built-in operations for transferring node subtrees between locations, exporting to files, and importing from external sources. They all appear directly in a node's **context menu** — Move, Copy and Delete in the edit/organize section, Import and Export in the content section (see [Node Menu](/Doc/GUI/NodeMenu)); there is no "Actions" sub-menu.

| Operation | What it does |
|-----------|-------------|
| **Export** | Produces a ZIP archive (`manifest.json` + `files/`) of a node and its entire subtree |
| **Import** | Reads files or a ZIP and creates nodes in the mesh |
| **Copy** | Duplicates a node and all its descendants to a new namespace |
| **Move** | Relocates a node and its entire subtree to a new path |

<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 760 300" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity="0.55"/>
    </marker>
    <marker id="arr-blue" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="#1e88e5"/>
    </marker>
    <marker id="arr-green" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="#43a047"/>
    </marker>
    <marker id="arr-orange" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="#f57c00"/>
    </marker>
    <marker id="arr-purple" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="#8e24aa"/>
    </marker>
  </defs>
  <rect x="285" y="90" width="190" height="120" rx="12" fill="#1565c0" fill-opacity="0.9"/>
  <text x="380" y="118" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff">Mesh Node Subtree</text>
  <rect x="305" y="130" width="60" height="26" rx="5" fill="#fff" fill-opacity="0.15"/>
  <text x="335" y="148" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff">root</text>
  <rect x="295" y="168" width="50" height="22" rx="5" fill="#fff" fill-opacity="0.15"/>
  <text x="320" y="184" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#fff">child1</text>
  <rect x="355" y="168" width="50" height="22" rx="5" fill="#fff" fill-opacity="0.15"/>
  <text x="380" y="184" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#fff">child2</text>
  <line x1="335" y1="156" x2="320" y2="168" stroke="#fff" stroke-opacity="0.4" stroke-width="1"/>
  <line x1="335" y1="156" x2="380" y2="168" stroke="#fff" stroke-opacity="0.4" stroke-width="1"/>
  <rect x="22" y="30" width="130" height="56" rx="10" fill="#43a047"/>
  <text x="87" y="55" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff">Export</text>
  <text x="87" y="74" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#c8e6c9">ZIP (manifest + files)</text>
  <line x1="152" y1="58" x2="280" y2="120" stroke="#43a047" stroke-width="1.8" marker-end="url(#arr-green)" stroke-dasharray="none"/>
  <rect x="22" y="214" width="130" height="56" rx="10" fill="#1e88e5"/>
  <text x="87" y="239" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff">Import</text>
  <text x="87" y="258" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#bbdefb">File / Folder / ZIP</text>
  <line x1="152" y1="242" x2="280" y2="180" stroke="#1e88e5" stroke-width="1.8" marker-end="url(#arr-blue)"/>
  <rect x="608" y="30" width="130" height="56" rx="10" fill="#f57c00"/>
  <text x="673" y="55" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff">Copy</text>
  <text x="673" y="74" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#ffe0b2">→ new namespace</text>
  <line x1="480" y1="120" x2="604" y2="58" stroke="#f57c00" stroke-width="1.8" marker-end="url(#arr-orange)"/>
  <rect x="608" y="214" width="130" height="56" rx="10" fill="#8e24aa"/>
  <text x="673" y="239" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff">Move</text>
  <text x="673" y="258" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#e1bee7">→ new path</text>
  <line x1="480" y1="180" x2="604" y2="242" stroke="#8e24aa" stroke-width="1.8" marker-end="url(#arr-purple)"/>
  <text x="138" y="148" text-anchor="middle" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity="0.55" font-style="italic">reads from</text>
  <text x="138" y="162" text-anchor="middle" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity="0.55" font-style="italic">writes to</text>
  <text x="620" y="148" text-anchor="middle" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity="0.55" font-style="italic">duplicates</text>
  <text x="620" y="162" text-anchor="middle" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity="0.55" font-style="italic">relocates</text>
</svg>

*The four node operations and how they relate to a mesh node subtree: Export serialises to files, Import reads files into the mesh, Copy duplicates to a new namespace, and Move relocates the entire subtree.*

---

# Export

Export serialises a node and its entire subtree into a **self-contained ZIP archive**. The archive has exactly two parts:

| Entry | Contents |
|---|---|
| `manifest.json` | The export-root path plus every descendant `MeshNode` as full JSON. Code source and Markdown bodies ride **inside** each node's `Content` — there are no separate `.cs` / `.md` entries. |
| `files/…` | The raw bytes of every editable content-collection file attached to the exported nodes. |

Subtree traversal uses the same snapshot query Copy uses (`path:{root} scope:subtree`), and the file reads plus the zipping run on the file-system `IIoPool` — never on the hub action block.

## Permission

Export requires `Permission.Export`. Nodes the caller lacks it on are **silently skipped**: partial access yields a subset, no access yields an empty-manifest ZIP. A non-existent path also resolves to an empty-manifest ZIP rather than an error.

## Programmatic Export

The programmatic surface is `MeshOperations` — the same object behind the MCP `export` / `import` tools. It is reactive: the observable is cold, so the work runs on `Subscribe`.

```csharp
var ops = new MeshOperations(hub);   // a facade over the hub, not a DI service

ops.Export("org/acme/project")
   .Subscribe(zipBytes => File.WriteAllBytes(outputPath, zipBytes),
              ex => logger.LogWarning(ex, "Export failed"));
```

> The old `IMeshExportService` / `IMeshImportService` interfaces were **deleted** in the persistence cull (2026-05-12). The in-portal Export and Import *views* have not been rewired onto the new surface yet and currently report that; `MeshOperations.Export` / `.Import` are the working path.

## Export–Import Round Trip

Exporting a subtree and re-importing the ZIP restores the original state:

```
Export org/acme → ZIP (manifest.json + files/)
  → Import the ZIP under a target namespace
  → Node paths are rewritten from the export root to the target;
    names, types, content, and content-collection files are preserved
```

---

# Import

Import reads files from a directory or ZIP and creates nodes in the mesh. Three sources are supported:

| Source | Description |
|--------|-------------|
| **Copy from Mesh Node** | Duplicate an existing node tree within the mesh |
| **Upload File** | Single `.md`, `.json`, `.yaml`, `.csv`, or `.html` file |
| **Upload Folder (ZIP)** | Directory structure or ZIP archive |

Single-file and folder uploads are parsed into `MeshNode`s by `FileFormatParserRegistry`, which picks a parser from the file extension.

## Programmatic Import

`MeshOperations.Import` is the inverse of `MeshOperations.Export`: it unpacks the archive, recreates every node with its path rewritten from the export root to `targetNamespace` (through `IMeshService.CreateNode`, carrying the caller's `AccessContext`), then re-uploads every content-collection file through the standard upload path.

```csharp
var ops = new MeshOperations(hub);   // a facade over the hub, not a DI service

ops.Import("org/acme/project", zipBytes)
   .Subscribe(summary => logger.LogInformation("{Summary}", summary),
              ex => logger.LogWarning(ex, "Import failed"));
// summary is JSON: {status,exportRoot,targetNamespace,nodesImported,filesImported}
```

---

# Copy

Copy duplicates a node and all its descendants to a new namespace. The source node's ID is preserved under the target.

**Example:** Copying `org/acme` to `org/backup` creates:
- `org/backup/acme` (root)
- `org/backup/acme/child1`
- `org/backup/acme/child2`
- … and so on for the full subtree

## Options

- **Force** — overwrite existing nodes at the destination (default: skip existing)

## Programmatic Copy

`CopyNodeTree` is reactive and cold — the copy runs on `Subscribe`, and the single emission is the count of upserted nodes.

```csharp
NodeCopyHelper.CopyNodeTree(
        meshService, meshService, hub,
        sourcePath: "org/acme",
        targetNamespace: "org/backup",
        force: false)
    .Subscribe(nodesCopied => logger.LogInformation("Copied {Count} nodes", nodesCopied),
               ex => logger.LogWarning(ex, "Copy failed"));
```

`hub` may be any hub — it supplies the caller identity and reply address; every per-node request is routed to `hub.NodeOperationTarget()`. Permission checks live in the `CreateOrUpdateNodeRequest` handler, and `force: true` updates an existing target rather than deleting it.

---

# Move

Move relocates a node and its entire subtree to a new path. It requires **Delete** permission on the source and **Create** permission on the target.

The move is implemented at the persistence layer, handling both same-partition and cross-partition moves (including PostgreSQL). Descendants are moved first, then the root node is relocated and the source is deleted.

## Programmatic Move

```csharp
hub.Observe(new MoveNodeRequest("org/acme/old-name", "org/acme/new-name"),
        o => o.WithTarget(address))
    .Subscribe(
        response =>
        {
            if (response.Message.Success)
                Console.WriteLine($"Moved to: {response.Message.Node.Path}");
        },
        ex => logger.LogWarning(ex, "Move failed"));
```

---

# File Format Details

Each content type serialises to a distinct, human-readable format. The sections below show the canonical shape for each.

## Markdown (.md)

```markdown
---
Name: "Getting Started"
Category: "Documentation"
Authors:
  - "Jane Doe"
Tags:
  - "tutorial"
---

# Getting Started

Your markdown content here...
```

`NodeType` defaults to `Markdown` and may be omitted. Only non-default values appear in the YAML front matter. If the name matches the ID and there's no other metadata, the YAML block may be omitted entirely.

## Code (.cs)

```csharp
// <meshweaver>
// Id: Person
// DisplayName: Person Data Model
// </meshweaver>

public record Person
{
    [Key]
    public string Id { get; init; } = string.Empty;
    public string? Name { get; init; }
}
```

The `<meshweaver>` metadata block is optional. The primary type name is extracted from the code if no explicit Id is provided.

## JSON (.json)

```json
{
  "id": "task-1",
  "namespace": "org/acme/tasks",
  "name": "Review submission",
  "nodeType": "ACME/Task",
  "content": {
    "$type": "Task",
    "title": "Review submission",
    "priority": "High"
  }
}
```

JSON is the fallback format for nodes that don't match markdown, agent, or code patterns.

---

```csharp --render NodeOpsOverview --show-code
MeshWeaver.Layout.Controls.Stack
    .WithView(MeshWeaver.Layout.Controls.Markdown("### Node Operations at a Glance"))
    .WithView(MeshWeaver.Layout.Controls.Markdown(
        "| Operation | Requires | Scope |\n" +
        "|-----------|----------|-------|\n" +
        "| **Export** | `Permission.Export` (Editor+) | Node + full subtree → ZIP |\n" +
        "| **Import** | Create permission on target | File, folder, or ZIP → nodes |\n" +
        "| **Copy** | Read on source, Create on target | Node + full subtree → new namespace |\n" +
        "| **Move** | Delete on source, Create on target | Node + full subtree → new path |"
    ))
```

---

# See Also

- [Node Menu Items](../../GUI/NodeMenu) — The menu system and the default item set
- [Query Syntax](../QuerySyntax) — Search and filter nodes
- [Node Type Configuration](../NodeTypeConfiguration) — Define custom node types
- [CRUD Operations](../CRUD) — Low-level data operations
