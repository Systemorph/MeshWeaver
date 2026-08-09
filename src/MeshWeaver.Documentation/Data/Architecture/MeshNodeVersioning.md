---
Name: MeshNode Versioning
Category: Documentation
Description: How MeshNode.Version counts REAL changes to a node — the node-local revision counter and the no-op gates that protect it
---

# MeshNode Versioning

Every `MeshNode` carries a `long Version`. It is the node's own **revision counter**: it increases by exactly one each time the node is really changed, and by nothing at all when a write turns out to change nothing.

Two rules define it, and everything else on this page follows from them:

1. **`Version = current.Version + 1`** — derived from the node, never from the hub that owns it.
2. **Only a real change mints.** Every write path gates on a content diff *before* it touches `Version`.

```csharp
public static long NextVersion(long currentVersion) => currentVersion + 1;
```

<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 760 300" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L8,3.5 L0,7 Z" fill="#90a4ae"/>
    </marker>
    <marker id="arr-green" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L8,3.5 L0,7 Z" fill="#43a047"/>
    </marker>
    <marker id="arr-grey" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L8,3.5 L0,7 Z" fill="#78909c"/>
    </marker>
  </defs>
  <text x="380" y="26" text-anchor="middle" font-family="sans-serif" font-size="14" font-weight="bold" fill="currentColor" fill-opacity=".85">One Real Change, One Version — No-Ops Cost Nothing</text>

  <text x="36" y="72" font-family="sans-serif" font-size="11" fill="currentColor" fill-opacity=".5">write</text>
  <rect x="96" y="52" width="108" height="34" rx="8" fill="#1b5e20" stroke="#43a047" stroke-width="1.5"/>
  <text x="150" y="74" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#a5d6a7">edit title</text>
  <rect x="236" y="52" width="108" height="34" rx="8" fill="#263238" stroke="#546e7a" stroke-width="1.5" stroke-dasharray="4,3"/>
  <text x="290" y="74" text-anchor="middle" font-family="sans-serif" font-size="11" fill="currentColor" fill-opacity=".55">re-save (same)</text>
  <rect x="376" y="52" width="108" height="34" rx="8" fill="#1b5e20" stroke="#43a047" stroke-width="1.5"/>
  <text x="430" y="74" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#a5d6a7">edit body</text>
  <rect x="516" y="52" width="108" height="34" rx="8" fill="#263238" stroke="#546e7a" stroke-width="1.5" stroke-dasharray="4,3"/>
  <text x="570" y="74" text-anchor="middle" font-family="sans-serif" font-size="11" fill="currentColor" fill-opacity=".55">import re-assert</text>
  <rect x="656" y="52" width="76" height="34" rx="8" fill="#1b5e20" stroke="#43a047" stroke-width="1.5"/>
  <text x="694" y="74" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#a5d6a7">rename</text>

  <line x1="150" y1="88" x2="150" y2="150" stroke="#43a047" stroke-width="1.5" stroke-dasharray="4,3" marker-end="url(#arr-green)"/>
  <line x1="290" y1="88" x2="290" y2="150" stroke="#78909c" stroke-width="1.5" stroke-dasharray="2,4" marker-end="url(#arr-grey)"/>
  <line x1="430" y1="88" x2="430" y2="150" stroke="#43a047" stroke-width="1.5" stroke-dasharray="4,3" marker-end="url(#arr-green)"/>
  <line x1="570" y1="88" x2="570" y2="150" stroke="#78909c" stroke-width="1.5" stroke-dasharray="2,4" marker-end="url(#arr-grey)"/>
  <line x1="694" y1="88" x2="694" y2="150" stroke="#43a047" stroke-width="1.5" stroke-dasharray="4,3" marker-end="url(#arr-green)"/>
  <text x="290" y="120" text-anchor="middle" font-family="sans-serif" font-size="9" fill="#78909c">no-op gate</text>
  <text x="570" y="120" text-anchor="middle" font-family="sans-serif" font-size="9" fill="#78909c">no-op gate</text>

  <rect x="20" y="156" width="720" height="44" rx="8" fill="#263238" fill-opacity=".5" stroke="currentColor" stroke-opacity=".2" stroke-width="1"/>
  <text x="36" y="173" font-family="sans-serif" font-size="11" fill="currentColor" fill-opacity=".5">node.Version</text>
  <rect x="96" y="162" width="108" height="32" rx="8" fill="#37474f"/>
  <text x="150" y="183" text-anchor="middle" font-family="sans-serif" font-size="14" font-weight="bold" fill="#90a4ae">2</text>
  <rect x="236" y="162" width="108" height="32" rx="8" fill="#2c383e"/>
  <text x="290" y="183" text-anchor="middle" font-family="sans-serif" font-size="14" fill="#607d8b">2</text>
  <rect x="376" y="162" width="108" height="32" rx="8" fill="#37474f"/>
  <text x="430" y="183" text-anchor="middle" font-family="sans-serif" font-size="14" font-weight="bold" fill="#90a4ae">3</text>
  <rect x="516" y="162" width="108" height="32" rx="8" fill="#2c383e"/>
  <text x="570" y="183" text-anchor="middle" font-family="sans-serif" font-size="14" fill="#607d8b">3</text>
  <rect x="656" y="162" width="76" height="32" rx="8" fill="#37474f"/>
  <text x="694" y="183" text-anchor="middle" font-family="sans-serif" font-size="14" font-weight="bold" fill="#90a4ae">4</text>
  <line x1="204" y1="178" x2="234" y2="178" stroke="#90a4ae" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="344" y1="178" x2="374" y2="178" stroke="#90a4ae" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="484" y1="178" x2="514" y2="178" stroke="#90a4ae" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="624" y1="178" x2="654" y2="178" stroke="#90a4ae" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arr)"/>

  <text x="36" y="238" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".45">Version counts REVISIONS OF THIS NODE — contiguous, monotonic, and completely independent of hub traffic,</text>
  <text x="36" y="254" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".45">of other nodes, and of how many times the node happened to be re-saved. A recycle cannot move it.</text>
  <text x="36" y="278" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".35">New node = 1 · never-mutated seeded node = 0 · v(n) → v(n+1) exactly once per real edit.</text>
