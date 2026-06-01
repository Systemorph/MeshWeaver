---
Name: MeshPlugin Tools
Category: Documentation
Description: Complete reference for the MeshPlugin tools available to AI agents
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z"/></svg>
---

MeshPlugin gives AI agents a clean, consistent API for navigating and modifying the mesh data graph. Every path argument supports the `@` shorthand prefix — `@graph/org1` resolves to `graph/org1`.

---

## Get

Reads a node from the mesh and returns its JSON representation.

### Single node

```
Get('@path')
```

Returns the full `MeshNode` JSON, including all properties and the typed `content` object.

### Children list

```
Get('@path/*')
```

Returns a JSON array of direct children, each with `{ path, name, nodeType, icon }`. Useful for browsing a namespace before diving deeper.

### Unified Path prefixes

`Get` understands reserved path prefixes that expose type metadata alongside node data:

| Syntax | Returns |
|---|---|
| `Get('@path/schema/')` | JSON Schema for the node's content type |
| `Get('@path/schema/TypeName')` | JSON Schema for a specific named type |
| `Get('@path/model/')` | Full data model with all registered types |

These schema prefixes work on **any** address — not just `NodeType` paths. Use them before creating or updating a node to discover the exact fields expected.

For the complete Unified Path reference:

@@../../../DataMesh/UnifiedPath

### Examples

```
Get('@graph/org1')                      // Read a specific organisation node
Get('@NodeType/*')                      // List all available node types
Get('@ACME/ProductLaunch/schema/')      // Content schema for ProductLaunch
Get('@ACME/ProductLaunch/model/')       // Full data model
```

---

## Search

Searches the mesh using a GitHub-style query syntax. Returns a JSON array of up to 50 matching nodes.

### Parameters

| Parameter | Required | Description |
|---|---|---|
| `query` | Yes | Filter string with field filters, wildcards, scoping, and sorting |
| `basePath` | No | Limits the search to a specific subtree |

### Common patterns

```
Search('nodeType:Agent')                                         // All agents
Search('namespace:ACME')                                         // Direct children of ACME
Search('path:ACME scope:descendants')                            // Everything under ACME recursively
Search('namespace:Doc scope:descendants')                        // Browse all documentation
Search('name:*sales* nodeType:Organization sort:name')           // Complex filtered query
Search('laptop', '@graph')                                       // Free-text search within graph
```

### Full query syntax

@@../../../DataMesh/QuerySyntax

---

## NavigateTo

Opens a node's visual layout area inside the chat UI — the mesh's equivalent of "Show me this page."

> **CRITICAL:** When a user asks to "show", "display", or "view" something, always prefer `NavigateTo` over dumping raw JSON. Call it, then keep your text response short — just confirm what was displayed.

### Workflow

1. Call `NavigateTo('@path')`.
2. Respond with one sentence — e.g., *"Here's the organisation chart."*

### Example

User: *"Show me the organisation chart."*
Action: `NavigateTo('@ACME/Organization')`
Response: *"Here's the organisation chart."*

---

## RenderArea

Returns a live, interactive layout area as an MCP-UI embedded resource.

Hosts that support MCP-UI (Claude.ai web/desktop, ChatGPT Apps) render it inline as an iframe widget. Text-only hosts (such as the Claude Code CLI) receive a fallback URL instead.

### Parameters

| Parameter | Required | Description |
|---|---|---|
| `path` | Yes | Path to the node hosting the area (e.g., `@Systemorph/FutuRe/EuropeRe/AcmeSubmission2025`) |
| `areaName` | Yes | Name of the layout area on that node (e.g., `Triangle`) |

### Choosing the right display tool

| Tool | Use when … |
|---|---|
| `RenderArea` | The user benefits from a **live interactive view** embedded in the conversation — charts, grids, dashboards, triangles. Best for MCP-UI hosts. |
| `NavigateTo` | You want to give the user a **clickable link** to open in a new browser tab. |
| `Get('.../area/Name')` | You need **structured JSON** of the rendered payload for programmatic use. |

