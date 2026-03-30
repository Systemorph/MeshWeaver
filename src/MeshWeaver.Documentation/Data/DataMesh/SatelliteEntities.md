---
Name: Satellite Entities
Category: Documentation
Description: How satellite entities (comments, approvals, access, tracking, threads, activities) are organized in sub-namespaces alongside primary nodes
Icon: /static/DocContent/DataMesh/SatelliteEntities/icon.svg
---

Satellite entities are secondary data elements that attach to a primary node through a dedicated sub-namespace. They enable features like comments, access control, approval workflows, and activity tracking without cluttering the main node hierarchy.

# What Are Satellite Entities?

Every primary node (e.g., `ACME/Projects/Alpha`) can have associated satellite data stored in sub-namespaces prefixed with `_`. For example:

```
ACME/Projects/Alpha                        ← Primary node
ACME/Projects/Alpha/_Comment/c1            ← Comment on Alpha
ACME/Projects/Alpha/_Access/Alice_Access   ← Access assignment for Alice
ACME/Projects/Alpha/_Approval/a1           ← Approval record
ACME/Projects/Alpha/_Thread/abc123         ← Discussion thread
ACME/Projects/Alpha/_Tracking/tc1          ← Tracked change (suggestion)
ACME/Projects/Alpha/_Activity/act1         ← Activity log entry
```

Satellite entities reference their parent via the `MainNode` property, which points back to the primary node path.

**CRITICAL:** When creating satellite nodes in code, always set `MainNode` explicitly to the content entity path. Without this, the node's path becomes its identity for access control, which fails for nested satellites (sub-threads, thread messages). Example:

```csharp
// CORRECT: MainNode points to content entity
var threadNode = new MeshNode(threadId, $"{contextPath}/_Thread")
{
    NodeType = "Thread",
    MainNode = contextPath,  // "PartnerRe/AiConsulting" — the real entity
    Content = new Thread { ParentPath = contextPath }
};

// WRONG: omitting MainNode — defaults to self, access control fails
var threadNode = new MeshNode(threadId, $"{contextPath}/_Thread")
{
    NodeType = "Thread",
    Content = new Thread { ParentPath = contextPath }
};
// MainNode defaults to "PartnerRe/AiConsulting/_Thread/threadId" — not a real entity
```

# Sub-Namespace Conventions

Each satellite type has a reserved sub-namespace prefix:

| Sub-Namespace | Node Type | Purpose |
|---------------|-----------|---------|
| `_Access` | AccessAssignment | Permission grants and denials (see [Access Control](../../Architecture/AccessControl)) |
| `_Comment` | Comment | Document comments and replies (see [Collaborative Editing](../CollaborativeEditing)) |
| `_Tracking` | TrackedChange | Suggested edits / track changes |
| `_Approval` | Approval | Approval workflow records |
| `_Thread` | Thread | Chat and discussion threads |
| `_Activity` | Activity | Node lifecycle events (created, updated, deleted) |
| `_UserActivity` | UserActivity | Per-user access tracking and history |
| `_Source` | Code | Source code files (.cs) attached to node types |
| `_Test` | Code | Test code files (.cs) for node type testing |

# File System Layout

On disk, satellite entities live in `_SubNamespace/` directories within their parent node's directory:

```
ACME/
  index.md                              ← ACME organization node
  _Access/
    Public_Access.json                  ← All authenticated users: Viewer
    Alice_Access.json                   ← Alice: Editor
  Projects/
    Alpha/
      index.md                          ← Alpha project node
      _Source/
        Alpha.cs                        ← Source code for Alpha
        AlphaLayoutAreas.cs             ← Layout area definitions
      _Comment/
        c1.json                         ← Comment on Alpha
        c1/
          reply1.json                   ← Reply to comment c1
        c2.json                         ← Another comment
      _Approval/
        a1.json                         ← Approval record
      _Thread/
        abc123.json                     ← Discussion thread
      _Access/
        Bob_Access.json                 ← Bob: Viewer on Alpha
```

