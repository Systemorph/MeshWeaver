---
Name: Live Query Re-query Cost
Category: Architecture
Description: "Why one node write cost more the larger the mesh was: every write re-walked the security fold's permanent live queries. A live raw query now ignores a change whose old and new node types it can never return."
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
  surface keeps re-querying on every change under its scope.

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
