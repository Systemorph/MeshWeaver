---
Name: Static-Repo Import (content-addressed, as an Activity)
Category: Architecture
Description: How a static repository (embedded docs, sample graphs, seed data) is materialized into a mesh partition — full content + prerender — exactly once per deploy, tracked as an Activity, idempotent via a source fingerprint stamped on the partition main node.
Icon: DatabaseArrowDown
---

# Static-Repo Import

A **static repo** is authored content that ships *with the build* — embedded documentation (`MeshWeaver.Documentation`), sample graphs (`samples/Graph/Data`), node-type templates. It is the **source**, never the live serving copy. This doc defines the one canonical way to get a static repo into a partition so the partition is **served from the database** like any other.

## The rule

> **A static repo is materialized into its partition by the canonical node-create pipeline (content + prerender), exactly once per (deploy, content-version), tracked as a content-addressed `Activity`, and recorded on the partition main node. No bespoke SQL, no per-instance races, no content-NULL shells.**

## What this replaces (and why)

Before this pattern, docs were populated by `DocumentationBackfill` — a raw `INSERT INTO doc.mesh_nodes (… , content = NULL, …)` run inside the SQL migration. That had three defects:

1. **Not served from the DB.** Rows were a *search index* (`content = NULL`); page content + `PreRenderedHtml` were expected to come from the in-memory `EmbeddedResourceStorageAdapter` at read time. When that read path isn't the serving source (distributed portal), doc pages render empty / 404 even though the row exists.
2. **Forked logic.** Raw SQL bypassed the canonical `CreateNode` pipeline (prerender, embedding, satellite + access handling). Re-implementing all of it in SQL is a maintenance trap.
3. **No clear lock / version.** "Did this content already import?" was a per-row hash table (`documentation_index`), not a partition-level fact, and nothing coordinated multiple portal replicas.

The pattern below fixes all three: the embedded resources become a **source repo only**; serving comes from the **DB partition**, materialized through the **real pipeline**, **once**.

## The three primitives

### 1. Source fingerprint — the content-version

A deterministic, order-independent hash over the source node set:

```
for each source node:  line = path + "\0" + (Versioned ? version : sha256(content))
sort lines by path                     // order MUST NOT affect the hash
fingerprint = sha256( join(lines, "\n") )[..16]
```

- Changes iff a node is **added, removed, or modified** (the line set changes).
- Versioned partitions use `(path, version)` (cheap, no content read); unversioned static repos (embedded docs, `Versioned=false`) fall back to `(path, contentHash)`.
- Helper: [`PartitionSourceFingerprint.Compute(IEnumerable<MeshNode>)`](../../../MeshWeaver.Mesh.Contract/PartitionSourceFingerprint.cs).

### 2. Content-addressed Activity — the lock + the state

The import runs as an [Activity](ActivityControlPlane.md) whose **id is the fingerprint**:

```
{Partition}/_Activity/import-{fingerprint}
```

This single naming choice gives the whole lock for free:

- **Same source ⇒ same name.** Two replicas both `CreateNodeRequest` that exact path → the mesh hub's node lifecycle makes the **first writer win**; the rest get "already exists" and observe instead of duplicate. No advisory lock, no leader election.
- **Owning hub serializes.** The `_Activity` node is owned by one hub whose single-threaded action block runs the work once; `WatchControlPlane` reacts to `RequestedStatus = Running`. (`ActivityLog.Status`/`RequestedStatus`, `ActivityStatus` enum — see [ActivityControlPlane.md](ActivityControlPlane.md).)
- **Changed source ⇒ new name** ⇒ a fresh import activity runs.
- **Observable + cancellable** like every other activity (`hub.RequestActivityStatus` / `hub.CancelActivity`).

Old `import-{prevHash}` nodes are kept as an **import history** (cheap; a visible "doc set vN imported at T" trail). Pruning is optional.

### 3. Partition main node — the durable "what's installed"

The partition root (`namespace="", id={Partition}`) carries the last successfully-imported fingerprint:

```jsonc
// MeshNode { Namespace="", Id="Doc" }.Content
{ "importedSourceHash": "<fingerprint>", "importedAt": "<utc>", "nodeCount": 149 }
```

