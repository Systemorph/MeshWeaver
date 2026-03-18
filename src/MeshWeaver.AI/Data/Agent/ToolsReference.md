---
Name: Tools Reference
Category: Documentation
Description: Complete tools reference for AI agents
---

MeshPlugin provides tools for interacting with the mesh data graph. All paths support the `@` prefix shorthand: `@graph/org1` resolves to `graph/org1`.

## Get

Retrieves a node from the mesh. Returns JSON.

### Single Node

`Get('@path')` — Returns the full MeshNode JSON including all properties and Content.

### Children

`Get('@path/*')` — Returns a JSON array of direct children with `{path, name, nodeType, icon}`.

### Unified Path Prefixes

Get supports Unified Path syntax with reserved prefixes for accessing specific resource types:

| Syntax | Returns |
|--------|---------|
| `Get('@path/data:')` | Node's Content data as JSON |
| `Get('@path/data:Collection')` | All entities in a data collection |
| `Get('@path/data:Collection/id')` | A specific entity by ID |
| `Get('@path/schema:')` | JSON Schema for the node's content type |
| `Get('@path/schema:TypeName')` | JSON Schema for a specific named type |
| `Get('@path/model:')` | Full data model with all registered types |
| `Get('@path/layoutAreas:')` | List of available layout areas (reports, charts) |
| `Get('@path/area:AreaName')` | Download a layout area's data for analysis |
| `Get('@path/content:file.txt')` | File content from the "content" collection |
| `Get('@path/content:folder/file.png')` | File from a subfolder in a collection |
| `Get('@path/assets:logo.svg')` | File from the "assets" collection (any collection name works) |
| `Get('@path/collection:')` | All content collection configs (names, types, editability) |
| `Get('@path/collection:content,assets')` | Specific collection configs |

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
| `@path` | **Hyperlink** - navigates to content |
| `@@path` | **Inline** - embeds content in place |

References must be at the **start of a line**.

Without a prefix, `@path` / `@@path` refers to a **layout area** of the target node.
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
1. Use `NavigateTo('@path')` to render the visual representation
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

- `Get('@path/schema:')` — Returns the JSON Schema for the node's content type
- `Get('@path/schema:TypeName')` — Returns the JSON Schema for a specific named type
- `Get('@path/model:')` — Returns the full data model with all registered types

### Workflow

1. Find an existing node of the type you want to create, or the namespace where you want to create
2. Retrieve its content schema: `Get('@path/schema:')`
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

1. Retrieve existing nodes via `Get('@path')` or `Search('...')`
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
Get('@path/layoutAreas:')
```

This returns an array of `LayoutAreaDefinition` objects with `Area`, `Title`, `Description`, `Group`, and `Order` fields.

### Downloading Area Data for Analysis

Use the `area:` prefix to download a layout area's data for analysis:

```
Get('@path/area:AreaName')
```

This returns the area's data as an EntityStore, which you can analyze and summarize.

### Navigating to a Visual Display

Use `NavigateTo` to display a layout area visually in the chat UI:

```
NavigateTo('@path/AreaName')
```

### Inline Embedding

Use `@@` syntax to embed a layout area inline in markdown responses:

```
@@path/AreaName
```

### Workflow

1. **Discover**: `Get('@path/layoutAreas:')` — list available areas
2. **Analyze**: `Get('@path/area:AreaName')` — download area data
3. **Display**: `NavigateTo('@path/AreaName')` — show visual chart/report
4. **Embed**: `@@path/AreaName` — inline in markdown

## Content Collections

Content collections store files (images, documents, markdown, etc.) associated with mesh nodes. The `content:` prefix accesses the default "content" collection. Other collection names use `collection:` for discovery.

### Workflow: Browsing and Downloading Files

When you need to work with files in a content collection, follow this sequence:

1. **Discover collections**: `Get('@path/collection:')` — lists all available collection configs (names, types, editability)
2. **List files in collection root**: `Get('@path/content:')` — returns files and folders in the default "content" collection root
3. **List files in a named collection**: `Get('@path/content:collectionName')` — returns files and folders in the named collection root
4. **Browse a subfolder**: `Get('@path/content:collectionName/subfolder')` — if "subfolder" is a folder, returns its files and folders
5. **Download a specific file**: `Get('@path/content:filename.md')` — downloads the file from the default "content" collection
6. **Download from a named collection**: `Get('@path/content:collectionName/filename.md')` — downloads the file from the named collection

The system automatically detects whether a path refers to a file or folder. Files are downloaded, folders are listed. Each item in a listing has `name`, `path`, `isFolder`, and `lastModified` (for files).

### Examples

```
Get('@path/collection:')                    -- List all collection configs
Get('@path/content:')                       -- List files/folders in default "content" collection
Get('@path/content:collectionName')         -- List files/folders in a named collection
Get('@path/content:collectionName/subfolder') -- List files in a subfolder
Get('@path/content:readme.md')             -- Download readme.md from default "content" collection
Get('@path/content:collectionName/file.pdf') -- Download file from a named collection
```

The format is: `@{address}/content:{path}` where the path is automatically resolved as file (download) or folder (list contents).

### Embedding Content Files

Use `@@` syntax to embed content files inline in markdown:
```
@@path/content:images/logo.svg              -- Embed image inline
@@path/content:notes.md                     -- Embed markdown inline
```

## Reading Documentation

To browse all available documentation:
```
Search('namespace:Doc scope:descendants')
```
Then read any article with `Get('@Doc/...')`.
