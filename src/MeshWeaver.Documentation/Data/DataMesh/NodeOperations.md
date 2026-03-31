---
Name: Node Operations
Category: Documentation
Description: Export, Import, Copy, and Move operations for mesh nodes
---

MeshWeaver provides built-in operations for transferring node subtrees between locations, exporting to files, and importing from external sources. These are accessible from the **Actions** sub-menu on any node.

# Export

Export produces a ZIP archive of a node and its entire subtree using **native file formats** that preserve human readability:

| Content Type | File Format | Example |
|-------------|-------------|---------|
| Markdown | `.md` with YAML front matter | Documents, articles |
| Agent | `.md` with agent YAML | Agent configurations |
| Code | `.cs` plain C# files | Code configurations |
| Other | `.json` with `$type` | Generic node data |

The exported ZIP mirrors the file-system directory structure, making it directly compatible with Import for round-trip workflows.

## Permission

Export requires `Permission.Export`, which is granted to the **Editor** and **Admin** roles but not to Viewer.

## Programmatic Export

```csharp
var exportService = hub.ServiceProvider.GetRequiredService<IMeshExportService>();
var result = await exportService.ExportToDirectoryAsync("org/acme/project", outputDir);
// result.NodesExported, result.PartitionsExported, result.Success
```

`IMeshExportService` uses `FileFormatParserRegistry` to select the appropriate serializer for each node based on its content type. Partition data (sub-paths like `_Source`, `layoutAreas`) is exported as JSON.

## Export-Import Round Trip

Exporting a subtree and re-importing the ZIP restores the original state:

```
Export org/acme → ZIP (contains .md, .cs, .json files)
  → Import ZIP into org/acme (force: true)
  → Original node names, types, and content are preserved
```

# Import

Import reads files from a directory (uploaded as file, folder, or ZIP) and creates nodes in the mesh.

**Supported sources:**
- **Copy from Mesh Node** -- duplicate an existing node tree
- **Upload File** -- single `.md`, `.json`, `.yaml`, `.csv`, or `.html` file
- **Upload Folder (ZIP)** -- directory structure or ZIP archive

The import service (`IMeshImportService`) uses `FileFormatParserRegistry` to parse each file into a `MeshNode` based on its extension.

## Programmatic Import

```csharp
var importService = hub.ServiceProvider.GetRequiredService<IMeshImportService>();
var result = await importService.ImportNodesAsync(
    sourcePath: "/tmp/exported-data",
    targetRootPath: "org/acme/project",
    force: true,           // overwrite existing nodes
    removeMissing: false); // don't delete nodes absent from source
```

# Copy

Copy duplicates a node and all its descendants to a new namespace. The source node's ID is preserved under the target.

**Example:** Copying `org/acme` to `org/backup` creates:
- `org/backup/acme` (root)
- `org/backup/acme/child1`
- `org/backup/acme/child2`
- etc.

## Options
- **Force**: overwrite existing nodes at the destination (default: skip existing)

## Programmatic Copy

```csharp
var nodesCopied = await NodeCopyHelper.CopyNodeTreeAsync(
    meshService, meshService, hub,
    sourcePath: "org/acme",
    targetNamespace: "org/backup",
    force: false);
```

# Move

Move relocates a node and its entire subtree to a new path. It requires **Delete** permission on the source and **Create** permission on the target.

The move is implemented at the persistence layer, handling both same-partition and cross-partition moves (including PostgreSQL). Descendants are moved first, then the root node is relocated and the source is deleted.

## Programmatic Move

```csharp
var response = await hub.AwaitResponse<MoveNodeResponse>(
    new MoveNodeRequest("org/acme/old-name", "org/acme/new-name"),
    o => o.WithTarget(address));

if (response.Message.Success)
    Console.WriteLine($"Moved to: {response.Message.Node.Path}");
```

# File Format Details

## Markdown (.md)

```markdown
---
NodeType: "ACME/Article"
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

Only non-default values appear in the YAML front matter. If the node type is "Markdown" and the name matches the ID, the YAML block may be omitted entirely.

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

# See Also

- [Node Menu Items](../../GUI/NodeMenu) -- Menu system and Actions sub-menu
- [Query Syntax](../QuerySyntax) -- Search and filter nodes
- [Node Type Configuration](../NodeTypeConfiguration) -- Define custom node types
- [CRUD Operations](../CRUD) -- Low-level data operations
