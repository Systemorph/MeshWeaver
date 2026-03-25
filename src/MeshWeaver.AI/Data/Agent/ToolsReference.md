---
Name: Tools Reference
Category: Documentation
Description: Complete tools reference for AI agents
---

MeshPlugin provides tools for interacting with the mesh data graph. All paths support the `@` prefix shorthand: `@graph/org1` resolves to `graph/org1`.

**IMPORTANT**: Examples below use `Doc/Architecture` as a sample node path. Always use the actual node path from the user's context instead.

## Get

Retrieves a node from the mesh. Returns JSON.

### Single Node

`Get('@Doc/Architecture')` — Returns the full MeshNode JSON including all properties and Content.

### Children

`Get('@Doc/Architecture/*')` — Returns a JSON array of direct children with `{path, name, nodeType, icon}`.

### Unified Path Prefixes

Get supports Unified Path syntax with reserved prefixes for accessing specific resource types:

| Syntax | Returns |
|--------|---------|
| `Get('@Doc/Architecture/data:')` | Node's Content data as JSON |
| `Get('@Doc/Architecture/data:Collection')` | All entities in a data collection |
| `Get('@Doc/Architecture/data:Collection/id')` | A specific entity by ID |
| `Get('@Doc/Architecture/schema:')` | JSON Schema for the node's content type |
| `Get('@Doc/Architecture/schema:TypeName')` | JSON Schema for a specific named type |
| `Get('@Doc/Architecture/model:')` | Full data model with all registered types |
| `Get('@Doc/Architecture/layoutAreas:')` | List of available layout areas (reports, charts) |
| `Get('@Doc/Architecture/area:AreaName')` | Download a layout area's data for analysis |
| `Get('@Doc/Architecture/content:icon.svg')` | File content from the "content" collection |
| `Get('@Doc/Architecture/content:folder/file.png')` | File from a subfolder in a collection |
| `Get('@Doc/Architecture/content:platform-overview.svg')` | File from a content collection |
| `Get('@Doc/Architecture/collection:')` | All content collection configs (names, types, editability) |
| `Get('@Doc/Architecture/collection:content,assets')` | Specific collection configs |

#### Unified Path Reference

Unified Path allows you to **reference** and **embed** content from anywhere in your MeshWeaver application using a simple `@` notation.

**Pattern:**
```
{address}/{prefix}:{path}
```

| Component | Description |
|-----------|-------------|
| `address` | MeshNode path (resolved via MeshCatalog) |
| `prefix` | Either a **reserved keyword** or a **content collection name** |
| `path` | Resource within the address |

**Reserved Keywords:**

| Prefix | Description |
|--------|-------------|
| `data:` | Access the node's Content data as JSON |
| `schema:` | Access the ContentType schema |
| `model:` | Access the data model |
| `area:` | Access a specific layout area |
| `layoutAreas:` | List available layout areas (reports, views, charts) |

**Content Collection Prefixes:**

Any other prefix is treated as a **content collection name** (e.g., `content:`, `assets:`, `files:`).

**@ vs @@:**

| Syntax | Behavior |
|--------|----------|
| single @ prefix | **Hyperlink** - navigates to content |
| double @@ prefix | **Inline** - embeds content in place |

References must be at the **start of a line**.

Without a prefix, a reference refers to a **layout area** of the target node.
With a reserved prefix (`data:`, `schema:`, `area:`), it accesses that specific resource type.
With any other prefix, it accesses files from a content collection.

### Examples

- `Get('@Doc/Architecture')` — Get a specific node
- `Get('@NodeType/*')` — List all available node types
- `Get('@Doc/DataMesh/data:')` — Get the node's content data as JSON
- `Get('@Doc/DataMesh/schema:')` — Get content type schema
- `Get('@Doc/DataMesh/model:')` — Get the full data model
- `Get('@Doc/DataMesh/layoutAreas:')` — List available layout areas

## Search

Searches the mesh using a GitHub-style query syntax. Returns a JSON array of matching nodes (limited to 50).

