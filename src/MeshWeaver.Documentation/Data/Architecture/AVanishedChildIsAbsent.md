---
Name: A Vanished Child Is Absent
Category: Architecture
Description: "The file-system adapter lists a directory in two steps: it takes a snapshot of the subdirectories, then probes each one. A subdirectory deleted between those two steps used to fail the whole listing. A live query then treated the empty answer as the new truth: every row under the parent was reported removed, and the write that triggered the re-query was never listed. A vanished child is now absent, and the rest of the listing stands."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7h5l2 2h11v10H3z"/><path d="M9 14h6"/></svg>
---

# A vanished child is absent

## The one sentence

**When a directory's child disappears while the directory is being listed, the child is absent.
That is not a reason to fail the whole listing.** `FileSystemStorageAdapter.ListChildPaths` takes a
snapshot of a directory's subdirectories and then probes each one for content. A subdirectory that
was deleted between the snapshot and its probe made the probe throw `DirectoryNotFoundException`, and
that exception failed the listing of the PARENT.

## What it cost

A failed listing is not loud. `StorageAdapterMeshQueryProvider`'s walk catches it, records a dropped
read and answers with nothing under that parent. What happens next depends on which read failed:

- **The Initial read.** The frame says it is incomplete (`SnapshotIncomplete`). The synced-query
  cache then refuses to keep it, so the next `GetQuery` call asks again
  ([The query fan-in's Initial gate](../QueryFanInStallTerminal)).
- **A live re-query.** Nothing marks it. `ProcessBatch` compares the empty answer with the rows the
  query already holds and emits `Removed` for every one of them. The write that triggered the
  re-query is never `Added`. Nothing triggers another re-query, so the live result stays wrong
  until some later write lands under the same parent.

The measured case is in MeshWeaver.Plugins, where `AgentListOneChainTest.OneChain_RacingACreate_NeverRegressesFromHasToLacks`
failed in CI job 121872335269. A test class runs a mesh whose file-system persistence is the test
binary's `TestData` folder, and the test classes that run beside it, four at a time, use the same
folder. One iteration created an agent under `ACME/Agent` while a parallel class deleted a sibling
directory there. The live query's re-query listed `ACME/Agent`, the probe of the deleted sibling
threw, and the query never listed the new agent within its 30 s budget. A fresh chain listed it at
once.

A local stress run reproduces it: the same race loop, with a thread that keeps creating and deleting
sibling directories under `ACME/Agent`, missed **9 of 60** creates. The emissions show both shapes:
`n=174 has=False | n=0 has=False` (a live re-query wiped the list), and `n=0` as the very first frame
(an incomplete Initial). With no churn the same loop missed **0 of 150**, and with the fix it misses
none with churn.

The deleting side does not need another process. A node delete prunes its own directory
([Moving Nodes](../MovingNodes)), so one mesh can delete a directory under a parent that one of its
own live queries is listing at that moment.

## The fix

`ClassifyChildDirectory` probes one child. A child that is gone by the time it is probed is
`Vanished` and is left out of the listing, which is the same answer the listing would have given a
moment later. A parent that disappears after its existence check lists as empty, which is the same
as a parent that was never there. Only `DirectoryNotFoundException` is treated this way. Every other
fault (permissions, I/O) still fails the listing and is still recorded as a dropped read.

## Regression test

`FileSystemListingToleratesAVanishedChildTest` (MeshWeaver.Hosting.Test) creates a child directory,
deletes it, and then probes it, which is the step that decides the interleaving. Without the fix the
probe throws `DirectoryNotFoundException`, and with it the probe answers `Vanished`. The negative
control checks that a present child is still classified by its content (`Node`, `Container`,
`NoContent`), so the test cannot pass by answering `Vanished` for everything. The adapter's own
listing is not driven, because the snapshot and the probe run back to back inside one call and there
is no seam to open the window between them.

## What this does not claim

- A live re-query whose read genuinely failed (a timeout, a permission fault) is still applied as if
  it were complete. It reports every unread row as removed, and the write it was answering can stay
  unlisted. That gap is in the provider's live path, not in this adapter, and it is tracked separately.
- `Read` has the same kind of window: it checks that the file exists and then opens it, and a file
  deleted in between is reported as a dropped read rather than as absent. That was not observed to
  cause a failure, so it is left as it is.
- It was not established which test class deleted the sibling directory in the failing CI run. The
  artifacts that would show it were not published.

## Related

- [Live query re-query cost](../LiveQueryRequeryCost)
- [The prune requires a complete listing](../PruneRequiresACompleteListing)
- [Partitioned persistence](../PartitionedPersistence)