This is the **short-circuit**: on every boot, compute the source fingerprint and compare to the main node. Equal ⇒ **done, no activity, no work** (the common case). This is also where `public.top_level_index` (#16) reads the partition root, so maintaining this node keeps the partition routable and listable.

## End-to-end flow (startup)

```
DocImport hosted service (per partition flagged ImportFromStaticRepo):
  1. nodes        = source.EnumerateSourceNodes()        // LoadIndexableNodes() for Doc
  2. fingerprint  = PartitionSourceFingerprint.Compute(nodes)
  3. root         = GetMeshNodeStream(Partition).Take(1)  // partition main node
  4. if root.ImportedSourceHash == fingerprint:  return   // ← short-circuit, no work
  5. CreateNodeRequest({Partition}/_Activity/import-{fingerprint})   // first-writer-wins lock
        Content = ActivityLog(Import) { Status = Running }
  6. owning hub (WatchControlPlane → Running) runs ImportPartition:
        for each source node:
            content    = node.Content
            prerender  = content is MarkdownContent md
                           ? MarkdownContent.Parse(md.Content, path, path).PrerenderedHtml   // SHARED static
                           : null
            CreateNode( node with { Content, PreRenderedHtml = prerender } )   // canonical pipeline
            AppendLog(activity, "imported {path}")
        prune target nodes whose path ∉ source   // full-replace semantics (Install = Copy + RemoveMissing)
        stamp main node { ImportedSourceHash = fingerprint, ImportedAt, NodeCount }
        MarkSucceeded(activity)   // or MarkFailed(activity, error)
```

Single-execution holds three ways over: the **main-node short-circuit** (step 4) skips the common case; the **content-addressed CreateNode** (step 5) makes concurrent replicas converge to one node; the **owning hub's action block** (step 6) serialises the work.

## Why startup, not the SQL migration

The SQL migration (`Memex.Database.Migration`) is a standalone process with **no live mesh** — it cannot post `CreateNodeRequest` or compute prerender through the pipeline. Doing the import there forces the bespoke-SQL fork this pattern removes. The portal boot **has** the live mesh, so the canonical pipeline is available there. The migration keeps owning **schema** (DDL); static-repo **content** is owned by this startup import. They sequence naturally — the import waits for the partition schema to exist (provisioned by [`PostgreSqlPartitionSubscriptionHostedService`](../../../MeshWeaver.Hosting.PostgreSql/PostgreSqlPartitionSubscriptionHostedService.cs)).

Distributed-replica safety is exactly what the content-addressed activity buys — so "runs in the portal, not the one-shot migration job" no longer means "runs N times."

## Import / export symmetry

Export is the inverse and shares the lock: an export activity at `{Partition}/_Activity/export-{…}` reads the partition subtree and writes the static-repo shape; before either direction starts, it checks for an in-flight `import-*` / `export-*` on the partition. One activity surface gates both.

## Reuse map (do not re-invent)

| Need | Existing piece |
|---|---|
| Copy a node subtree as an activity | [`NodeCopyDispatchRequest`](../../../MeshWeaver.Graph/NodeCopyDispatchRequest.cs) + `NodeCopyHelper.CopyNodeTree` + `Templates/NodeCopy.csx` |
| Activity node + state machine | `ActivityLog` / `ActivityStatus` / `hub.WatchControlPlane` ([ActivityControlPlane.md](ActivityControlPlane.md)) |
| Start/log/finish an activity | `NodeTypeCompilationActivity.Start/AppendLog/MarkSucceeded/MarkFailed` |
| Enumerate embedded doc nodes (with content) | `DocumentationNodeProvider.LoadIndexableNodes()` |
| Prerender markdown | `MarkdownContent.Parse(content, path, path).PrerenderedHtml` |
| Write the main node | `workspace.GetMeshNodeStream(partition).Update(...)` |
| Per-partition startup hook | `IHostedService` (see `PostgreSqlPartitionSubscriptionHostedService`) |

`NodeCopyHelper.CopyNodeTree` already preserves `PreRenderedHtml` but does **not compute** it — the import wraps it (or its own per-node path) with the `MarkdownContent.Parse` prerender step. `ImportNodesRequest` (in `ImportDeleteRequests.cs`) is **dead/unimplemented** — do not use it; this pattern supersedes it.

## Implementation status / phases

- **Phase 1 — fingerprint.** `PartitionSourceFingerprint.Compute` + tests. *(done)*
- **Phase 2 — import-as-activity.** A partition-import activity handler that reads source nodes, computes prerender, `CreateNode`s through the pipeline, prunes missing, stamps the main node, reports status. Content-addressed activity id = fingerprint.
- **Phase 3 — startup orchestration.** A hosted service that, per partition declaring `ImportFromStaticRepo`, runs the short-circuit + triggers the activity. Sequenced after schema provisioning.
- **Phase 4 — cut over docs.** `AddDocumentation` flags the `Doc` partition `ImportFromStaticRepo`; `DocumentationBackfill` stops writing `content = NULL` (either removed, or reduced to only the embedding/search columns on the now content-bearing rows). The embedded adapter becomes the **import source**, the DB the **serving source**.
- **Phase 5 — export + general repos.** Export activity; generalise to `samples/Graph/Data` seed import.

## Invariants (tests)

- Fingerprint is **order-independent** and changes on add/remove/modify.
- Re-run with an unchanged source is a **no-op** (main-node short-circuit; zero `CreateNode`s).
- Concurrent triggers for the same fingerprint produce **one** activity and **one** import (content-addressed create).
- After import, doc rows have **non-NULL content and PreRenderedHtml**, and navigation resolves them from the DB (no embedded-adapter dependency).
- A genuinely-absent page resolves to a fast, clean `NavigationPhase.NotFound` (no `DeliveryFailureException`), because absence is now real absence — not a content-NULL shell.
