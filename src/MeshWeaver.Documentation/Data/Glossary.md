---
Name: Glossary
Category: Documentation
Description: The MeshWeaver vocabulary — mesh node, partition, satellite, hub, workspace, stream, and friends — each with a one-paragraph definition and a pointer to the page that owns it.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20"/><path d="M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2z"/></svg>
---

# Glossary

The dozen words that carry the whole platform. Each entry says what the thing *is* in one breath and links to the page that owns the full story.

## The data model

| Term | Meaning |
|---|---|
| **Mesh node** | The universal unit of content: a path, a name, a `NodeType`, and a typed `Content` payload. Documents, threads, agents, dimensions, notifications — everything is a mesh node. |
| **Path / namespace** | A node's address, e.g. `acme/Stories/launch-plan`. The namespace is everything before the last segment; the first segment names the partition. See [Unified Path](/Doc/DataMesh/UnifiedPath). |
| **Partition** | A unit of physical storage isolation rooted at a top-level path segment (`acme/…`, `rbuergi/…`, `Doc/…`). On Postgres, one schema per partition. Created only by partition-owning node types (User, Space). See [Partition Storage Routing](/Doc/Architecture/PartitionStorageRouting). |
| **NodeType** | The type of a node — what its `Content` looks like, how it renders, how it persists. Node types are themselves data: authored, versioned, and compiled at runtime. See [Node Types](/Doc/DataMesh/NodeTypes). |
| **Satellite** | A node living under a main node in a `_Segment` namespace (`_Thread`, `_Comment`, `_Access`, `_Notification`, …) with `MainNode` pointing back. Inherits the main node's access; routes to its own storage table. See [Satellite Entity Patterns](/Doc/Architecture/SatelliteEntityPatterns). |
| **Main node** | The content node a satellite belongs to — the access-control anchor: who can read the main node can read its satellites. |
| **Dimension** | A reference-data node type whose instances populate pickers and group facts in cubes and reports — countries, lines of business, … See [Data Modeling](/Doc/DataMesh/DataModeling). |

## The runtime

| Term | Meaning |
|---|---|
| **Hub (message hub)** | The actor: a single-threaded message processor with an address. Every node has an owning hub; hubs talk only via typed messages. See [Actor Model](/Doc/Architecture/ActorModel). |
| **Owning hub** | The one hub authoritative for a node's state. All writes — local or from other silos — are serialised through its action block. |
| **Hosted hub** | A child hub created inside another (`_Exec` for streaming work, kernels, …). Shares the parent's lifetime. |
| **Workspace** | A hub's typed data context: entity collections, streams, and the node-stream API (`workspace.GetMeshNodeStream(path)`). See [Workspace References](/Doc/Architecture/WorkspaceReferences). |
| **Stream / handle** | The live `IObservable<MeshNode>` for one path, with `.Update(...)` write-back — one shared handle per path per silo via the stream cache. See [MeshNode Stream Cache](/Doc/Architecture/MeshNodeStreamCache). |
| **Silo** | One process of the distributed mesh (Orleans host or the monolith). Each silo runs one stream cache; grains/hubs inside it share handles. |
| **Mesh** | The whole graph of nodes + the runtime that routes messages among their hubs, across silos. |
| **Query** | The eventually-consistent read-side index for *sets* (`nodeType:…`, `namespace:…`, free text → vector search). Never for a single known node's content. See [CQRS](/Doc/Architecture/CqrsAndContentAccess). |
| **Layout area** | A named, addressable view a hub renders as a control tree; the Blazor client binds to it by route `@{address}/{area}`. See [Layout Areas](/Doc/GUI/LayoutAreas). |
| **Kernel** | The C# scripting host behind executable Code nodes and interactive markdown. See [Script Execution](/Doc/Architecture/ScriptExecution). |

## The control plane

| Term | Meaning |
|---|---|
| **`stream.Update`** | The one mutation API: `GetMeshNodeStream(path).Update(node => changed).Subscribe(...)`. Cross-hub it ships an RFC 7396 merge patch. |
| **`Status` / `RequestedStatus`** | The state-machine pair: callers patch `RequestedStatus` (the ask); only the owning hub writes `Status` (the truth) — its watcher reacts. See [Activity Control Plane](/Doc/Architecture/ActivityControlPlane). |
| **Activity** | A satellite node representing a unit of long-running work — inputs, progress messages, terminal output — driven through `RequestedStatus`. |
| **Thread** | A conversation node (`_Thread` satellite) whose rounds are executed by agents; submissions go through `hub.StartThread` / `hub.SubmitMessage`. See [Thread Operations](/Doc/Architecture/ThreadOperations). |
| **Agent** | An AI participant defined as a mesh node under `Agent/…`, acting through the same APIs as users. See [Agentic AI Architecture](/Doc/Architecture/AgenticAI). |
| **AccessAssignment** | The permission grant: a node in a `_Access` namespace giving a user/group roles at that scope. See [Granting Access](/Doc/Architecture/GrantingAccess). |
| **AccessContext** | The identity riding every operation; framework write primitives carry it across `Subscribe` boundaries. See [AccessContext Propagation](/Doc/Architecture/AccessContextPropagation). |
| **Owner Injection** | The node OWNER (from `CreatedBy`) is the standing access context on a node/thread/activity hub — injected everywhere, carried forward via `CircuitContext`. Genuine infra (doc sync) = System; an empty context is rejected, never faked. See [Owner Injection](/Doc/Architecture/OwnerInjection). |
| **IoPool** | The bounded bridge where async/blocking I/O enters the reactive world — the only place `Observable.FromAsync` exists. See [Controlled I/O Pooling](/Doc/Architecture/ControlledIoPooling). |