</svg>

*A write that changes nothing is completed, acked, and dropped — it never reaches the counter.*

## No `Version` Without a Change

The gate comes first at **every** place a write can land; the bump is what happens *after* it passes. This ordering is load-bearing: the bump would itself manufacture the difference that makes a no-op write look like a change.

| Write path | Where the gate lives |
|---|---|
| Own-node write (`MeshNodeStreamHandle.UpdateOwn`) | `ReferenceEquals` → record `Equals` → `MeshNode.SerializedEquals`; a no-op completes the observable with the unchanged node and applies nothing |
| Cross-hub write (`MeshNodeStreamHandle.UpdateRemote`) | the RFC 7396 merge-patch diff is computed on the lambda's raw output; an empty diff returns without posting |
| Owner applying a cross-hub patch (`DataExtensions.ApplyMeshNodePatchInTurn` and the deferred path) | `JsonNode.DeepEquals(preMerge, postMerge)` — acks success and commits nothing |
| `IMeshService.UpdateNode` (`NodeUpdatePipeline`) | normalises `Version` + `LastModified` to the live values, then `SerializedEquals` |
| The persistence re-stamp (`MeshNodeTypeSource.UpdateImpl`) | record `Equals` ignoring `Version`, confirmed by `SerializedEquals` |

**Why `SerializedEquals` and not plain record `Equals`.** `MeshNode.Content` is an `object?`. A rebuilt-but-identical typed content, a re-parsed `JsonElement` (a struct whose default equality compares the parse buffer), or a content record holding collections (compared by reference) all read as "changed" under record equality while the persisted JSON is byte-identical. Every such write used to mint a version and persist a history row — the "v1170 with no edits" report. `MeshNode.SerializedEquals(a, b, options)` compares the serialized JSON and runs only after the cheap checks already disagreed, so an unchanged node costs nothing.

