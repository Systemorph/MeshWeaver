---
Name: MeshPlugin Tools
Category: Documentation
Description: How to use MeshPlugin tools for mesh operations
---

MeshPlugin provides tools for working with the mesh data store.

# Get - Retrieve Nodes

Retrieves a node or list of nodes from the mesh hierarchy. Returns JSON.

**Syntax:**
- Single node: `Get('@path')` - Returns full node with name, description, nodeType, content
- Children: `Get('@path/*')` - Returns list of direct children

**Path format:**
- Use `@` prefix as shorthand: `@graph/org1` = `graph/org1`
- Common paths: `@NodeTypes/*` (list all types), `@graph/*` (top-level nodes)

**Examples:**
- `Get('@graph/org1')` - Get specific organization
- `Get('@NodeTypes/*')` - List all node types
- `Get('@Agents/*')` - List all agents

# Search - Query Nodes

Searches the mesh using GitHub-style query syntax. Returns JSON array.

**Query syntax:**
- Field filters: `nodeType:Agent`, `name:*sales*`, `status:active`
- Text search: `laptop` (searches all text fields)
- Path scope: `path:graph` (limits to subtree)
- Scope modifiers: `scope:children`, `scope:descendants`
- Wildcards: `name:*acme*`

**Examples:**
- `Search('nodeType:Agent')` - Find all agents
- `Search('laptop', '@graph')` - Search for 'laptop' under graph
- `Search('nodeType:Organization scope:descendants')` - All orgs

Results limited to 50 items.

# NavigateTo - Display Node

Displays a node's visual representation in the chat UI.

**IMPORTANT:** When users ask to 'show', 'display', or 'view' something:
1. Prefer `NavigateTo` over returning raw JSON
2. Keep text response minimal after displaying
3. The node renders with its configured layout area

**Example:**
- `NavigateTo('@graph/org1')` - Displays org1's visual view

# Update - Create/Modify Nodes

Creates or updates a node at a path. **Use with caution** - modifies persistent data.

**JSON fields:**
- `name`: Display name (required for new nodes)
- `description`: Brief description
- `nodeType`: Type of node (required for new nodes)
- `content`: Type-specific JSON data

**Examples:**
- Create: `Update('@graph/neworg', '{"name": "New Org", "nodeType": "Organization"}')`
- Update: `Update('@graph/existingorg', '{"description": "Updated"}')`
