---
Name: Satellite Entities
Category: Documentation
Description: How satellite entities — comments, approvals, access assignments, tracked changes, threads, and activities — attach to primary nodes via reserved sub-namespaces
Icon: /static/DocContent/DataMesh/SatelliteEntities/icon.svg
---

Every primary node can carry a family of related records — comments, approval decisions, access grants, discussion threads — without polluting the main node hierarchy. These are **satellite entities**: secondary data elements that attach to a primary node through a reserved `_SubNamespace/` prefix.

# What Are Satellite Entities?

Consider a project node at `ACME/Projects/Alpha`. Its satellite data lives in sub-namespaces directly beneath it:

```
ACME/Projects/Alpha                        ← Primary node
ACME/Projects/Alpha/_Comment/c1            ← Comment on Alpha
ACME/Projects/Alpha/_Access/Alice_Access   ← Access assignment for Alice
ACME/Projects/Alpha/_Approval/a1           ← Approval record
ACME/Projects/Alpha/_Thread/abc123         ← Discussion thread
ACME/Projects/Alpha/_Tracking/tc1          ← Tracked change (suggestion)
ACME/Projects/Alpha/_Activity/act1         ← Activity log entry
```

Each satellite entity links back to its parent via the `MainNode` property, which always points to the primary node path — not to the satellite namespace itself.

> **CRITICAL — always set `MainNode` explicitly.** Without it, `MainNode` defaults to the satellite's own path, which breaks access control for nested satellites (sub-threads, thread messages, replies).

```csharp
// CORRECT: MainNode points to the content entity
var threadNode = new MeshNode(threadId, $"{contextPath}/_Thread")
{
    NodeType = "Thread",
    MainNode = contextPath,  // e.g. "PartnerRe/AiConsulting" — the real entity
    Content = new Thread()
};

// WRONG: omitting MainNode — defaults to self, access control fails
var threadNode = new MeshNode(threadId, $"{contextPath}/_Thread")
{
    NodeType = "Thread",
    Content = new Thread()
};
// MainNode defaults to "PartnerRe/AiConsulting/_Thread/threadId" — not a real entity
```

# Sub-Namespace Conventions

Each satellite type has a reserved sub-namespace prefix. The routing layer depends on these prefixes for both storage table selection and permission delegation.

| Sub-Namespace | Node Type | Purpose |
|---|---|---|
| `_Access` | AccessAssignment | Permission grants and denials — see [Access Control](../../Architecture/AccessControl) |
| `_Comment` | Comment | Document comments and replies — see [Collaborative Editing](../CollaborativeEditing) |
| `_Tracking` | TrackedChange | Suggested edits and track changes |
| `_Approval` | Approval | Approval workflow records |
| `_Thread` | Thread | Chat and discussion threads |
| `_Activity` | Activity | Node lifecycle events (created, updated, deleted) |
| `_UserActivity` | UserActivity | Per-user access tracking and history |

> **Note:** `Source/` and `Test/` sub-namespaces exist under NodeTypes and hold **primary** Code nodes (source files and tests), not satellite metadata. They share the same `code` PostgreSQL table as a storage optimization, but semantically they are primary content, not satellites.

# File System Layout

On disk, satellite entities live in `_SubNamespace/` directories alongside their parent node's `index.md`:

```
ACME/
  index.md                              ← ACME organization node
  _Access/
    Public_Access.json                  ← All authenticated users: Viewer
    Alice_Access.json                   ← Alice: Editor
  Projects/
    Alpha/
      index.md                          ← Alpha project node
      Source/
        Alpha.cs                        ← Source code for Alpha (primary content, not satellite)
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

Replies to comments are nested as children of the comment node (e.g., `_Comment/c1/reply1.json`). Source and test code files live in `Source/` and `Test/` directories — these are primary content even though they share the `code` table for routing purposes.

# PostgreSQL Table Routing

In PostgreSQL, satellite entities are stored in **dedicated tables** within the same schema as their parent partition. This separation enables efficient index-based lookups — for example, "get all comments for this document" via the `main_node` column index.

Configuration lives in `PartitionDefinition.StandardTableMappings`:

| Sub-Namespace | Table | Description |
|---|---|---|
| `_Activity` | `activities` | Activity log entries |
| `_UserActivity` | `user_activities` | User access records |
| `_Thread` | `threads` | Threads and thread messages |
| `_Tracking` | `tracking` | Track change records |
| `_Approval` | `approvals` | Approval records |
| `_Access` | `access` | Access assignments |
| `_Comment` | `comments` | Comments and replies |

Primary entities (where `MainNode == Path`, or where no satellite prefix matches) go to the `mesh_nodes` table.

Each satellite table shares the same column schema as `mesh_nodes`, including the indexed `main_node` column.

```csharp
// Table routing example
var def = new PartitionDefinition
{
    Namespace = "ACME",
    Schema = "acme",
    TableMappings = PartitionDefinition.StandardTableMappings
};

def.ResolveTable("ACME/Projects/Alpha")                     // → "mesh_nodes"
def.ResolveTable("ACME/Projects/Alpha/_Comment/c1")         // → "comments"
def.ResolveTable("ACME/Projects/Alpha/_Access/Alice_Access") // → "access"
def.ResolveTable("ACME/Projects/Alpha/_Activity/act1")       // → "activities"
```

# Creating Satellite Entities

Satellite entities are created like any other `MeshNode`. Set the namespace to include the satellite sub-namespace, and always set `MainNode`:

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

The `MainNode` property drives three key behaviours:

- **Permission delegation** — satellites inherit access from their primary node
- **Query filtering** — find all satellites for a given primary node via `mainNode:{path}`
- **Lifecycle management** — deleting a primary node can cascade to its satellites

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

Anchored to text ranges in markdown documents via inline markers. Replies nest as children of the comment node within `_Comment/`. See [Collaborative Editing](../CollaborativeEditing).

## Tracked Changes (`_Tracking`)

Suggested insertions and deletions in collaborative documents. Each `TrackedChange` records the author, change type, and acceptance status.

## Approvals (`_Approval`)

Workflow records for approval processes. Each approval captures the approver, decision, and timestamp.

## Threads (`_Thread`)

Discussion threads attached to any node. Thread messages are children of the thread node itself.

## Activities (`_Activity`)

Immutable log entries recording node lifecycle events (created, updated, deleted). Used for audit trails and activity feeds.

## User Activities (`_UserActivity`)

Per-user access records tracking when a user last viewed or edited a node. Drives "recently accessed" features and personalized navigation.

# Best Practices

1. **Use the correct sub-namespace** — the routing layer depends on it for table separation and query efficiency.
2. **Always set `MainNode`** — enables parent lookups, permission delegation, and cascade deletes.
3. **Access assignments go in `_Access/`** — never at the root level of a namespace.
4. **Comments support nesting** — replies are children of the comment node within `_Comment/`.
5. **Satellite entities inherit permissions** from their primary node via `MainNode`.