**`LastModified` is stamped only on a real change.** The audit stamp is applied *after* the diff, never before — otherwise the stamp is the only thing in the patch and every save looks like an edit.

## 🚨 Never Author `Version` in a Source File — Use SemVer

`Version` is the OWNER's persistence clock. It is `[Editable(false)]`, it means "revision N of this
node", and it belongs to the hub that owns the node. **A node file in a source repo must never carry
it.**

A committed `Version` is a snapshot of some *other* mesh's clock. On a fresh mesh it collides with
the durable row and the store correctly refuses the write:

```
[MonotonicWriteGuard] REFUSED a backward write to Store: incoming Version=1 is BELOW the
    stored Version=249 … forked lineage: stale activation seed or a second writer
MeshWeaver.PluginCatalog.Catalog: Install of Store failed.
```

**What that costs is out of all proportion to the symptom, and the symptom names the wrong thing.**
The refused write fails the INSTALL; the partition then has no readable root, so every subscribe is
denied (799 of them in one observed run); its NodeTypes never compile because nothing readable
reaches them; and the harness finally reports *"Store/Plugin never reached compilationStatus Ok"* —
a compile error, five minutes and four causal steps from the actual failure. It reads as a flaky
compile for as long as you let it.

Found 2026-08-06 across **every** node repo — 13 modules carrying counters as high as 4489, each a
fork waiting for a fresh mesh.

**The rule:** a module's authored version is **SemVer** — `manifest.lock`'s `version` (and
`content.version` for the series), published as the git tag `<Module>/vMAJOR.MINOR.PATCH`. The
persistence counter is never authored, only minted. Two different things that happened to share a
field name, which is exactly how this went unnoticed.

🚨 **Do not "fix" a fork by relaxing the guard.** Its own message says it: *"Find the writer that
adopted a stale own-node snapshot; do not relax this guard."* A regressing write is a stale snapshot
about to destroy acknowledged data. When a fork appears, find the writer — and check first whether
the writer is a checked-in file.

## Only the Owner Mints

A client/subscriber writing a node it does not own (a cross-hub `GetMeshNodeStream(path).Update(...)`) **carries the base version it last observed** and lets the owner assign the fresh value on apply. It never increments client-side. A pre-incremented client version (the old `Math.Max(existing, …) + 1`) ships a frame whose base is already out of date by the time it lands, and the owner's version-guarded merge mishandles it. See [DataSyncAndCrdt](/Doc/Architecture/DataSyncAndCrdt) §2 ("a subscriber never mints a version").

**Write through the live lambda parameter.** `stream.Update(node => node with { … })` must transform the node it is handed — the live, owner-reconciled value — never discard it and slam a separately-read full node (`_ => fetchedNode`). The owner computes the diff it applies against that live value; a discarded parameter bases the diff on a stale snapshot and can clobber a concurrent edit.

**Each write bumps once.** The write path that commits a change is the one that bumps. `MeshNodeTypeSource.UpdateImpl` re-stamps only a node whose incoming version did **not** already advance past what it previously carried (a sync-stream value update, a client-carried base version) — re-stamping a node the write path already bumped would count one edit twice.

## Not the Hub Clock (#325)

`MessageHub.Version` increments once per message dispatch — it counts **operations the hub processed**. `MeshNode.Version` used to be stamped from it (`max(hubVersion, current + 1)`), which had two consequences that are now gone:

- **Unrelated traffic moved the number.** A node touched under message 3 and again under message 47 jumped `3 → 47`. The version described the hub's workload, not the node's history.
- **A recycle rolled it backward.** The hub clock resets to **0** on every activation, so a deactivate → reactivate cycle (idle-release, `Recycle`/`DisposeRequest`, a replica restart) stamped the node's next write with the fresh *low* clock. A caller re-reading the node saw an OLDER version than the writes it had just confirmed — the write-rollback / "v113 read back as v3" of [issue #325](https://github.com/Systemorph/MeshWeaver/issues/325).

