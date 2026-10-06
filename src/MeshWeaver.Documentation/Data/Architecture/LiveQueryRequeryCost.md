---
Name: Live Query Re-query Cost
Category: Architecture
Description: "Why one node write cost more the larger the mesh was: every write re-walked the security fold's permanent live queries. A live raw query, and a secured one answered as System, now ignores a change whose old and new node types it can never return."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 3v18h18"/><path d="M7 15l4-4 4 4 5-5"/></svg>
---

# Live query re-query cost: one write, one walk of the mesh

## The one sentence

**A live query in `StorageAdapterMeshQueryProvider` re-reads its whole scope on every change under
that scope, and the security fold keeps three such queries open for as long as the mesh runs. So every
write, of anything, re-walked the partition and then the whole mesh. The cost of one write grew with
the size of the mesh.**

## What it looked like

The CD job `Plugins: bake + seal` failed in 8 of 9 runs after 2026-09-29 09:41Z, with two
signatures:

- `GATE FAILED — tests: Hosting/FleetConsole`. The live journey's first step, *"The operational space
  exists (the Deployments container included)"*, reported `no outcome within 10s`.
- `GATE FAILED — install: Hosting — TimeoutException`, which is `PluginGateRunner`'s 600 s
  `InstallTimeout`.

Both come from the same place. The gate installs about 75 packages into one in-memory mesh, one
after another. Here is the per-node write time of the installer, taken from the job logs (for each
package, `── X: installing N file(s)` to `Installed node-repo plugin X: N written`):

| package (install position) | run 36543299719 (green) | run 36642931001 (red) |
|---|---|---|
| Store (1st) | 0.003 s/node | 0.003 s/node |
| Edu (23rd) | 0.10 | 0.12 |
| Governance (~57th) | 0.67 | 0.48 |
| Hosting | **0.23** (32nd, 397 nodes, 91 s) | **1.11** (last, 414 nodes, 459 s) |

Plugins #2554 added `Essentials@^1.0.0` to the `requires` of Hosting and Feedback. That moved both
packages to the end of the install order. The trigger was that reorder. The cause was that a write
at the end of the install costs about 300 times what the first write costs. So Hosting, which is the
largest package, was installed at the most expensive point, and the Fleet Console journey ran its
`OperationalSpaceProvisioning.Ensure` (two queries and a few writes) straight after, at that same
point.

## The repro and the profile

A monolith test mesh that writes `Markdown` nodes one after another, 150 per batch, took **3.2 ms per
write at 150 nodes, 22 ms at 1,200 and 88 ms at 4,500**. The growth was linear. A `dotnet-trace`
(`dotnet-sampled-thread-time`) of the late batches put **92 %** of busy time under the provider's
scope walk: a `ToObservableRecursive<(string, QueryScope)>` recursion into
`InMemoryStorageAdapter.ListChildPaths`, reached from the live pipeline's
`CoalesceWhileRunning(RunQuery)`. A tally of the re-queries during 450 writes named exactly three
queries, with 451 re-runs each:

```
path:TestData scope:descendants nodeType:AccessAssignment …          (SecurityQueries.PartitionAssignments)
path:TestData scope:descendants id:_Policy nodeType:PartitionAccessPolicy …   (SecurityQueries.PartitionPolicies)
nodeType:GroupMembership partitions:all scope:subtree …              (SecurityQueries.Memberships)
```

None of these three can ever return a `Markdown` node. They were re-run anyway, because the live
pipeline's relevance test was `PathMatcher.ShouldNotify`, and that test looks at the path alone.

## The fix

**A change is irrelevant to a live RAW query when the query confines its rows to a fixed set of node
types and neither the type the path now holds nor the type it held before is in that set.** That is
`NodeTypeChangeRelevance`, applied in `ObserveQueryInternal` together with the scope test.

- **The prior state travels on the notification.** `DataChangeNotification` now carries
  `PriorStateKnown` and `PreviousNodeType`. Without the previous type, a retype would be missed: a
  package root starts as a placeholder `Space` and is then written as its package type, and in that
  write the row *leaves* one type's result. `InMemoryStorageAdapter` stamps the prior state on every
  write, conditional write and delete, from the row it replaced. It takes that row inside the same
  `Mutate` section as the write.
- **Anything the rule cannot prove counts as relevant.** That covers an unconstrained query, a
  disjunction, a feed that does not know the prior state (every other adapter today), a change with no
  type, the `NextLevel` frontier and a joined change feed. So in the worst case the re-query still
  runs, exactly as it always has. A change is never missed.
- **Only the raw surface uses it.** The security fold reads through `IMeshQueryCore`. A
  row-level-security filtered result also moves with grants, and a grant is exactly a node of *another*
  type (an `AccessAssignment` under the scope makes other rows visible). So the secured `Query`
  surface keeps re-querying on every change under its scope, with one exception: a secured read
  answered as System, which is filtered by nothing (see *The secured surface* below).

After the fix the same repro holds **~1 ms per write, flat**, from 150 nodes to 1,200.

## Regression test

`LiveQueryIgnoresOtherNodeTypesTest` (MeshWeaver.Hosting.Test) counts walks rather than timing them.
It uses a pass-through adapter over a real `InMemoryStorageAdapter` and counts the listings of the
query's base path:

- forty `Markdown` writes under a `nodeType:ProbeWatched` live query cost **0** walks, and then one
  watched write costs exactly one walk and reaches the subscriber. The negative control, with the
  type test removed, gave **41** walks;
- a row retyped away from the watched type, and a deleted watched row, still leave the result. The
  negative control, with the previous type ignored, turned both red;
- the rule refuses every shape it cannot prove.

## The secured surface: System catalogs re-walked the mesh on every write

The fix above covered the raw surface only. The **secured** surface kept re-querying on every change
under its scope, and the platform keeps several secured live queries open for the life of the mesh
whose scope is the whole mesh: the `UiContribution` menu catalog (`UiContributionCatalog`), the
`NodeType` catalogs and the `Store/Plugin` package index. Each is a `partitions:all` query confined to
one node type, and each is read as **System**. So every write, of anything, still re-walked the mesh
three times.

### What it looked like

`MeshWeaver.Reinsurance`'s required check `test-repos / Compile + render node repos (MeshWeaver from
ACR)` runs its gate in one shard: 56 package installs into one in-memory mesh (40 upstream packages
from the sealed Plugins publication, then the repository's own 17). It was cut by the 45-minute job cap
while installing `ReinsurancePractice` (main schedule run 37411388338, ci.10047), and the one green
main run (37414123629) took 41 minutes. The per-node write time of the installer in the cut run
(`── X: installing N file(s)` to `Installed node-repo plugin X: N written`):

| package (install position) | nodes already installed | s/node |
|---|---|---|
| Store (1st) | 0 | 0.006 |
| Edu (15th) | 499 | 0.025 |
| HomeAssistant (26th) | 850 | 0.115 |
| Ifrs17 (30th) | 1,558 | 0.37 |
| Reinsurance (42nd) | 2,319 | 0.63 |
| ReinsuranceDemo (56th) | 3,346 | 1.02 (300 nodes, 305 s) |

Nothing was compiling, baking or waiting: the time went into writes, and the cost of one write grew
with the mesh.

### The measurement

The gate was run locally (`mw-plugin-test` from this repository, the AI module built from
`MeshWeaver.Plugins`, the packages Store, Edu, Essentials, Training and Hosting plus all of
`MeshWeaver.Reinsurance`) with every live re-query tallied by query, viewer and duration:

```
 4156 re-runs   113 s   secured  viewer=system-security  nodeType:Store/Plugin is:main partitions:all
 3804 re-runs   112 s   secured  viewer=system-security  nodeType:UiContribution partitions:all
 2596 re-runs    93 s   secured  viewer=system-security  nodeType:NodeType partitions:all
```

About 2,950 node writes paid for about 10,500 re-walks of the whole mesh.

### The fix

**A secured read answered as System, in a mesh whose every Read validator admits System
unconditionally, IS the raw read, so the node-type test applies to it unchanged.**
`StorageAdapterMeshQueryProvider.SecuredReadIsTheRawRead` decides this when the live query subscribes:

- **The viewer is stamped on the request as `WellKnownUsers.System`.** An explicit request identity wins
  over any ambient one (`QueryIdentityResolver`), so every re-run is filtered for the same viewer.
- **Every validator that judges a Read declares `ISystemReadTransparentNodeValidator`.** Row-level
  security (`RlsNodeValidator`) does; its first line returns `Valid` for System. A Read validator
  without the declaration switches the pruning off for every System query in that mesh, and the
  re-read runs as it always has. That is the safe direction.
- **Any other viewer keeps today's behaviour.** A grant can make other rows visible to it.

The same local run after the fix: about 280 re-queries in all (the `NodeType` catalog still re-reads
when a `NodeType` is written, which is a relevant change). Per-node write cost stayed between 1 and
5 ms from the first package to the last, where it had grown from 3 ms to 136 ms. The whole run took
2 min 36 s instead of 6 min 23 s, and every package and type verdict was identical.

### Regression test

`LiveQueryIgnoresOtherNodeTypesTest` (MeshWeaver.Hosting.Test) gained two cases:

- `ASystemSecuredQuery_SkipsWritesOfAnotherNodeType`: forty `Markdown` writes cost a secured System
  query **0** walks, and then one watched write costs exactly one walk and reaches the subscriber. With
  the System test disabled, the same test counted **41** walks.
- `ASecuredQueryTheRuleCannotProve_StillReReadsOnAForeignWrite`: a secured query for a real viewer,
  and a System query in a mesh with an undeclared Read validator, both still re-read on a foreign
  write.

## What this does not claim

- It does not make a relevant re-query cheaper. A write of an `AccessAssignment` still re-walks its
  partition, and a `GroupMembership` write still re-walks the mesh. Those writes are rare, and they
  are the ones that can change the answer.
- `InMemoryStorageAdapter.ChildrenOf` calls `Drifted()` on every listing, and `Drifted()` reads
  `ConcurrentDictionary.Count`, which takes every bucket lock. That was 39 % of the walk's time. It is
  a constant factor per listing, not the growth, and it is untouched here.
- Postgres publishes no prior state, so production portals keep today's behaviour until their feed
  carries it.

## Related

- [Bake Seal — NodeOps Saturation](../BakeSealNodeOpsSaturation), the earlier reading of the same gate
  symptom.
- [Bounds Must Be Ordered](../BoundsMustBeOrdered), which explains why the 600 s and 10 s bounds were
  left alone.
