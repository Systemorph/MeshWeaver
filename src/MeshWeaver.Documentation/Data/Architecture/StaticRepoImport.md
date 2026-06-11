---
Name: Static-Repo Import (content-addressed, as an Activity)
Category: Architecture
Description: How a static repository (embedded docs, built-in agents, the model catalog, seed graphs) is materialized into a mesh partition — full content + prerender + a Space root — idempotently per content-version, tracked as a content-addressed Activity, through the one canonical upsert verb.
Icon: DatabaseArrowDown
---

# Static-Repo Import

A **static repo** is authored content that ships *with the build* — embedded documentation (`MeshWeaver.Documentation`), the built-in agents (`MeshWeaver.AI`), the model catalog, sample graphs (`samples/Graph/Data`). It is the **source**, never the live serving copy. This doc defines the **one** way to get a static repo into a partition so the partition is **served from the database** like any other.

## The rule

> **A static repo is materialized into its partition through the single canonical upsert verb (`CreateOrUpdateNodeRequest`) — content + prerender + a `Space` root — idempotently per content-version, tracked as a content-addressed `Activity`, then reconciled (prune absent). No bespoke SQL, no hand-rolled `CreateNode`/stream-`Overwrite`, no per-instance races, no content-NULL shells.**

## How to import a static repo (the whole recipe)

To materialize partition **`P`** from a static repo:

1. **Implement `IStaticRepoSource`:**
   - `Partition` → **the target partition name** (e.g. `"Doc"`). This *is* the target; it defaults to the repo's own partition — there is no separate "target" argument, you set it here.
   - `Versioned` → `false` for authored content (fingerprint on content hash so an edited file re-imports); `true` if the nodes carry meaningful versions.
   - `EnumerateSourceNodes()` → the partition's **children, with full `Content`** (e.g. `MarkdownContent`). Children + satellites only — never the `namespace=""` root.
   - `PartitionRoot` (optional) → a curated `Space` root (`NodeType = "Space"`, `MarkdownContent` welcome). Return `null` to get a generic synthesized root.
2. **Register it:** `services.AddSingleton<IStaticRepoSource>(new MyRepoSource())`, gated behind `Features:StaticRepoSync:Partitions` via `AddStaticRepoSync(serveFromPartition)`. For a synced partition the in-memory read-only static provider is skipped so Postgres serves + accepts the import.
3. **That's it.** On boot, `StaticRepoImporter.ImportAll(hub)` runs every registered source. To import one source directly: `StaticRepoImporter.Import(hub, source)`.

Reference implementations: `DocumentationStaticRepoSource`, `AgentStaticRepoSource`, `ModelStaticRepoSource`. The importer: `StaticRepoImporter`.

## Where the import runs — a dedicated hub, never the mesh router

> **The import runs on its own reachable hub (`import/{meshHubId}`), NEVER the root mesh hub.** The bulk upsert traffic — every `CreateOrUpdateNodeRequest`, plus the inner `CreateNodeRequest` each one self-dispatches — must not be processed on the **root mesh hub's** single action block. That hub is the **irreplaceable router**; the moment it is busy creating import nodes it stops routing, every node op times out, and the whole portal wedges (the **atioz 2026-06-11 outage**: 11× `CreateOrUpdateNodeRequest` + 3× `CreateNodeRequest@mesh/<self>` stale >60s while real user `SubscribeRequest`s starved).

`StaticRepoImporter.ImportAll` therefore creates **one dedicated import hub** and runs the whole import on it — the same reachable-hosted-hub pattern as the [`MeshNodeStreamCache`](/Doc/Architecture/MeshNodeStreamCache) cache hub:

```csharp
meshHub.GetHostedHub(
    new Address(ImportAddressType /* "import" */, meshHub.Address.Id),   // process-unique
    config => config
        .AddData()                       // IWorkspace, so the upsert-of-existing path can dispatch
        .WithNodeOperationHandlers()     // Create/CreateOrUpdate handled on THIS action block
        .WithInitialization(h => h.RegisterForDisposal(routingService.RegisterStream(h))),
    HostedHubCreation.Always);
```