A node-local counter is monotonic across activations **by construction**: the node loads its persisted `Version` verbatim on activation (`MeshNodeTypeSource.BuildInstanceCollection` leaves it alone — a load is a read), and the next write is that value `+ 1`.

> **The hub clock is untouched, and that matters.** `Hub.Version` also stamps the owning hub's **layout-area render Fulls**, and the sync-stream *frame* version rides it. Re-seeding the shared clock from a node version was tried (`SetInitialVersion(node.Version)`) and reverted: layout Fulls advance per *render* and run far ahead of a doc/static node's low `Version`, so seeding it backward made the monotonicity guard drop every later Full — the prod 2026-06-18 "cannot find pinned doc" wedge. Likewise, flooring the *frame* version at a content baseline broke the normal single-hub data-load frame sequence (the `PageLoadingTest` / `SourceDocumentDataLoadingTest` hangs) and the activity/export relay. The two clocks stay separate: node revisions on the node, render/frame ordering on the hub.
>
> The residual cross-**silo** mirror-drop (#325 symptom 2, "index vs node-resolution split-brain", multi-replica only — the monolith heals it via the heartbeat resubscribe) is fixed **on the mirror side, scoped precisely to the resubscribe path** so no normal frame is touched. After an owner grain idle-recycles, a mirror on another silo that cached the higher pre-recycle frame version would drop the recycled owner's low post-recycle resubscribe Full under the guard (`version < Current.Version`) and stay orphaned. But that mirror **already detected it is behind** — `JsonSynchronizationStream.CreateExternalClient`'s version-gated resubscribe fires only when the change feed announced a *higher node version than the mirror holds* — and it is asking for a fresh authoritative snapshot. So the resubscribe **arms a one-shot latch** (`SynchronizationStream.ExpectResubscribeFull`); the next `Full` that reaches `UpdateStream` consumes the latch and is accepted even though its frame version regressed, then the mirror adopts that re-based clock. Only a `Full` consumes the latch (a stray reordered patch is still dropped), and it is set **only when the mirror is genuinely behind** (`receivedVersion < announcedVersion`) — so it can never clobber a newer optimistic write with a stale snapshot. Proven by `TwoSiloRecycleConvergenceTest` and guarded by `PageLoadingTest` / `SourceDocumentDataLoadingTest` / `ExportDocumentScriptRelayTest` / `DataChangeStreamUpdateTest` / `InlineEditingTest`.

## The Activation Seed Is Durable Storage — and the Store Refuses a Backward Write

`current.Version + 1` only holds if the value it counts from is the node's **real** persisted version. Two invariants make that true, and both exist because they were once violated — with durable data loss.

**1. A (re)activating hub seeds its own node from `IStorageAdapter`, not from a cache.** The routing layer attaches an own-node observable at hub instantiation (`WithOwnNodeStream`, set by `MessageHubGrain` / `MonolithRoutingService`). That stream is the right source for **live updates** — and on Orleans it is the only source of the *enriched* node, whose `HubConfiguration` delegate storage cannot hold — but it is **not durable state**: `PathResolutionService` memoizes the resolved `AddressResolution` *including its `MeshNode` snapshot*, invalidated only by the per-silo change feed, and `MeshNodeStreamCache` replays its last seen value. A hub that adopted such a snapshot as its live own-node state came up on an arbitrarily old node — which the persistence sampler then wrote back **over newer durable data**. So `MeshNodeTypeSource.Initialize` **merges** a one-shot durable read with the routing stream. Merge, not replace: a slow or faulted storage read never delays activation (the routing stream still seeds, and a node that has never been persisted reads back `null` and is simply dropped), while a stale routing emission loses to the durable one on version.

**2. A hub never adopts an own-node emission whose `Version` regresses.** `MeshNodeTypeSource` keeps a per-hub floor raised by every state it adopts — the durable seed, a routing emission, **and every local write committed through `UpdateImpl`** (the last is load-bearing: the durable read is asynchronous on a real backend and can land *after* a local write already advanced the in-RAM node). Only a **strictly** lower version is dropped; equal passes, because a never-mutated node sits at its seed version forever. The one legitimate rewind — a same-path recreate restarting at `Version = 1` — is recognised through the existing delete tombstone (`RecentlyDeletedRegistry`) and resets the floor.

**3. The store itself refuses a backward write.** `MonotonicWriteGuardStorageAdapter` is the outermost `IStorageAdapter` decorator (composed alongside the version writer, so every consumer that resolves `IStorageAdapter` from DI gets it). Since the counter only ever moves forward, a write whose `Version` is below the stored one is **never** a newer state — it is a stale snapshot about to destroy acknowledged data, and it is refused with an `Error` log naming both versions; the write emits the stored (winning) node rather than throwing, so a data-integrity save cannot fault a create or dispose-flush chain. A per-path in-process high-water mark (fed by writes *and* reads, so it costs no extra I/O) is only a cheap **filter**: a suspected regression is verified against a real read of the current row before anything is refused, so a stale mark — another replica deleted and recreated the node, the store was restored out of band — can never refuse a legitimate write. Re-persisting an *unchanged* node at its existing version is accepted: the guard refuses only a **strictly** lower version. There is deliberately **no bypass hatch**: every framework rewind already writes *forward* (version restore re-stamps `Version = 0` so the owner mints a new top version; imports and GitSync go through the owner's `stream.Update`; a delete drops the row, so a recreate faces no stored row at all).

Pinned by `StaleActivationSeedRollbackTest` (deterministic: recycle the owner, advance the durable row out of band, assert the reactivated hub serves durable state and never rolls the store back) and `MonotonicWriteGuardTests`.

## Never-Mutated Nodes Keep Their Seed Version

A node loaded from persistence — or seeded via `AddMeshNodes` / `IStaticNodeProvider` — and **never** written through `Update` keeps whatever `Version` it was created with, typically `0`. The `HandleSaveMeshNode` path persists the node's `Version` verbatim; it does **not** synthesise a bump on save. So a static config node legitimately reads back as `Version == 0`.

The same holds for a hub *reactivating*: its already-durable node is re-added to a fresh in-memory collection, but re-persisting it is not a change, so it keeps its version.

## Created Nodes Start at Version 1

`HandleCreateNodeRequest` stamps new nodes as follows:

```csharp
Version = node.Version > 0 ? node.Version : 1
```

A freshly created node gets `Version = 1` unless the caller explicitly supplied a higher value (for example, an import flow replaying historical versions). The reason is serialisation, not semantics: the hub's `JsonSerializerOptions` uses `DefaultIgnoreCondition = WhenWritingDefault`, so `Version = 0` would be **omitted** from the persisted JSON entirely. Starting at `1` guarantees the field is always present on the wire and in storage.

## Version Semantics at a Glance

| Situation | `Version` value |
|---|---|
| Seeded static / config node, never mutated | `0` (its seed value) |
| Node created via `CreateNodeRequest` | `1` (or caller-supplied, if > 0) |
| Node really changed through a write path | `previous + 1` |
| Write that changes nothing (identical upsert, re-import, re-save) | unchanged — no bump, no history row |
| Owning hub recycled and reactivated | unchanged — the durable value is loaded verbatim |
| Persisted via `HandleSaveMeshNode` | verbatim — no synthetic bump |

## What This Is Not

This is in-mesh change tracking for the live `MeshNode` graph. It is entirely unrelated to **data versioning** of the *content* held by NodeTypes — historical queries, time-travel, and `@path@V{n}` snapshots are a separate concern covered in [DataVersioning](/Doc/Architecture/DataVersioning).
