---
Name: MeshPlugin Tools
Category: Documentation
Description: Complete reference for MeshPlugin tools used by AI agents
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

For the complete Unified Path reference:

@@MeshWeaver/Documentation/DataMesh/UnifiedPath

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

@@MeshWeaver/Documentation/DataMesh/QuerySyntax

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

A MeshNode has these key properties:

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `id` | string | Yes | Local identifier within namespace (e.g., "NewOrg", "Task1") |
| `namespace` | string | For nested nodes | Parent path (e.g., "ACME", "ACME/Projects"). Omit for root-level nodes. |
| `name` | string | Yes | Human-readable display name |
| `nodeType` | string | Yes | Type category (must match an existing NodeType, e.g., "Organization", "Todo") |
| `category` | string | No | Grouping category (overrides NodeType for display) |
| `icon` | string | No | Icon URL or identifier |
| `order` | int | No | Sort order (lower values appear first) |
| `content` | object | Depends on type | Type-specific data model content. Schema depends on NodeType. |

The `path` of a node is derived as `{namespace}/{id}` (or just `{id}` for root-level nodes).

### Discovering Content Schemas

Before creating a node, discover what content fields are expected by looking at an existing node of the same type, or at the target namespace:

- `Get('@path/schema:')` — Returns the JSON Schema for the node's content type (e.g., `Get('@Cornerstone/schema:')`)
- `Get('@path/schema:TypeName')` — Returns the JSON Schema for a specific named type
- `Get('@path/model:')` — Returns the full data model with all registered types

The `path` is any node path — the schema/model prefixes work on any address, not just NodeType paths.

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
2. Modify the returned MeshNode JSON (change name, content fields, etc.)
3. Pass the modified node(s) to Update as a JSON array

### Important

- **Always Get before Update** to preserve fields you don't want to change
- The node at the given path is completely replaced with the provided data
- Path is derived from `namespace` + `id`

### Example

```
// First: result = Get('@ACME/ExistingProject')
// Then modify the JSON and update:
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