- The `import` address-type is declared **stream-routed** — registered **modularly** by the owning module via `MeshBuilder.AddStreamRoutedAddressType(StaticRepoImporter.ImportAddressType)` in **`AddGraph`** (NOT hard-coded into the core `MeshConfiguration.DefaultStreamRoutedAddressTypes`, which keeps only the framework-core `portal`/`client`/`cache`). The silo's `RoutingGrain` dispatches to it over the cluster memory stream, and `RegisterStream` makes responses (query results, `ImportContent` acks) route back.
- Because the hub carries `WithNodeOperationHandlers`, the bulk upserts are **handled locally on the import hub** (the inner self-posted `CreateNodeRequest` stays on it too). The mesh router only sees the occasional lock-`CreateNode` / read query — never the create storm.
- Even a **total import failure is isolated**: the boot subscription (`StaticRepoImportHostedService`) is fire-and-forget with an `onError` terminal, so a wedged or failing import can never take down the router. The portal serves regardless.

## What the importer does, per source, per boot

1. **Resolve the Space root** — `source.PartitionRoot` or a synthesized generic `Space` — and fold it into the source node set.
2. **Provision the partition schema** (`IPartitionStorageProvider.EnsurePartitionProvisioned`, lowercased, idempotent/promise-cached) **before** anything is written — the activity-lock node in step 4 lives at `{P}/_Activity/…`, *inside* the partition schema, so a fresh partition would otherwise fault (42P01 — there is no lazy schema create; see [GhostSchemaInvariantTests]).
3. **Fingerprint + short-circuit** — `PartitionSourceFingerprint.Compute(nodes + root)`. If a `Succeeded` activity at `{P}/_Activity/import-{fingerprint}` already exists, **stop** (the common case on every boot).
4. **Lock** — `CreateNode({P}/_Activity/import-{fingerprint})`. The node lifecycle makes the **first caller win**; concurrent replicas get "already exists" and stop. The activity *is* the lock and the durable "version vN imported at T" record.
5. **Ensure the Space root** (standard step) via the canonical upsert — creating a `Space` triggers eager schema provisioning + the `Admin/Partition/{P}` routing prime + the admin grant; an existing root is updated. This makes the partition routable, listed in `public.top_level_index`, and gives it a landing page.
6. **Upsert every source node** through **`CreateOrUpdateNodeRequest`** — the single canonical verb (the same one `NodeCopyHelper` uses). It **creates** absent nodes and **updates** existing ones (the owner **re-stamps Version**), running the full pipeline: prerender (`MarkdownContent.Parse`), embedding, satellites, access. User-claimed subtrees (`SyncBehavior != Include`) are skipped. **Each upsert is independently guarded** (per-file `try/catch`): a single node faulting (bad content, a validator reject, a transient owner timeout) logs a `⚠ Failed to import {path}` line **into the import activity** and the import **continues** — the first failure never aborts the rest of the partition. Failures are tallied.
7. **Prune (full-replace)** — delete target nodes absent from the source (except governance `_Policy`/`_Access`/`_Activity`), then write the **terminal status atomically** via `NodeTypeCompilationActivity.Complete`: **`Succeeded`** when every node imported, **`Warning`** (`"N FAILED (see ⚠ above)"`) when any per-file upsert failed — so the activity log never shows a green Succeeded while hiding failures, and the `⚠` lines pinpoint exactly which files to investigate. A hard fault in provisioning/root/read still `MarkFailed`s the whole run.