### Example

```
RenderArea('@Systemorph/FutuRe/EuropeRe/AcmeSubmission2025', 'Triangle')
```

---

## Create

Creates and persists a new node in the mesh. The node is validated before it is saved.

### Parameter

`node` (string, required) — A JSON string representing a `MeshNode` object.

### MeshNode schema

| Property | Type | Required | Description |
|---|---|---|---|
| `id` | string | Yes | Simple slug — **no slashes** (e.g., `"NewOrg"`, `"Task1"`) |
| `namespace` | string | For nested nodes | Full parent path (e.g., `"ACME"`, `"ACME/Projects"`). Omit for root-level nodes. |
| `name` | string | Yes | Clear, human-readable title. Think of it as a document heading. |
| `nodeType` | string | Yes | Must match an existing `NodeType` (e.g., `"Organization"`, `"Markdown"`) |
| `category` | string | No | Grouping category; overrides `NodeType` for display purposes |
| `icon` | string | No | Inline SVG starting with `<svg` — create a unique, visually appealing icon that matches the node's purpose |
| `order` | int | No | Sort order; lower values appear first |
| `content` | object | Depends on type | Type-specific data. Schema varies by `NodeType`. |

The node's `path` is derived as `{namespace}/{id}`, or simply `{id}` for root-level nodes.

### Critical rules

> **`id` must be a simple slug — no slashes.** Use only letters, numbers, and hyphens.
>
> Correct: `"id": "PricingTool", "namespace": "User/rbuergi"`
> Wrong: `"id": "User/rbuergi/PricingTool", "namespace": ""`

- **`name` must be a clear, descriptive title** — not just the first few words of the content body.
- **`icon` should be an inline SVG** — a small, clean 24×24 image that visually represents the node's purpose. Use simple shapes and colours that match the content theme.

### Discovering the content schema

Before creating a node, check what `content` fields the target type expects:

```
Get('@Cornerstone/schema/')             // Schema for nodes in the Cornerstone namespace
Get('@path/schema/TypeName')            // Schema for a specific named type
Get('@path/model/')                     // Full data model with all registered types
```

### Workflow

1. Find an existing node of the same type, or navigate to the target namespace.
2. Retrieve its content schema: `Get('@path/schema/')`.
3. Build the `MeshNode` JSON with all required fields.
4. Call `Create` with the JSON string.

### Example

```
Create('{"id": "NewProject", "namespace": "ACME", "name": "New Project", "nodeType": "Project", "content": {"status": "Active"}}')
```

---

## Update

Replaces one or more existing nodes with new data. The **entire node** is replaced — this is not a merge/patch operation.

### Parameter

`nodes` (string, required) — A JSON array of `MeshNode` objects with updated fields.

### Important

> **Always `Get` before `Update`** to avoid accidentally erasing fields you did not intend to change. The node at the given path is completely replaced by what you provide.

Path is derived from `namespace` + `id`, same as for `Create`.

### Workflow

1. Retrieve the current node with `Get('@path')` or `Search('...')`.
2. Modify the returned JSON (change `name`, `content` fields, etc.).
3. Pass the modified node(s) to `Update` as a JSON array.

### Example

```
// First: result = Get('@ACME/ExistingProject')
// Modify the JSON, then:
Update('[{"id": "ExistingProject", "namespace": "ACME", "name": "Renamed Project", "nodeType": "Project", "content": {"status": "Completed"}}]')
```

---

## Delete

Removes one or more nodes from the mesh by their paths.

### Parameter

`paths` (string, required) — A JSON array of path strings to delete.

### Example

```
Delete('["ACME/OldProject", "ACME/ArchivedTask"]')
```

---

## Reading Documentation

To explore all available documentation, browse the `Doc` namespace and read any article with `Get`:

```
Search('namespace:Doc scope:descendants')
Get('@Doc/Architecture/SomeArticle')
```