### Parameters

- `query` (string, required) — Query string with field filters, wildcards, scoping, sorting
- `basePath` (string, optional) — Base path to narrow the search scope

### Common Patterns

- `Search('nodeType:Agent')` — Find all agents
- `Search('namespace:Doc')` — List direct children of Doc
- `Search('path:Doc scope:descendants')` — All descendants under Doc recursively
- `Search('namespace:Doc scope:descendants')` — Browse all documentation
- `Search('name:*unified* sort:name')` — Complex filtered query
- `Search('architecture')` — Free-text search

### Full Query Syntax Reference

Queries consist of space-separated terms. Each term can be:
- **Field filter**: `field:value` — matches nodes where field equals value
- **Negation**: `-field:value` — excludes nodes where field equals value
- **Text search**: `keyword` — searches in name and description

#### Field Filters

**Equality:** `nodeType:Organization`, `name:Acme`, `status:Active`

**Negation:** `-status:Archived`

**Wildcard Patterns:** `name:*claims*` (contains), `name:Acme*` (starts with), `name:*Corp` (ends with)

**Comparison Operators:** `price:>100`, `price:<50`, `price:>=100`, `price:<=50`

**List Values (OR):** `status:(Active OR Pending OR Draft)`, `nodeType:(Organization OR Project)`

**Empty Values:** `description:` (matches nodes with no description)

#### Reserved Qualifiers

**namespace** — Sets the search location (like a folder). Default scope is `children`:
```
namespace:Doc                  # Immediate children of Doc
namespace:Doc scope:descendants  # All items under Doc (recursive)
```

**scope** — Controls search scope relative to namespace or path:
```
scope:descendants     # All descendants recursively (excludes self)
scope:ancestors       # Parent hierarchy upward (excludes self)
scope:hierarchy       # Ancestors + self + descendants
scope:subtree         # Self + all descendants
scope:ancestorsandself # Self + all ancestors
```

**path** — Sets the base path for search (default scope is `exact`):
```
path:Doc/Architecture          # The exact node
namespace:Doc                  # Immediate children of Doc
```

**sort** — Specifies sort order: `sort:name`, `sort:name-desc`, `sort:lastModified-desc`

**limit** — Limits the number of results: `limit:10`, `limit:50`

**source** — Specifies the data source:
```
source:activity    # Results ordered by user's last access time
```

**context** — Filters results by visibility context:
```
context:search         # Exclude nodes hidden from search
context:create         # Exclude nodes hidden from create menus
```

**select** — Projects results to include only specified properties:
```
select:name,nodeType,icon
```

#### Complex Queries

Combine multiple filters:
```
namespace:Doc nodeType:Markdown
nodeType:Markdown name:*path* sort:lastModified-desc limit:20
namespace:Doc/Architecture scope:descendants
```

#### Tips

1. All comparisons are case-insensitive
2. `namespace:X` is like searching in folder X (immediate children)
3. Add `scope:descendants` for recursive search
4. Use `*` for flexible pattern matching

## NavigateTo

Displays a node's visual layout area in the chat UI.

**CRITICAL:** When users ask to "show", "display", or "view" something:
1. Use `NavigateTo('@Doc/Architecture')` to render the visual representation
2. Keep your text response minimal — just confirm what was displayed
3. Do NOT dump raw JSON when a visual display is available

### Example

User asks: "Show me the architecture docs"
Action: Call `NavigateTo('@Doc/Architecture')`, respond: "Here's the architecture documentation."

## Create

Creates a new node in the mesh. The node is validated before being persisted.

### Parameter

`node` (string, required) — A JSON string representing a MeshNode object.

### MeshNode Schema

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `id` | string | Yes | Local identifier within namespace (e.g., "NewOrg", "Task1") |
| `namespace` | string | For nested nodes | Parent path (e.g., "ACME", "ACME/Projects"). Omit for root-level nodes. |
| `name` | string | Yes | Human-readable display name |
| `nodeType` | string | Yes | Type category (must match an existing NodeType) |
| `category` | string | No | Grouping category |
| `icon` | string | No | Icon URL or identifier |
| `order` | int | No | Sort order (lower values appear first) |
| `content` | object | Depends on type | Type-specific data model content |

