---
Name: Import Write Ordering (type before instance)
Category: Architecture
Description: Why a static-repo import writes a NodeType before the instances that name it, what happens to a type that arrives from another partition or repo, and the cycle policy — the root cause and the settled decisions behind issue #2556.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 6h13"/><path d="M3 12h9"/><path d="M3 18h5"/><path d="m17 10 4 4-4 4"/><path d="M21 14h-8"/></svg>
---

# Import Write Ordering

A [static-repo import](../StaticRepoImport) writes a source's nodes through the one canonical upsert verb. **The order it writes them in is part of the contract**, because the create pipeline refuses a node whose `NodeType` names nothing the mesh knows:

```text
System.InvalidOperationException: Upsert of
  'MeshWeaver/samples/Graph/Data/FutuRe/EuropeRe/TransactionMapping/EUR-COMM_FIRE-PROP'
  failed: NodeType 'FutuRe/TransactionMapping' is not registered
```

## The rule

> **A NodeType node is written before every instance that names it, and a type's `Source/`/`Test/` nodes are written before the type. A node whose type this pass cannot put in place first is a *blocked create* — named, reported, and NOT counted as a failure.**

## The incident (issue #2556)

The importer wrote nodes in whatever order the source enumerated them, five at a time. A repo shipping an **instance of a type it introduces** therefore had its instance refused whenever enumeration happened to put it first.

What made that permanent rather than transient is the interaction with the last-sync baseline guard. `GitHubSyncService.MayAdvanceBaseline` holds the baseline whenever a node failed to land — added by #2229 item C, *precisely* so a later pass would retry the refused instance once its type node existed. It works exactly as designed. But the retry re-ran **the same pass with the same ordering**, so the same instance was refused again, the baseline was held again, and the cycle repeated. #2229 item C converted a permanent silent miss into a permanent loud loop — strictly better, and still not landing the content.

Measured on memex-cloud:

| Measurement | Window | Value |
|---|---|---|
| `is not registered` in `namespace="memex-cloud"` | 90 min | **6,902** |
| Refusals of one single node (`EUR-COMM_FIRE-PROP`) | 120 min | **40** |

Forty attempts, one node, zero progress: **non-convergent**, not merely slow. The write order was the defect, so more retries, a longer backoff, or a watchdog could not have helped — each would only have made the loop cheaper to ignore.

## Ordering is sufficient — the refusal is not about a TypeRegistry

This is the question to settle **first**, because if registration lagged the write, a topological sort would only postpone the failure instead of removing it.

It does not. Despite the wording, the check in `MeshExtensions`' create path (step 3) is:

```csharp
// 3. NodeType existence check.
if (string.IsNullOrEmpty(node.NodeType))            typeExistsObs = Observable.Return(true);
else if (hub.ServiceProvider.FindStaticNode(node.NodeType) is not null)
                                                    typeExistsObs = Observable.Return(true);
else if (persistence != null)                       typeExistsObs = persistence.Exists(node.NodeType);
else                                                typeExistsObs = Observable.Return(false);
```

`IStaticNodeProvider` ∪ `IStorageAdapter.Exists(typePath)` — **a node at the type's path**, not a compiled assembly, not a hub type registration. And the write that puts it there is **commit-then-publish**: `WriteAndPublishCreated` emits only after the storage write commits, and `CreateNodeResponse.Ok` is posted after that. So a *completed* type write is already visible to the very next node's probe. There is no registration lag left for ordering to merely delay.

🚨 The two things this deliberately does **not** conflate:

- the **type NODE existing** — synchronous with the write, and all an instance needs;
- the **type being COMPILED** into a live assembly — asynchronous, minutes later, and something instances never wait on.

Only the first is on the import's critical path. (A payload that degrades to an untyped `JsonElement` because the *reading* hub never loaded the assembly is a different defect with the same log line — see [Node Type Compilation](../NodeTypeCompilation) and `ObjectAsExtensions`. Ordering does not address it, and a count of `is not registered` lines mixes the two.)

## The two edges

`ImportWriteOrder.Plan` builds a dependency graph over **paths only** — nothing casts `Content`, because a node read back from storage carries its content as an untyped `JsonElement`:

| Edge | Meaning |
|---|---|
| **Type before instance** | A node depends on the node whose **path equals its `NodeType`**, when this import carries one. This is the whole of #2556. |
| **Compile inputs before their type** | A type node depends on the `Source/` and `Test/` nodes under its own path — creating the type is what triggers the compile that reads them. |

The second edge is the rule `PackageInstaller.InstallNodeRepo` has applied to node-repo plugin installs since #815, and it carries that path's hard-won caveat: it is **`Source/` and `Test/` only**, never "any descendant". Widening it drags a typed instance *nested under a leaf-shaped type* (`ClaimsDeepfield/Cedent/NSV` under type `ClaimsDeepfield/Cedent`) ahead of the very type it needs — the fix becoming the bug it fixes. `ImportWriteOrderTest.ANestedInstanceIsADependentOfItsType_NotACompileInput` pins that.

The graph is then peeled by `NodeTypeDependencyGraph.TopologicalOrder` — the **same** primitive that orders NodeType compiles for the pre-warmer, so import order and compile order cannot disagree — and turned into **stages**: everything inside a stage is written concurrently at the unchanged `BatchSize` fan-out, and a stage begins only after the previous one has completed. A well-formed source is two or three stages, so this costs two barriers rather than N serial round-trips.

Within a stage the source's own enumeration order is preserved. **The ordering moves only what the dependency graph actually constrains** — a partition whose nodes have no type relationships is written exactly as it always was.

## Decision 1 — the cycle policy

`A` typed by `B` while `B` is typed by `A` is a defect **in the source**, not a state of the mesh, and no write order can satisfy it. The policy:

1. **The plan stays total.** The peel runs over the graph's strongly-connected components; the condensation of a directed graph is acyclic, so it can never stall and every input node is emitted exactly once. A node the ordering *dropped* would be a node nobody ever imports — strictly worse than one written late.
2. **A cycle is not demoted to last.** Its members are emitted at the position their component becomes ready, in path order. This is #1347's lesson, inherited: demoting cycles put the Store/paywall chain — the most user-visible types on the portal — at the very end of an 85-type sweep. Nothing about a cycle says "do this late"; it only says "these cannot be ordered relative to each other".
3. **It is deterministic.** Members are ordered by path, so *which* member gets refused is reproducible rather than a race between five concurrent writes.
4. **It is reported, and it does not fail the import.** The members are named in one warning line and in the import activity. Failing the whole import instead was rejected: the content-addressed marker and its short-circuit read a partial import's verdict, and one malformed pair would take every other node in the partition down with it.
5. **A member the type check then refuses is a blocked create**, not a per-file failure — see decision 2, which is the same rule.

A node typed by **itself** is not a cycle: a self-edge is no ordering constraint, and reporting it would name a one-member "cycle" on an ordinary self-typed root.

## Decision 2 — a type from another partition or repo

Topological order within one import cannot help when the type arrives from **another source entirely** — satellite content typed by an upstream package, or a partition whose type lives in a plugin that is not installed yet. That case is settled explicitly rather than left to the retry loop.

Before the write stages, the import probes — **once per distinct type**, and only for the types the plan does **not** already order strictly ahead of every node that needs them — using the same rule the create path applies (`FindStaticNode`, else `IStorageAdapter.Exists`). For a well-formed source that at-risk set is **empty** and nothing is probed at all.

A node whose type this pass cannot put in place, **and which does not exist in the mesh, and which the mesh has no node at yet**, is then recorded as a **blocked create**:

- **No write is attempted**, so the refusal — and its log line — is never produced. This is what removes the 6,902 lines rather than merely reordering them.
- The path **and the missing type** are named on the server log and as a `🚫` line in the import activity.
- The import outcome is `ImportedWithBlockedCreates` and the marker is stamped **`Warning`**, so the next boot re-attempts. The moment the source that defines the type is installed, the node lands — self-healing, with no watchdog.
- 🚨 It is **not** counted in `Failed`, so it does **not** hold the caller's sync baseline.

That last point is the substance of the decision. Holding the baseline means "retry this same commit", which is right for a node that might land next time and **catastrophic** for one that cannot: a single node whose type lives elsewhere froze every *later* commit of the same repo, because the diff base could never advance past it. Nothing is lost by letting it move — the git-diff scope deliberately never skips a node that is **absent** from the database, so a blocked node is re-evaluated on every pass regardless of what the diff says.

The same classification covers a cycle member, for the identical reason: its type is carried, just not ahead of it, and retrying cannot change that.

**This is not a bounded-retry or a deferral timer.** There is one attempt per import pass, it is named, and it costs no write.

## What did NOT change (and why that mattered)

`StaticRepoImporter.Run`'s ordering encodes four production incidents. The staging is inserted **inside the per-node upsert phase only**; every one of these is upstream or downstream of it and is untouched:

| Encoded decision | Incident | Where it lives |
|---|---|---|
| The **content-addressed marker** `import-{fingerprint}` — the one id that cannot be minted fresh per attempt, because the "already imported" short-circuit is derived from the content | #919 | `Import`, before `Run` |
| A **fresh attempt node per run** — a single poisoned row at the deterministic id made every retry re-target the same broken node and burn the 30 s "no initial state arrived" abort | memex Store, 2026-08-07 | `Import`, before `Run` |
| **Schema provisioning strictly before the activity-lock create** — the lock lives *inside* the partition schema, so on an unprovisioned partition the create faults `42P01` and is misreported as `AlreadyRunning` | — | `Import` → `ProvisionPartitions` |
| **Bookkeeping written as System while content keeps its original identity** — a grant-less partition could not otherwise record the progress of the sync sent to repair it | memex Store, 2026-08-07 | `Upsert` → `AsSystem` |

Also unchanged: root-first (`EnsureRoot` before any child), the claimed-node and claimed-root skips, the git-diff scope, the per-node manifest's incremental skip, two-way conflict preservation, the prune phase and its five guards, the phase-batched activity log (per-item appends are O(n²)), and the `BatchSize` concurrency bound — the barrier is *between* stages, never inside one.

## Where the code is

| Piece | File |
|---|---|
| The pure plan — edges, stages, cycle report | `src/MeshWeaver.Graph/ImportWriteOrder.cs` |
| The staged write + the blocked-create classification | `src/MeshWeaver.Graph/StaticRepoImporter.cs` (`Run`, `ProbeUnsatisfiableTypes`) |
| The shared peel (SCC condensation, #1347) | `src/MeshWeaver.Graph/Configuration/NodeTypeDependencyGraph.cs` |
| The refusal itself | `src/MeshWeaver.Mesh.Contract/MeshExtensions.cs`, create step 3 |
| The baseline guard this unblocks | `src/MeshWeaver.GitSync/GitHubSyncService.cs` (`MayAdvanceBaseline`) |
| Tests | `test/MeshWeaver.Graph.Test/ImportWriteOrderTest.cs` (pure) · `ImportTypeBeforeInstanceTest.cs` (real mesh) |

## Related

- [Static Repo Import](../StaticRepoImport) — the import itself, end to end
- [Node Type Compilation](../NodeTypeCompilation) — what happens *after* a type node lands
- [CQRS and Content Access](../CqrsAndContentAccess) — why the type probe reads storage, not the query index
- [Data Access Patterns](../DataAccessPatterns)
