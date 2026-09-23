---
Name: Data-Plane Messages Are Stream Plumbing
Category: Architecture
Description: GetDataRequest, GetDataResponse, DataChangeRequest, PatchDataChangeRequest and DataChangedEvent are how the data layer moves streams between hubs - not an API. Reading a node is GetMeshNodeStream, writing it is GetMeshNodeStream().Update, creating or replacing it is IMeshService. The inventory of every remaining caller, the per-kind migration recipe, the shrink-only ratchet, and the target - all five types internal to the data layer.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 6h16"/><path d="M4 12h10"/><path d="M4 18h6"/><path d="M17 15l3 3-3 3"/></svg>
---

# Data-Plane Messages Are Stream Plumbing

Five message types in `MeshWeaver.Data.Contract` carry data between hubs:

| Type | What the data layer uses it for |
|---|---|
| `GetDataRequest` / `GetDataResponse` | a one-shot answer for a `WorkspaceReference` at an address |
| `DataChangeRequest` | a set of instance updates / deletions applied by the owning workspace |
| `PatchDataChangeRequest` | a JSON patch a synchronization stream sends upstream to its owner |
| `DataChangedEvent` | a full or patch emission an owner sends to a subscribed stream |

They are the **wire format of the synchronization streams**. They are not an application API.
Application code — layout areas, handlers, services, Blazor views, NodeType sources — reads and writes
nodes through the stream surfaces, and those surfaces use these messages underneath:

| You want to… | Use | Not |
|---|---|---|
| read ONE node | `workspace.GetMeshNodeStream(path)` (+ `.ContentAs<T>(options)`) | `GetDataRequest(new MeshNodeReference())`, `GetDataRequest(new EntityReference(nameof(MeshNode), id))` |
| change a node that exists | `workspace.GetMeshNodeStream(path).Update(current => …)` | `DataChangeRequest { Updates = [node] }` |
| create, or replace wholesale | `IMeshService.CreateNode` / `CreateOrUpdateNode` | `DataChangeRequest` carrying a node that may not exist |
| read a reference on a hub you HOLD (a probe, your own hub) | `hub.GetWorkspace().GetNullableStream(reference)` | a `GetDataRequest` posted to that hub |

## Why this matters: the second path has its own identity surface