The `path` of a node is derived as `{namespace}/{id}` (or just `{id}` for root-level nodes).

### Discovering Content Schemas

Before creating a node, discover what content fields are expected:

- `Get('@Doc/Architecture/schema:')` — Returns the JSON Schema for the node's content type
- `Get('@Doc/Architecture/schema:TypeName')` — Returns the JSON Schema for a specific named type
- `Get('@Doc/Architecture/model:')` — Returns the full data model with all registered types

### Workflow

1. Find an existing node of the type you want to create, or the namespace where you want to create
2. Retrieve its content schema: `Get('@Doc/Architecture/schema:')`
3. Construct the MeshNode JSON with all required fields
4. Call Create with the JSON

### Example

```
Create('{"id": "NewPage", "namespace": "MyOrg", "name": "New Page", "nodeType": "Markdown"}')
```

## Update

Updates one or more existing nodes in the mesh. The entire MeshNode is replaced, not merged.

### Parameter

`nodes` (string, required) — A JSON array of MeshNode objects with updated fields.

### Workflow

1. Retrieve existing nodes via `Get('@Doc/Architecture')` or `Search('...')`
2. Modify the returned MeshNode JSON
3. Pass the modified node(s) to Update as a JSON array

### Important

- **Always Get before Update** to preserve fields you don't want to change
- The node at the given path is completely replaced with the provided data
- Path is derived from `namespace` + `id`

### Example

```
Update('[{"id": "ExistingPage", "namespace": "MyOrg", "name": "Renamed Page", "nodeType": "Markdown"}]')
```

## Delete

Deletes one or more nodes by their paths.

### Parameter

`paths` (string, required) — A JSON array of path strings to delete.

### Example

```
Delete('["MyOrg/OldPage", "MyOrg/ArchivedNote"]')
```

## Layout Areas (Reports, Views, Charts, Dashboards)

When the user asks about **reports**, **views**, **charts**, **analysis**, **dashboards**, or **visualizations**, use layout areas.

### Discovering Available Layout Areas

Use the `layoutAreas:` prefix to list all available layout areas for a node:

```
Get('@Doc/Architecture/layoutAreas:')
```

This returns an array of `LayoutAreaDefinition` objects with `Area`, `Title`, `Description`, `Group`, and `Order` fields.

### Downloading Area Data for Analysis

Use the `area:` prefix to download a layout area's data for analysis:

```
Get('@Doc/Architecture/area:AreaName')
```

This returns the area's data as an EntityStore, which you can analyze and summarize.

### Navigating to a Visual Display

Use `NavigateTo` to display a layout area visually in the chat UI:

```
NavigateTo('@Doc/Architecture/AreaName')
```

### Inline Embedding

Use double @@ prefix to embed a layout area inline in markdown responses. Write the double @@ followed by the node path and area name at the start of a line. Example:

### Workflow

1. **Discover**: `Get('@Doc/Architecture/layoutAreas:')` — list available areas
2. **Analyze**: `Get('@Doc/Architecture/area:AreaName')` — download area data
3. **Display**: `NavigateTo('@Doc/Architecture/AreaName')` — show visual chart/report
4. **Embed**: write double @@ followed by `Doc/Architecture/AreaName` at the start of a line

## Content Collections

Content collections store files (images, documents, markdown, etc.) associated with mesh nodes. The `content:` prefix accesses the default "content" collection. Other collection names use `collection:` for discovery.

### Workflow: Browsing and Downloading Files

When you need to work with files in a content collection, follow this sequence:

1. **Discover collections**: `Get('@Doc/Architecture/collection:')` — lists all available collection configs (names, types, editability)
2. **List files in collection root**: `Get('@Doc/Architecture/content:')` — returns files and folders in the default "content" collection root
3. **List files in a named collection**: `Get('@Doc/Architecture/content:collectionName')` — returns files and folders in the named collection root
4. **Browse a subfolder**: `Get('@Doc/Architecture/content:collectionName/subfolder')` — if "subfolder" is a folder, returns its files and folders
5. **Download a specific file**: `Get('@Doc/Architecture/content:icon.svg')` — downloads the file from the default "content" collection
6. **Download from a subfolder**: `Get('@Doc/Architecture/content:MeshGraph/overview.svg')` — downloads a file from a subfolder

The system automatically detects whether a path refers to a file or folder. Files are downloaded, folders are listed. Each item in a listing has `name`, `path`, `isFolder`, and `lastModified` (for files).

### Examples

```
Get('@Doc/Architecture/collection:')                    -- List all collection configs
Get('@Doc/Architecture/content:')                       -- List files/folders in default "content" collection
Get('@Doc/Architecture/content:collectionName')         -- List files/folders in a named collection
Get('@Doc/Architecture/content:collectionName/subfolder') -- List files in a subfolder
Get('@Doc/Architecture/content:icon.svg')              -- Download icon.svg from default "content" collection
Get('@Doc/Architecture/content:MeshGraph/overview.svg')  -- Download file from a subfolder
```

The format is: `@Doc/Architecture/content:{path}` where the path is automatically resolved as file (download) or folder (list contents).

### Embedding Content Files

Use double @@ prefix to embed content files inline in markdown. Write the double @@ followed by the node path and content reference at the start of a line. Only embed files that actually exist — use `Get` with the `content:` prefix first to verify the file is available.

Example syntax: `@@Doc/Architecture/ActorModel` embeds the Actor Model documentation inline.

### Uploading Content Files

Use `UploadContent` to save text-based files (SVG, markdown, JSON, CSS) to a node's content collection:

```
UploadContent('@Doc/Architecture', 'diagram.svg', '<svg>...</svg>')
UploadContent('@Doc/Architecture', 'images/overview.svg', svgContent, 'content')
```

Parameters:
- `nodePath` — the node that owns the collection
- `filePath` — file name/path within the collection (e.g., `diagram.svg`, `images/arch.svg`)
- `content` — the text content (SVG markup, markdown, JSON, etc.)
- `collectionName` — collection name (default: `content`)

After uploading, reference the file with `@Doc/Architecture/content:diagram.svg` or embed inline with `@@Doc/Architecture/content:diagram.svg`.

**Tip for icons:** Set a node's `icon` property to inline SVG (starting with `<svg`) and it renders directly — no upload needed.

## Satellite Namespaces

Nodes can have satellite data stored in dedicated sub-namespaces with underscore prefixes. These are persisted in separate database tables per partition.

| Prefix | Table | Node Types | Purpose |
|--------|-------|------------|---------|
| `_Thread` | threads | Thread, ThreadMessage | Chat/discussion threads |
| `_Comment` | comments | Comment | Document comments and replies |
| `_Activity` | activities | ActivityLog | Activity tracking |
| `_UserActivity` | user_activities | UserActivity | Per-user activity (recently viewed) |
| `_Access` | access | AccessAssignment | Permission grants |
| `_Approval` | approvals | Approval | Approval workflows |
| `_Tracking` | tracking | TrackedChange | Track changes / collaborative editing |

### Path Patterns

- Satellite nodes: `{parentPath}/{_Prefix}/{nodeId}`
- Thread messages (children of threads): `{contextPath}/_Thread/{threadId}/{msgId}`
- Comment replies: `{docPath}/_Comment/{commentId}/{replyId}`

### Querying Satellites

```
Search('namespace:{parentPath}/_Thread nodeType:Thread')     # Find threads under a node
Search('namespace:{parentPath}/_Comment nodeType:Comment')   # Find comments
Search('namespace:{parentPath}/_Activity')                   # Find activity logs
```

## Reading Documentation

To browse all available documentation:
```
Search('namespace:Doc scope:descendants')
```
Then read any article with `Get('@Doc/...')`.
