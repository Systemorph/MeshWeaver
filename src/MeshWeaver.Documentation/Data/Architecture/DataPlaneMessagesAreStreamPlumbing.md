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
| read a reference on a hub you HOLD (a probe, your own hub) | `hub.GetWorkspace().GetNullableStream(reference)` — after the hub has STARTED if it is still initializing | a `GetDataRequest` posted to that hub |

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

`DataPlaneMessageRatchetGuard` (`test/MeshWeaver.Documentation.Test`) scans `src/`, `memex/`,
`tools/` and `samples/` (whose `Data/` trees are in-mesh C#), excluding the data layer, and counts code references to each of the five types (comments
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
| `DataChangeRequest` | 11 | 5 (incl. 2 in `samples/`) |
| `PatchDataChangeRequest` | 0 | — |
| `DataChangedEvent` | 0 | — |

`PatchDataChangeRequest` and `DataChangedEvent` are **already at the target** in core production
code: nothing outside `MeshWeaver.Data` references either. They are the upstream patch and the
downstream emission of `SynchronizationStream`, and no application C# observes them (the only
C# references outside the data layer are tests — 2 and 25 in core, 0 and 4 in Plugins). But
`DataChangedEvent` is ALSO the frame external clients decode — see below — so its wire name and
fields are frozen whatever its C# visibility.

### Core production code — remaining sites by kind

| Site | Kind | Role | Stream surface | Plan |
|---|---|---|---|---|
| `MeshNodeStreamExtensions.GetMeshNodeOutcome` | `MeshNodeReference` | **the** one-shot node read behind `hub.GetMeshNode(...)` (100+ callers) | `IMeshNodeStreamCache` — **not yet equivalent**, see [Moving GetMeshNode onto the node stream](#moving-getmeshnode-onto-the-node-stream) | blocked on a platform decision: the two reads do not grant the same things |
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

### Outside the two `src/` trees

Swept with `git grep -w` on each repository's `origin/main`, with a positive control (core `src/`:
523 lines) run in the same script — an empty answer from a sweep that could not have matched is
not a zero.

| Where | Found |
|---|---|
| core `samples/` | **7 code references, both in-mesh or sample C#.** `samples/Graph/Data/ACME/Project/Todo/Source/TodoLayoutAreas.cs` (1, a NodeType source compiled at runtime) and `samples/Todo/MeshWeaver.Todo/LayoutAreas/TodoLayoutAreas.cs` (6, the Todo sample app writing workspace entities to its app hub). Both are in the ratchet — the guard scans `samples/` because its `Data/` trees are exactly the code `dotnet build` never sees. The rest are prose in sample `.md` files. |
| MeshWeaver.Plugins module folders (`Mail/`, `Store/`, …) | code: 0 (one doc comment in `Store/Installer/Source/DependencyInstall.cs`) |
| MeshWeaver.Plugins `clients/` (TypeScript) | **`DataChangedEvent` is a WIRE CONTRACT.** The grpc-web and React clients decode `DataChangedEvent { changeType, change, streamId }` frames by their `$type` name (`clients/grpc-web/src/changeFold.ts`, `connection.test.ts`, `clients/react/docs/live-protocol.md`). Making the CLR type `internal` does not change the wire, but its NAME and its field names can never change — so it is plumbing to C# code and a published protocol to clients. |
| MeshWeaver.SocialMedia | **14 in-mesh callers**: every `LinkedIn/*/Source/*Loader.cs` reads its CSV through `hub.Observe<GetDataResponse>(new GetDataRequest(new FileReference("content", "archive/….csv")))` — the `FileReference` kind, which has no stream surface yet |
| MeshWeaver.Reinsurance | 2, both under `legacy/` |
| Memex | 0 code (2 doc comments) |
| MeshWeaver.Crm, .Education, .FundReporting, .Manufacturing | 0 |
| live mesh | **not established.** `search_chunks` needs an anchored scope and cannot sweep every partition; the node `search` is semantic, so a hit list is not a literal match and an empty one would not be a zero. The control instance's MCP was unreachable (503). This sweep must be completed — literally, with `searched: true` — before step 3. |

## Moving GetMeshNode onto the node stream

`GetMeshNodeOutcome` is the last and largest node read on `GetDataRequest`. Its recycling
re-probe lost the read's identity until #5444; whether the wider incident family
(#5431–#5439: `GetDataRequest`s from `portal/reads-*` to partition roots such as `Mail`,
`Stripe`, `ThreeBody`) goes through this method or another `GetDataRequest` issued on the read
hub is NOT established here — those log lines name the message and target, not the calling site. Swapping its transport for `IMeshNodeStreamCache` is **not** a
transport swap: the two reads decide different things, in different places.

| | `GetMeshNodeOutcome` (today) | `IMeshNodeStreamCache.GetStream` |
|---|---|---|
| who decides Read | the OWNER, per caller, on two paths: the `[RequiresPermission(Read)]` delivery gate (`AccessControlPipeline`), which re-evaluates a denied fold through `NodeTypeAccessRuleGate`; then the read-validator pipeline, where every `INodeValidator` for `NodeOperation.Read` runs — `RlsNodeValidator` resolving the type's access rule itself (`_accessRules.Find` → `HasAccess`, not through the gate), plus `SatelliteAccessRule` and the User/VUser/Space/Partition and `AddAccessRule` rule sets | the READER, locally: one shared upstream per path (opened under the cache identity), then `GateOnRead` on `GetEffectivePermissions` — the permission fold only, no node-type rules; skipped entirely for no-user contexts and type-definition paths |
| delete in flight | `Absence = DeleteInProgress` (owner tombstone, #1471) | no signal on the subscription protocol |
| absent path | `Absent`, no state kept | NotFound recorded in the storm-breaker's negative cache — reads AND writes of that path fast-fail for the backoff window |
| owner recycling | paced `ShuttingDown` re-probe within the caller's budget | transient-fault classification + transient breaker |

**Measured** (local, Monolith, the `SystemOwnedSyncConfigIsVisibleToPlatformAdminsTest` fixture,
reading a system-owned Space's `_GitSync` as the platform admin, whose fold on that path is `None`
and whose Read comes only from `GitHubSyncConfigAccessRule`):

```
GetMeshNodeOutcome:  Present
StreamCache:         UnauthorizedAccessException: User 'platform-boss' lacks Read permission on 'Owned…/_GitSync'
```

So moving `GetMeshNode` onto the cache as it stands would DENY reads a node-type rule grants — and,
for any type whose rule is narrower than the fold, ALLOW reads the owner refuses. That is a change to
the authorization model of 100+ reads, not a refactor, and it needs a decision before code:

1. **Reader-side rule parity.** Should the cache's read gate apply the node-type rules and the Read
   validators the way the owner's delivery gate and validator pipeline do? That also changes what every
   EXISTING `GetMeshNodeStream` reader sees — the measurement above is a live inconsistency today,
   independent of `GetMeshNode`.
2. **A delete tombstone on the subscription protocol**, so the stream can say `DeleteInProgress`.
3. **An absent-aware one-shot read on the cache** (`NodeReadOutcome` out, no negative-cache
   admission for a single bounded read), so an existence probe cannot arm the storm-breaker against
   the create that follows it. The `scope:children` listing is not a substitute for `GetMeshNode`:
   its negative can be minutes old, and read-after-create callers would read their own write as
   absent.

Until those land, `GetMeshNodeOutcome` stays on `GetDataRequest`, which after #5444 carries the
read's identity on every probe.

## What changed first

| Converted | From | To |
|---|---|---|
| `NodeTypeDataModelAreas.ProbeInstanceModel` | `GetDataRequest(SchemaReference)` posted to its own probe hub | wait for the probe's `RunLevelChanged` to reach `Started` (the gate the self-posted request used to provide), then read its `GetNullableStream(new SchemaReference())`. 🚨 The stream alone is NOT a gate: it can reduce off the empty store before the data sources initialize — the first version of this change did exactly that, and CI caught the probe being disposed before a virtual source's provider had subscribed |
| `MeshDataSource` NodeType schema probe | same | same |
| `NodeTypeLayoutAreas` Hub Configuration **Save** | read the node, post it back as a `DataChangeRequest` | `GetMeshNodeStream().Update(current => ApplyHubConfigForm(current, form))` |
| `AccessControlLayoutArea` add-assignment | `DataChangeRequest` carrying the new node to the CURRENT node's hub | `IMeshService.CreateOrUpdateNode` (same create-or-replace semantics — the id is derived from the subject) |
| `UserActivityLayoutAreas` reset / clear home body | `GetMeshNode` + `DataChangeRequest` | `ClearUserBody` → `GetMeshNodeStream(path).Update` |
| `SatelliteEntityPatterns` verification example | `GetDataRequest(EntityReference(MeshNode))` | `GetMeshNodeStream(path)` + wait on the condition |

A write issued from a callback — after a form stream emits, not on the click's own delivery turn —
captures the caller's `AccessContext` on the click and runs the cold write under
`access.RunAs(caller, () => …)`: the `AccessContext` is an `AsyncLocal`, and the stream write
stamps whatever is ambient when it is CALLED. Content is changed through the TYPED
`Update<TContent>((node, content) => …)`, which fails the write on present-but-unreadable content
instead of defaulting it — a read-tolerant `ContentAs<T>() ?? new T()` inside a write destroys
the record it cannot read.

Related: [CQRS — Queries vs. Content Access](../CqrsAndContentAccess) ·
[Data Access Patterns](../DataAccessPatterns) · [The Read Path Minted a Hub Per Read](../ReadPathStreamMinting) ·
[AccessContext Propagation](../AccessContextPropagation).
