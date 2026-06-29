---
Name: MeshPlugin Tools
Category: Documentation
Description: Complete reference for the MeshPlugin tools available to AI agents
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z"/></svg>
---

MeshPlugin gives AI agents a clean, consistent API for navigating and modifying the mesh data graph. Every path argument supports the `@` shorthand prefix — `@graph/org1` resolves to `graph/org1`.

<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 760 310" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="mp-arrow" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity=".55"/>
    </marker>
  </defs>
  <rect x="1" y="1" width="758" height="308" rx="12" fill="none" stroke="currentColor" stroke-opacity=".15" stroke-width="1.5"/>
  <text x="380" y="26" font-family="sans-serif" font-size="13" font-weight="600" fill="currentColor" fill-opacity=".75" text-anchor="middle">MeshPlugin — AI Agent API Surface</text>
  <rect x="290" y="42" width="180" height="46" rx="10" fill="#1565c0"/>
  <text x="380" y="61" font-family="sans-serif" font-size="12" font-weight="700" fill="#fff" text-anchor="middle">AI Agent</text>
  <text x="380" y="78" font-family="sans-serif" font-size="10" fill="#bbdefb" text-anchor="middle">Claude / GPT / custom</text>
  <line x1="160" y1="88" x2="290" y2="88" stroke="currentColor" stroke-opacity=".3" stroke-width="1" stroke-dasharray="4,3"/>
  <line x1="470" y1="88" x2="600" y2="88" stroke="currentColor" stroke-opacity=".3" stroke-width="1" stroke-dasharray="4,3"/>
  <rect x="24" y="115" width="108" height="46" rx="9" fill="#1e88e5"/>
  <text x="78" y="135" font-family="sans-serif" font-size="12" font-weight="700" fill="#fff" text-anchor="middle">Get</text>
  <text x="78" y="151" font-family="sans-serif" font-size="10" fill="#bbdefb" text-anchor="middle">Read node / schema</text>
  <rect x="150" y="115" width="108" height="46" rx="9" fill="#5c6bc0"/>
  <text x="204" y="135" font-family="sans-serif" font-size="12" font-weight="700" fill="#fff" text-anchor="middle">Search</text>
  <text x="204" y="151" font-family="sans-serif" font-size="10" fill="#e8eaf6" text-anchor="middle">Query the graph</text>
  <rect x="276" y="115" width="108" height="46" rx="9" fill="#26a69a"/>
  <text x="330" y="135" font-family="sans-serif" font-size="12" font-weight="700" fill="#fff" text-anchor="middle">NavigateTo</text>
  <text x="330" y="151" font-family="sans-serif" font-size="10" fill="#b2dfdb" text-anchor="middle">Open in UI</text>
  <rect x="402" y="115" width="108" height="46" rx="9" fill="#8e24aa"/>
  <text x="456" y="135" font-family="sans-serif" font-size="12" font-weight="700" fill="#fff" text-anchor="middle">RenderArea</text>
  <text x="456" y="151" font-family="sans-serif" font-size="10" fill="#e1bee7" text-anchor="middle">Embed live widget</text>
  <rect x="528" y="115" width="108" height="46" rx="9" fill="#43a047"/>
  <text x="582" y="135" font-family="sans-serif" font-size="12" font-weight="700" fill="#fff" text-anchor="middle">Create</text>
  <text x="582" y="151" font-family="sans-serif" font-size="10" fill="#c8e6c9" text-anchor="middle">Add new node</text>
  <rect x="654" y="115" width="82" height="46" rx="9" fill="#e53935"/>
  <text x="695" y="135" font-family="sans-serif" font-size="12" font-weight="700" fill="#fff" text-anchor="middle">Update</text>
  <text x="695" y="151" font-family="sans-serif" font-size="10" fill="#ffcdd2" text-anchor="middle">Replace / Delete</text>
  <line x1="380" y1="88" x2="380" y2="115" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#mp-arrow)"/>
  <line x1="78" y1="88" x2="78" y2="115" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#mp-arrow)"/>
  <line x1="204" y1="88" x2="204" y2="115" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#mp-arrow)"/>
  <line x1="456" y1="88" x2="456" y2="115" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#mp-arrow)"/>
  <line x1="582" y1="88" x2="582" y2="115" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#mp-arrow)"/>
  <line x1="695" y1="88" x2="695" y2="115" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#mp-arrow)"/>
  <rect x="170" y="215" width="420" height="60" rx="10" fill="none" stroke="currentColor" stroke-opacity=".2" stroke-width="1.5"/>
  <text x="380" y="238" font-family="sans-serif" font-size="11" font-weight="600" fill="currentColor" fill-opacity=".65" text-anchor="middle">Mesh Data Graph</text>
  <text x="380" y="256" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".5" text-anchor="middle">Nodes  ·  Schemas  ·  Layout Areas  ·  Documentation</text>
  <line x1="78" y1="161" x2="78" y2="240" stroke="currentColor" stroke-opacity=".25" stroke-width="1" stroke-dasharray="3,3"/>
  <line x1="78" y1="240" x2="170" y2="240" stroke="currentColor" stroke-opacity=".25" stroke-width="1" stroke-dasharray="3,3"/>
  <line x1="204" y1="161" x2="204" y2="240" stroke="currentColor" stroke-opacity=".25" stroke-width="1" stroke-dasharray="3,3"/>
  <line x1="204" y1="240" x2="170" y2="240" stroke="currentColor" stroke-opacity=".25" stroke-width="1" stroke-dasharray="3,3"/>
  <line x1="330" y1="161" x2="330" y2="215" stroke="currentColor" stroke-opacity=".25" stroke-width="1" stroke-dasharray="3,3"/>
  <line x1="456" y1="161" x2="456" y2="215" stroke="currentColor" stroke-opacity=".25" stroke-width="1" stroke-dasharray="3,3"/>
  <line x1="582" y1="161" x2="582" y2="240" stroke="currentColor" stroke-opacity=".25" stroke-width="1" stroke-dasharray="3,3"/>
  <line x1="582" y1="240" x2="590" y2="240" stroke="currentColor" stroke-opacity=".25" stroke-width="1" stroke-dasharray="3,3"/>
  <line x1="695" y1="161" x2="695" y2="240" stroke="currentColor" stroke-opacity=".25" stroke-width="1" stroke-dasharray="3,3"/>
  <line x1="695" y1="240" x2="590" y2="240" stroke="currentColor" stroke-opacity=".25" stroke-width="1" stroke-dasharray="3,3"/>
</svg>

*Six tools give AI agents complete read, search, display, and write access to the mesh data graph.*

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
