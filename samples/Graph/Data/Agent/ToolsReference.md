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
| `Get('@path/schema:')` | JSON Schema for the node's content type |
| `Get('@path/schema:TypeName')` | JSON Schema for a specific named type |
| `Get('@path/model:')` | Full data model with all registered types |

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

- `Get('@graph/org1')` — Get a specific organization node
- `Get('@NodeType/*')` — List all available node types
- `Get('@ACME/ProductLaunch/schema:')` — Get content type schema for ProductLaunch
- `Get('@ACME/ProductLaunch/model:')` — Get the full data model

## Search

Searches the mesh using a GitHub-style query syntax. Returns a JSON array of matching nodes (limited to 50).

### Parameters

- `query` (string, required) — Query string with field filters, wildcards, scoping, sorting
- `basePath` (string, optional) — Base path to narrow the search scope

### Common Patterns

- `Search('nodeType:Agent')` — Find all agents
- `Search('path:ACME scope:children')` — List direct children of ACME
- `Search('path:ACME scope:descendants')` — All descendants under ACME recursively
- `Search('namespace:MeshWeaver/Documentation scope:descendants')` — Browse all documentation
- `Search('name:*sales* nodeType:Organization sort:name')` — Complex filtered query
- `Search('laptop', '@graph')` — Free-text search under graph

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
namespace:Systemorph          # Immediate children of Systemorph
namespace:Systemorph scope:descendants  # All items under Systemorph (recursive)
```

**scope** — Controls search scope relative to namespace or path:
```
scope:exact           # Only the exact path (default for path:)
scope:children        # Immediate children only (excludes self)
scope:descendants     # All descendants recursively (excludes self)
scope:ancestors       # Parent hierarchy upward (excludes self)
scope:hierarchy       # Ancestors + self + descendants
scope:subtree         # Self + all descendants
scope:ancestorsandself # Self + all ancestors
```

**path** — Sets the base path for search (default scope is `exact`):
```
path:Systemorph                # The exact Systemorph node
path:Systemorph scope:children # Immediate children of Systemorph
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
namespace:Systemorph nodeType:Project
nodeType:Story name:*claims* sort:lastModified-desc limit:20
namespace:ACME/ProductLaunch nodeType:Todo scope:descendants
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

User asks: "Show me the organization chart"
Action: Call `NavigateTo('@ACME/Organization')`, respond: "Here's the organization chart."

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
Create('{"id": "NewProject", "namespace": "ACME", "name": "New Project", "nodeType": "Project", "content": {"status": "Active"}}')
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
Update('[{"id": "ExistingProject", "namespace": "ACME", "name": "Renamed Project", "nodeType": "Project", "content": {"status": "Completed"}}]')
```

## Delete

Deletes one or more nodes by their paths.

### Parameter

`paths` (string, required) — A JSON array of path strings to delete.

### Example

```
Delete('["ACME/OldProject", "ACME/ArchivedTask"]')
```

## Reading Documentation

To browse all available documentation:
```
Search('namespace:MeshWeaver/Documentation scope:descendants')
```
Then read any article with `Get('@MeshWeaver/Documentation/...')`.