A bespoke `GetDataRequest`/`DataChangeRequest` is a second read/write path beside the sanctioned
one, and it re-implements — or forgets — everything the stream surfaces already do: whose identity the
delivery carries, where it is issued from, how an absent node is answered, how a concurrent write is
merged. The incident family *"Portal (reads) hub posts GetDataRequest to X with no AccessContext —
rejected at the identity gate"* ([#1253](https://github.com/Systemorph/MeshWeaver/issues/1253),
[#5431](https://github.com/Systemorph/MeshWeaver/issues/5431)–[#5439](https://github.com/Systemorph/MeshWeaver/issues/5439))
lives entirely on that second path.

The write side shows the same thing, measured. `UserActivityLayoutAreas.ClearUserBody` used to read
the user node once and post the whole node back as a `DataChangeRequest`. Run as the user
`bodyowner`, the owner recorded the write as **`LastModifiedBy = "system-security"`** — the
authorship the earlier read happened to return — and the router logged `ROUTER_TRAFFIC ORIGIN:
DataChangeRequest was POSTED with the mesh hub as sender`. Through
`GetMeshNodeStream(path).Update(…)` the same write is stamped `bodyowner`
(`NodeWritesGoThroughTheStreamTest`, which fails on the old path with exactly that message). The
stream write also applies the change to the node's **current** state, so a field the caller did not
touch is never overwritten from a stale copy.

## The target

1. **Every caller outside the data layer is converted.** The data layer is
   `src/MeshWeaver.Data` + `src/MeshWeaver.Data.Contract` — where the types are defined, registered
   in the type registry and served.
2. **`[Obsolete]`** on the five types once core's own callers are gone, pointing at the table above.
   It lands only then, because under `-warnaserror` every remaining reference would otherwise need a
   suppression.
3. **`internal`**, with `InternalsVisibleTo` for the data layer's own assembly (`MeshWeaver.Data`)
   and its test projects, and nothing else — when core, MeshWeaver.Plugins, every satellite and the
   reachable live mesh are at zero.

Before step 3, a test must show that an `internal` `GetDataRequest`/`GetDataResponse` still
round-trips across hubs — type-registry registration by `typeof` (in `DataExtensions`) and the
polymorphic `$type` JSON both work on non-public types — and across an Orleans grain boundary.
Step 3 removes public surface, so its pull request carries `Pairs-with:` and the in-mesh sweep
evidence below.

🚨 **Why a ratchet and never a delete:** in-mesh C# — NodeType `Source/*.cs`, scripts, cells
installed from a `cellSurface: true` course — compiles at RUNTIME and is invisible to `dotnet build`
and to any source scan. A type that disappears breaks those callers silently, in production
(AGENTS.md: *Green CI does NOT mean the mesh compiles*).

## The ratchet

`DataPlaneMessageRatchetGuard` (`test/MeshWeaver.Documentation.Test`) scans `src/`, `memex/` and
`tools/`, excluding the data layer, and counts code references to each of the five types (comments
and strings masked). `test/DataPlaneMessageSites.allow` lists every file that still carries one, per
type, with its count; the guard's `TotalBudget` caps each type's sum. A new file, a higher count, or
a grown total is RED. A count that dropped is reported as STALE — lower the line and the budget in
the same change.

## Inventory

Counts are code references (a handler registration, a parameter type and a construction each count
one). Measured on `main` at `39f49494eb` (core) and `e9d9283ab` (MeshWeaver.Plugins).

### Core production code — after the first conversion

| Type | Refs | Files |
|---|---:|---|
| `GetDataRequest` | 18 | 9 |
| `GetDataResponse` | 26 | 8 |
| `DataChangeRequest` | 4 | 3 |
| `PatchDataChangeRequest` | 0 | — |
| `DataChangedEvent` | 0 | — |

`PatchDataChangeRequest` and `DataChangedEvent` are **already at the target** in core production
code: nothing outside `MeshWeaver.Data` references either. They are the upstream patch and the
downstream emission of `SynchronizationStream`, and no application code observes them. (The only
references outside the data layer are tests — 2 and 25 in core, 0 and 4 in Plugins.)

### Core production code — remaining sites by kind

| Site | Kind | Role | Stream surface | Plan |
|---|---|---|---|---|
| `MeshNodeStreamExtensions.GetMeshNodeOutcome` | `MeshNodeReference` | **the** one-shot node read behind `hub.GetMeshNode(...)` (100+ callers) | `IMeshNodeStreamCache` | own change, after [#5444](https://github.com/Systemorph/MeshWeaver/pull/5444) (which fixes this read's re-probe identity) — the stream cache answers an absent path by a routing NotFound that opens the storm-breaker, while `GetMeshNodeOutcome` must keep `Absent` / `DeleteInProgress` / `Unavailable` distinct |
| `MeshDataSource` — absence pipeline step, NodeType schema handler | server side of the above and of `SchemaReference` | answers | — | goes with its callers |
| `ContentFileResolver`, `ContentStaging`, `HubStreamProviderFactory`, `MeshOperations` ×3, handler in `ContentCollectionsExtensions` | `ContentCollectionReference` | "which content collections does node X serve" | the owner already reduces `ContentCollectionReference` as a workspace stream (`CreateContentCollectionReferenceStream`) | one platform read surface for a node's collection configs, then migrate all six callers together |
| `MeshOperations` Unified Path resolution (`get @X/data:…`, `layoutAreas:`, `schema:`, `collection:`) + handlers in `LayoutExtensions` (`layoutAreas:`) and `GraphConfigurationExtensions` (`NodeTypeReference`) | `UnifiedReference`, `NodeTypeReference` | a generic "resolve this reference at that address" | partly: `StandardReducers` reduces `data:` and `content:`; `layoutAreas:` and `NodeTypeReference` have NO reducer | needs a platform decision on the MCP `get` read surface; recorded, not forced |
| `MeshExtensions` ×2 (post-create / confirm announcements) | `DataChangeRequest` | framework-internal fan-out of an already-persisted node to its owner's workspace, carried as `WellKnownUsers.SystemContext` | `GetMeshNodeStream(path).Update` would re-persist a node persistence just wrote | own change, in the node-CRUD core (#1140, #4061 history) |
| `LayoutClientExtensions.SubmitModel` | `DataChangeRequest` | a form's model submitted to the layout area's owner | `ISynchronizationStream.Update` | own change with the layout-client submit path |
| `VersionLayoutArea` (undo) | `DataChangeRequest` | restores N nodes to earlier versions | `IMeshService.CreateOrUpdateNode` per node | needs a test surface with version history (Postgres, MeshWeaver.Plugins) — the in-memory backend refuses undo |

### MeshWeaver.Plugins production code

| Site | Kind | Recipe |
|---|---|---|
| `Markdown.Export/Layout/ExportDocumentLayoutArea` | node read (`EntityReference(MeshNode)`) | `GetMeshNodeStream(path)` |
| `AI/ThreadLayoutAreas` | node read (`MeshNodeReference`) | `GetMeshNodeStream(path)` |
| `Graph.Views/PinViews` ×2 | node write | `GetMeshNodeStream(path).Update` |
| `Graph.Views/AccessAssignmentLayoutAreas` ×4 (`RequestChange`, one a deletion) | node write / delete | `Update`; the deletion is `IMeshService.DeleteNode` |
| `Graph.Views/CodeViews` ×2 | node write | `GetMeshNodeStream(path).Update` |
| `Blazor.Graph/MeshNodeCollectionView` | node write | `GetMeshNodeStream(path).Update` |
| `Import/Implementation/ImportManager` ×2 | writes of import configs | per site — config nodes vs. imported instances |
| `ContentCollections.Indexing.Graph/ContentIndexingObserver` | `ContentCollectionReference` | the core collection-config surface, once it exists |

That is 18 production references in 8 files. The rest of Plugins' references — 123
`GetDataRequest`, 15 `GetDataResponse`, 29 `DataChangeRequest` and 4 `DataChangedEvent` — are in
test projects, most of them reading one node to assert on it; the recipe there is the same
`GetMeshNodeStream(path)` wait-on-condition shown in
[Satellite Entity Patterns](../SatelliteEntityPatterns).

### Outside both source trees

| Where | Found |
|---|---|
| core `samples/` (all file types) | 0 |
| MeshWeaver.Plugins outside `src/` (module content, node JSON) | 0 |
| MeshWeaver.Crm, .Education, .FundReporting, .Manufacturing, .Reinsurance, .SocialMedia, Memex (`origin/main`, all file types) | 0 |
| live mesh | **not established.** `search_chunks` needs an anchored scope and cannot sweep every partition; the node `search` is semantic, so a hit list is not a literal match and an empty one would not be a zero. The control instance's MCP was unreachable (503). This sweep must be completed — literally, with `searched: true` — before step 3. |

## What changed first

| Converted | From | To |
|---|---|---|
| `NodeTypeDataModelAreas.ProbeInstanceModel` | `GetDataRequest(SchemaReference)` posted to its own probe hub | the probe's `GetNullableStream(new SchemaReference())` — its first emission is also the init gate |
| `MeshDataSource` NodeType schema probe | same | same |
| `NodeTypeLayoutAreas` Hub Configuration **Save** | read the node, post it back as a `DataChangeRequest` | `GetMeshNodeStream().Update(current => ApplyHubConfigForm(current, form))` |
| `AccessControlLayoutArea` add-assignment | `DataChangeRequest` carrying the new node to the CURRENT node's hub | `IMeshService.CreateOrUpdateNode` (same create-or-replace semantics — the id is derived from the subject) |
| `UserActivityLayoutAreas` reset / clear home body | `GetMeshNode` + `DataChangeRequest` | `ClearUserBody` → `GetMeshNodeStream(path).Update` |
| `SatelliteEntityPatterns` verification example | `GetDataRequest(EntityReference(MeshNode))` | `GetMeshNodeStream(path)` + wait on the condition |

Related: [CQRS — Queries vs. Content Access](../CqrsAndContentAccess) ·
[Data Access Patterns](../DataAccessPatterns) · [The Read Path Minted a Hub Per Read](../ReadPathStreamMinting) ·
[AccessContext Propagation](../AccessContextPropagation).
