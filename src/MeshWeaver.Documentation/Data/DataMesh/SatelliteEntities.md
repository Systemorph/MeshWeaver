---
Name: Satellite Entities
Category: Documentation
Description: How satellite entities — comments, approvals, access assignments, tracked changes, threads, and activities — attach to primary nodes via reserved sub-namespaces
Icon: /static/DocContent/DataMesh/SatelliteEntities/icon.svg
---

Every primary node can carry a family of related records — comments, approval decisions, access grants, discussion threads — without polluting the main node hierarchy. These are **satellite entities**: secondary data elements that attach to a primary node through a reserved `_SubNamespace/` prefix.
<svg viewBox="0 0 760 300" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="12">
  <defs>
    <marker id="se-arrow" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="#90a4ae"/>
    </marker>
  </defs>
  <rect x="0" y="0" width="760" height="300" rx="12" fill="#1a2030" opacity="0.6"/>
  <rect x="240" y="18" width="280" height="50" rx="10" fill="#1e88e5"/>
  <text x="380" y="39" text-anchor="middle" fill="#fff" font-weight="bold" font-size="14">Primary Node</text>
  <text x="380" y="57" text-anchor="middle" fill="#bbdefb" font-size="11">ACME/Projects/Alpha</text>
  <line x1="282" y1="68" x2="90" y2="178" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#se-arrow)" stroke-opacity="0.7"/>
  <line x1="318" y1="68" x2="198" y2="178" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#se-arrow)" stroke-opacity="0.7"/>
  <line x1="354" y1="68" x2="306" y2="178" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#se-arrow)" stroke-opacity="0.7"/>
  <line x1="380" y1="68" x2="414" y2="178" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#se-arrow)" stroke-opacity="0.7"/>
  <line x1="406" y1="68" x2="522" y2="178" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#se-arrow)" stroke-opacity="0.7"/>
  <line x1="442" y1="68" x2="630" y2="178" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#se-arrow)" stroke-opacity="0.7"/>
  <line x1="468" y1="68" x2="706" y2="178" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#se-arrow)" stroke-opacity="0.7"/>
  <rect x="28" y="178" width="126" height="58" rx="8" fill="#43a047"/>
  <text x="91" y="196" text-anchor="middle" fill="#fff" font-weight="bold" font-size="12">_Access</text>
  <text x="91" y="212" text-anchor="middle" fill="#c8e6c9" font-size="10">AccessAssignment</text>
  <text x="91" y="228" text-anchor="middle" fill="#a5d6a7" font-size="10">→ access</text>
  <rect x="136" y="178" width="126" height="58" rx="8" fill="#8e24aa"/>
  <text x="199" y="196" text-anchor="middle" fill="#fff" font-weight="bold" font-size="12">_Comment</text>
  <text x="199" y="212" text-anchor="middle" fill="#e1bee7" font-size="10">Comment</text>
  <text x="199" y="228" text-anchor="middle" fill="#ce93d8" font-size="10">→ comments</text>
  <rect x="244" y="178" width="126" height="58" rx="8" fill="#5c6bc0"/>
  <text x="307" y="196" text-anchor="middle" fill="#fff" font-weight="bold" font-size="12">_Tracking</text>
  <text x="307" y="212" text-anchor="middle" fill="#c5cae9" font-size="10">TrackedChange</text>
  <text x="307" y="228" text-anchor="middle" fill="#9fa8da" font-size="10">→ tracking</text>
  <rect x="352" y="178" width="126" height="58" rx="8" fill="#f57c00"/>
  <text x="415" y="196" text-anchor="middle" fill="#fff" font-weight="bold" font-size="12">_Approval</text>
  <text x="415" y="212" text-anchor="middle" fill="#ffe0b2" font-size="10">Approval</text>
  <text x="415" y="228" text-anchor="middle" fill="#ffcc80" font-size="10">→ approvals</text>
  <rect x="460" y="178" width="126" height="58" rx="8" fill="#26a69a"/>
  <text x="523" y="196" text-anchor="middle" fill="#fff" font-weight="bold" font-size="12">_Thread</text>
  <text x="523" y="212" text-anchor="middle" fill="#b2dfdb" font-size="10">Thread</text>
  <text x="523" y="228" text-anchor="middle" fill="#80cbc4" font-size="10">→ threads</text>
  <rect x="568" y="178" width="126" height="58" rx="8" fill="#e53935"/>
  <text x="631" y="196" text-anchor="middle" fill="#fff" font-weight="bold" font-size="12">_Activity</text>
  <text x="631" y="212" text-anchor="middle" fill="#ffcdd2" font-size="10">Activity</text>
  <text x="631" y="228" text-anchor="middle" fill="#ef9a9a" font-size="10">→ activities</text>
  <rect x="640" y="178" width="112" height="58" rx="8" fill="#546e7a"/>
  <text x="696" y="196" text-anchor="middle" fill="#fff" font-weight="bold" font-size="11">_UserActivity</text>
  <text x="696" y="212" text-anchor="middle" fill="#cfd8dc" font-size="10">UserActivity</text>
  <text x="696" y="228" text-anchor="middle" fill="#b0bec5" font-size="10">→ user_activities</text>
  <text x="91" y="264" text-anchor="middle" fill="#e0e0e0" font-size="10">MainNode =</text>
  <text x="91" y="276" text-anchor="middle" fill="#90caf9" font-size="10">ACME/Projects/Alpha</text>
  <text x="415" y="264" text-anchor="middle" fill="#e0e0e0" font-size="10">MainNode =</text>
  <text x="415" y="276" text-anchor="middle" fill="#90caf9" font-size="10">ACME/Projects/Alpha</text>
  <text x="631" y="264" text-anchor="middle" fill="#e0e0e0" font-size="10">MainNode =</text>
  <text x="631" y="276" text-anchor="middle" fill="#90caf9" font-size="10">ACME/Projects/Alpha</text>
</svg>
*Each satellite sub-namespace routes to its own PostgreSQL table; all satellites carry `MainNode` pointing back to the primary node.*

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