Replies to comments are nested as children of the comment node (e.g., `_Comment/c1/reply1.json`).
Source code files (`.cs`) live in `_Source/` directories and test code in `_Test/` directories.

# PostgreSQL Table Routing

In PostgreSQL, satellite entities are stored in **dedicated tables** within the same schema as their parent partition. This is configured via `PartitionDefinition.StandardTableMappings`:

| Sub-Namespace | Table Name | Description |
|---------------|------------|-------------|
| `_Activity` | `activities` | Activity log entries |
| `_UserActivity` | `user_activities` | User access records |
| `_Thread` | `threads` | Threads and thread messages |
| `_Tracking` | `tracking` | Track change records |
| `_Approval` | `approvals` | Approval records |
| `_Access` | `access` | Access assignments |
| `_Comment` | `comments` | Comments and replies |

Primary entities (where `MainNode == Path` or no satellite prefix matches) go to the `mesh_nodes` table.

Each satellite table has the same column schema as `mesh_nodes`, including a `main_node` column that references the parent entity. An index on `main_node` enables efficient queries like "get all comments for this document."

```csharp
// Table routing example
var def = new PartitionDefinition
{
    Namespace = "ACME",
    Schema = "acme",
    TableMappings = PartitionDefinition.StandardTableMappings
};

def.ResolveTable("ACME/Projects/Alpha")                    // → "mesh_nodes"
def.ResolveTable("ACME/Projects/Alpha/_Comment/c1")         // → "comments"
def.ResolveTable("ACME/Projects/Alpha/_Access/Alice_Access") // → "access"
def.ResolveTable("ACME/Projects/Alpha/_Activity/act1")       // → "activities"
```

# Creating Satellite Entities

Satellite entities are created like any other MeshNode, with the namespace set to include the satellite sub-namespace:

```csharp
// Create a comment on a document
var comment = new MeshNode("c1", "ACME/Docs/readme/_Comment")
{
    Name = "Great document!",
    NodeType = "Comment",
    MainNode = "ACME/Docs/readme",
    Content = new { Author = "alice", Text = "This is really helpful!" }
};
await persistence.SaveNodeAsync(comment, options, ct);
```

The `MainNode` property links the satellite back to its primary entity. This is used for:
- **Permission delegation**: Satellite entities inherit access from their parent
- **Query filtering**: Find all satellites for a given primary node
- **Lifecycle management**: Deleting a primary node can cascade to its satellites

# Satellite Types in Detail

## Access Assignments (`_Access`)

Control who can read, edit, or administer a node and its descendants. See [Access Control Architecture](../../Architecture/AccessControl) for the full permission model.

```json
{
  "id": "Alice_Access",
  "namespace": "ACME/_Access",
  "nodeType": "AccessAssignment",
  "mainNode": "ACME",
  "content": {
    "accessObject": "Alice",
    "displayName": "Alice Chen",
    "roles": [{ "role": "Editor" }]
  }
}
```

## Comments (`_Comment`)

Anchored to text ranges in markdown documents via inline markers. See [Collaborative Editing](../CollaborativeEditing).

## Tracked Changes (`_Tracking`)

Suggested insertions and deletions in collaborative documents. Each TrackedChange records the author, change type, and acceptance status.

## Approvals (`_Approval`)

Workflow records for approval processes. Each approval captures the approver, decision, and timestamp.

## Threads (`_Thread`)

Discussion threads attached to any node. Thread messages are children of the thread node.

## Activities (`_Activity`)

Immutable log entries recording node lifecycle events (created, updated, deleted). Used for audit trails and activity feeds.

## User Activities (`_UserActivity`)

Per-user access records tracking when a user last viewed or edited a node. Used for "recently accessed" features and personalized navigation.

# Best Practices

1. **Always use the correct sub-namespace** for satellite types — the routing layer depends on it for table separation
2. **Set `MainNode`** on every satellite entity to enable parent lookups and permission delegation
3. **Access assignments go in `_Access/`** — never at the root level of a namespace
4. **Comments support nesting** — replies are children of the comment node within `_Comment/`
5. **Satellite entities inherit permissions** from their primary node via `PrimaryNodePath`/`MainNode`