All writes run under `AccessService.ImpersonateAsSystem` (re-established at each write's own subscribe, since the System identity must reach the cross-hub write — see [AccessContextPropagation.md](/Doc/Architecture/AccessContextPropagation)).

### Why `CreateOrUpdateNodeRequest`, not CreateNode + Overwrite

The importer must be **idempotent over existing rows** (re-imports, eventually-consistent snapshots, and especially the migration backfill's content-NULL shadow rows). Plain `CreateNode` faults on an existing node; a hand-rolled stream-`Overwrite` re-asserts the *same* Version, which the owner drops as not-newer — so content silently never lands. The canonical `CreateOrUpdateNodeRequest` does the right thing for both cases and **increments the Version on update**, so the write is accepted and persists. This is non-negotiable: do not re-implement create/update in the importer.

## Scope: mesh nodes AND content-collection files

The import materializes both **mesh nodes** (a node's `Content` + prerendered HTML, via the node upsert above) and the node's **content-collection files** — the assets a node references through the `content` collection, e.g. an `@@content/logo.svg` image embed or an `@@content/sample.md` include on a doc page.

Those files live in a **per-node content collection**, not the node row: `ConfigureDefaultNodeHub` gives every node hub its own `content` collection rooted at `{Storage:BasePath}/content/{nodePath}`, read via `IFileContentProvider.GetFileContent("content", "<file>")` **on that node's hub**. Syncing a file is therefore a per-node-hub operation dispatched to the owning node's hub, distinct from the node upsert.

### How content files are synced (collection → collection)

1. A source ships its assets in an **embedded content collection** and declares the imports by overriding `IStaticRepoSource.EnumerateContentImports()` → `StaticContentImport(NodePath, SourceCollection, SourcePath, TargetCollection="content", TargetPath="")`. E.g. `DocumentationStaticRepoSource` ships `logo.svg` + `sample.md` under `Content/DataMesh/UnifiedPath/` (the embedded `DocContent` collection) and imports that folder into the `Doc/DataMesh/UnifiedPath` node's `content` collection.
2. After the node upsert the importer's `SyncContentImports` posts the **canonical `ImportContentRequest`** per entry, under `ImpersonateAsSystem`, via the fluent API in `MeshWeaver.ContentCollections`:

   ```csharp
   hub.ImportContent(nodePath)
      .From("DocContent", "DataMesh/UnifiedPath")  // embedded source collection + folder
      .To("content")                               // target collection on the node
      .Post()                                       // → ImportContentRequest to the OWNING node's hub
   ```

   The handler (registered by `AddContentCollectionsInfrastructure`, so every content-enabled node hub has it) resolves both collections via `IContentService` and copies each direct-child file of the source folder **stream-to-stream** so binary assets (svg/png) survive — `GetFiles`/`GetContentAsync` on the source, `SaveFileAsync` on the target — with the **whole copy sealed in one `IIoPool` operation** (the hub action block only subscribes + returns; async never runs on the hub). The file is then served through `/static/{address}/content/<file>`. **No hand-rolled cross-hub `IFileContentProvider` write, no async on a hub path, and no second `ImportContentRequest`** (the type is wire-registered — a duplicate collides). The embedded source collection (`DocContent`) is exposed on node hubs too (`AddDocumentation`'s `ConfigureDefaultNodeHub`) so the node-hub handler can read it.

This is why a synced doc page's `@@content/<file>` embeds render on a fresh deployment (atioz: FileSystem `/mnt/content`): the importer copies the assets into the runtime `content` collection on boot, alongside the nodes. Tests: `ContentImportSyncTest` (monolith, filesystem + embedded sources) and `OrleansContentImportSyncTest` (the distributed cross-grain path — the shape that must not deadlock).

## The two primitives

### 1. Source fingerprint — the content-version

A deterministic, order-independent hash over the source node set (children **+ the Space root**):

```
for each source node:  line = path + "\0" + (Versioned ? version : sha256(content))
sort lines by path                     // order MUST NOT affect the hash
fingerprint = sha256( join(lines, "\n") )[..16]
```

Changes iff a node is added, removed, or modified — including an edited welcome (the root is in the set). Helper: `PartitionSourceFingerprint.Compute`.

### 2. Content-addressed Activity — the lock + the short-circuit

The import runs as an [Activity](/Doc/Architecture/ActivityControlPlane) whose **id is the fingerprint**: `{Partition}/_Activity/import-{fingerprint}`.

- **Same source ⇒ same id.** Concurrent replicas both `CreateNode` that exact path → first writer wins; the rest get "already exists" and observe. No advisory lock, no leader election.
- **A `Succeeded` activity for the fingerprint is the durable "already imported" record** — the boot short-circuit reads it; equal fingerprint ⇒ no work.
- **Changed source ⇒ new id** ⇒ a fresh import runs. Old `import-{prevHash}` activities remain as a visible import history.

## Why startup, not the SQL migration

The SQL migration (`Memex.Database.Migration`) is a standalone process with **no live mesh** — it cannot post `CreateOrUpdateNodeRequest` or compute prerender through the pipeline. The portal boot **has** the live mesh, so the canonical pipeline is available there. The migration owns **schema** (DDL); static-repo **content** is owned by this startup import. The content-addressed activity is exactly what makes "runs in the portal" safe across replicas (not "runs N times").

(`DocumentationBackfill` — the old raw `INSERT … content = NULL` search-index write in the migration — is now redundant: the imported rows are content-bearing. It is harmless but its content-NULL rows, if present, are refilled by the import's upsert.)

## Distributed serving (why this matters)

In the **distributed (Orleans/PG) portal, routing does not consult the in-memory `EmbeddedResourceStorageAdapter`** — so a partition that is only served from the embedded overlay 404s / hangs. The static-repo import is what makes built-in partitions (Doc/Agent/Model) **served from the DB** there. The monolith (in-process embedded routing) works either way, so the cutover is gated by `Features:StaticRepoSync:Partitions` (default `["Doc","Agent","Model"]` for the distributed portal; monolith leaves it empty and keeps in-memory serving).

## Import / export symmetry

Export is the inverse and shares the lock: an export activity at `{Partition}/_Activity/export-{…}` reads the partition subtree and writes the static-repo shape; before either direction starts it checks for an in-flight `import-*` / `export-*`. One activity surface gates both.

## Reuse map (do not re-invent)

| Need | Existing piece |
|---|---|
| Upsert one node (create-or-update) | `CreateOrUpdateNodeRequest` (the single canonical verb) |
| Copy/import an existing **mesh** subtree (node + children + satellites) as an activity | `NodeCopyDispatchRequest` + `NodeCopyHelper.CopyNodeTree` (`Force` = overwrite/full-replace) |
| GUI import / copy / export | `ImportLayoutArea` (Import menu: namespace + Mesh Node/File/Folder), `CopyLayoutArea`, `MarkdownExport` |
| Activity node + state machine | `ActivityLog` / `ActivityStatus` / `hub.WatchControlPlane` ([ActivityControlPlane.md](/Doc/Architecture/ActivityControlPlane)) |
| Start/log/finish an activity | `NodeTypeCompilationActivity.Start/AppendLog/MarkSucceeded/MarkFailed`; `Complete(status, messages)` for an **atomic** terminal-status-plus-log write |
| A dedicated reachable hub for off-router bulk work | `GetHostedHub(new Address(type, meshHub.Address.Id), …RegisterStream…)` + `AddStreamRoutedAddressType(type)` — the cache-hub / import-hub pattern ([MeshNodeStreamCache.md](/Doc/Architecture/MeshNodeStreamCache)) |
| Enumerate embedded doc nodes (with content) | `DocumentationNodeProvider.LoadIndexableNodes(jsonOptions)` — **pass the hub's `JsonSerializerOptions`** (camelCase + polymorphic `$type`), else `.json` nodes deserialize bare |
| Prerender markdown | `MarkdownContent.Parse(content, path, path).PrerenderedHtml` |

`ImportNodesRequest` (in `ImportDeleteRequests.cs`) is **dead/unimplemented** — do not use it; this pattern supersedes it.

## Status

Shipped and enabled for `Doc` / `Agent` / `Model` on the distributed portal. The migration backfill is superseded (content-NULL rows are refilled by the import).

## Invariants (tested — `StaticRepoImporterTests`, `PartitionSourceFingerprintTests`)

- Fingerprint is **order-independent** and changes on add/remove/modify.
- Import materializes children **with non-NULL content + `PreRenderedHtml`** (round-tripped from PG) and a `namespace=""` `Space` root.
- Only the **lowercased** partition schema is provisioned — never a verbatim/capital ghost.
- A **changed** source re-imports and **increments the Version** of updated nodes (the canonical-upsert guarantee).
- An import over a **content-NULL row refills its content** (the migration-backfill shadow case).
- A node **absent from the source is pruned** (full-replace).
- Re-run with an unchanged source is a **no-op** (fingerprint short-circuit).
- The import runs on the **dedicated `import/{meshHubId}` hub**, not the root mesh hub — the bulk create/upsert traffic never touches the router (verified end-to-end by `OrleansStaticRepoImportTest` / `OrleansContentImportSyncTest`, which complete only because the import hub is reachable).
- A **single node failing does not abort the import**; it logs a `⚠` line and the activity ends **`Warning`**, not a green `Succeeded`.
