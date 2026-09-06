---
Name: The Recursive-Delete Drain
Category: Architecture
Description: What "drained" means for a recursive delete — why the plan is a snapshot the removals are allowed to exceed, why the completion check must include the ROOT, and why the stage bound measures the gap between removals rather than the operation's duration.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="3 6 5 6 21 6"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/><path d="M10 11v6"/><path d="M14 11v6"/></svg>
---

# The Recursive-Delete Drain

A recursive `DeleteNodeRequest` is not one storage call. `HandleDeleteNodeRequest`
(`src/MeshWeaver.Mesh.Contract/MeshExtensions.cs`) runs six bounded stages, and the last one —
`commit` — is a **drain loop**: fan out, verify against storage, and go round again if anything is
still there.

```text
CollectPathsForDelete(root)          → the PLAN: a point-in-time snapshot of strict descendants
   ↓
RunDeletePass(pass 1, toDelete = plan)
   FanOutDeleteSubtree               → bottom-up; leaves via their own per-node hubs, root last
   ListDescendantPaths(root)         → authoritative RE-enumeration, writable providers only
   ExistsInWritableStorage(root)     → the root is NOT in that enumeration; ask separately
   ↓ still something there?
RunDeletePass(pass 2 … MaxDeleteDrainPasses = 5)
   ↓ nothing left
success — AffectedPaths = every path actually removed
```

Three properties of that loop are load-bearing, and each of them was once wrong in a way that
produced a confident, false answer.

## 1. The plan is a snapshot; the removals may EXCEED it

`CollectPathsForDelete` enumerates the subtree once, before anything is removed. In a live mesh a
writer can create a node under that subtree after the snapshot — a compile-watcher `Release`
satellite, a sync landing a file, another process entirely. The drain's re-enumeration finds it and
a follow-up pass removes it, so the operation legitimately removes **more** paths than it planned.

🚨 **`removed > planned` is a normal, healthy state, and it is the state in which the work is most
certainly done.** Any predicate satisfied only by `removed == planned`, or that keeps waiting while
`removed > planned`, cannot terminate exactly where it should. Nothing in the drain compares the two
counts, and nothing may start: the plan bounds the FIRST pass, not the result.

The in-process half of the same race is closed structurally rather than by re-enumeration: the
subtree-deletion scope (`RecentlyDeletedRegistry.BeginSubtreeDeletion`) opens BEFORE the plan is
taken and refuses every in-process write at or under the root until the operation ends. The drain
loop is what covers writers this process cannot refuse.

## 2. Completion is TWO reads, because one cannot see the whole subtree

`IStorageAdapter.ListDescendantPaths` is **strict descendants — the root is excluded by contract**.
So the enumeration the drain verifies with structurally cannot see the one path the whole operation
is for.

That is why the check asks a second question, `ExistsInWritableStorage(root)`, and reports success
only when BOTH halves are empty:

```csharp
if (survivorSet.IsEmpty && !rootSurvives)
    return Observable.Return((IReadOnlyList<string>)acc);   // drained
```

A surviving root is fed straight back into the next pass, where the root branch's
`DeleteIfExists` removes it; a root that survives every pass fails the operation loudly and names
itself. Before this, a root row that survived the cascade left the subtree with exactly ONE node in
it and the delete still answered success — the shape measured on 2026-09-06, where seven Spaces
were logged `deleted` and each found with one node left.

**Why `ExistsInWritableStorage` and not `Exists`.** `Exists` consults every provider, read-only ones
included, and the question here is "did the delete actually remove the row". A writable override of
a node a read-only provider (Embedded, Static) also serves is deletable — removing the override is
the delete — and the read-only original would keep answering `true` forever afterwards. So the probe
is writable-only, the same set and the same reason as `ListDescendantPaths`. It has a default
(`=> Exists(path)`), which is correct for a single-store adapter; 🚨 **a decorator MUST forward it**,
or the writable-only filter is silently lost at the outermost decorator that falls back to that
default.

**What the predicate still cannot see.** A row that is re-created AFTER the final verification is
outside any completion check by construction — that is the tombstone's job
(`RecentlyDeletedRegistry`), not the drain's. The drain's contract is narrower and now true: at the
moment it answered, storage held nothing at or under the root.

## 3. The stage bound is a NO-PROGRESS watchdog, not a duration cap

Every stage of the delete is bounded by `TimeoutAtStage`, which is `Observable.Timeout` — an
**inter-emission** bound. `DeleteSubtreeUntilDrained` emits exactly once, at the very end of the
multi-pass drain, and an inter-emission bound over a single-emission source degenerates into a
**total-duration cap**: a delete that was removing rows steadily was killed at the budget purely for
taking the budget.

That produced the report that named the defect:

```text
[DeleteNode:commit] the bottom-up delete of 'AgenticOffice' did not drain within 30s
  — 162 of 161 planned path(s) were already removed from storage
```

— a finished delete, declared a failure, which then aborted the remaining 15 steps of an approved
68-step operation. It also explains the asymmetry that made "it was too big" look wrong: a 1394-node
Space **succeeded** because its caller chunked it into 36 calls that each got a fresh budget, while a
106-node Space on the single-call path did not. Whether an operation fitted was decided by how the
CALLER chunked it, not by how much work there was.

The fan-out now publishes a tick per path it removes, and the commit stage merges those ticks into
the bounded sequence:

```csharp
var drainProgress = new Subject<string>();
return drainProgress
    .Select(_ => (IReadOnlyList<string>?)null)              // ticks: timer resets, no result
    .Merge(DeleteSubtreeUntilDrained(…, drainProgress)
        .Select(d => (IReadOnlyList<string>?)d))            // the one real emission
    .TimeoutAtStage(budget, …)
    .Where(d => d is not null)
    .Take(1)                                                // tears the merge down; ticks never complete
    .Select(d => d!);
```

Every removal resets the clock, so a drain that is advancing runs to the end. A drain that stops
advancing for the whole budget still fails — and now says what it measured
(`made no progress for {N}s — {C} path(s) removed from storage so far`) instead of quoting a count
against a plan taken before it started. Total time stays bounded by `MaxDeleteDrainPasses`, and a
genuinely stuck subtree still fails with `could not drain the subtree after N pass(es)`.

🚨 **Raising the budget is not a fix for either half** — the question is never "how much headroom
does this need", it is "is the bound measuring the right quantity, and does the check look at the
whole subtree".

## Where this is pinned

`test/MeshWeaver.Graph.Test/DeleteDrainCompletionTest.cs` drives both symptoms on a real Monolith
mesh: a delete that removes plan + 1 while outliving its operation budget must SUCCEED, a root put
back once must be drained by a follow-up pass so that success means it is gone, and a root that
survives every pass must FAIL naming itself.

## Related

- [Data Access Patterns](../DataAccessPatterns) — the delete API and the rest of node CRUD
- [CQRS — Queries vs. Content Access](../CqrsAndContentAccess) — why the drain verifies against
  storage and never against the query catalog
- [Asynchronous Calls](../AsynchronousCalls) — `Observable.Timeout` semantics and the reactive
  composition rules the delete pipeline follows
