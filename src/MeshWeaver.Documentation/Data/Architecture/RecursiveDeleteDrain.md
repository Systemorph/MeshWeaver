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

### Every write route an owner uses honours the tombstone — the post-commit flush included

A per-node hub keeps holding the pre-delete node until its `DisposeRequest` lands, so a write that
reaches it in that window commits against a node the store no longer has. Three routes make such a
commit durable, and all three must drop it while the tombstone is live: the persistence sampler's
`SaveMeshNodeRequest` handler, the dispose-flush, and `StoragePostCommitFlush` — the durable write
behind every `GetMeshNodeStream(p).Update`. The last one used to skip the check. Its storage write
is an upsert, and `UpdateRemote` answers the writer optimistically, so an update that committed at
the owner after the delete removed the row wrote the row straight back, after the delete had
answered `removed=true`. Measured: an update racing the delete of a live install record left the
record in storage in 7 of 40 rounds. In CI that showed up as `Plugins/{id}` outliving its deleted
partition (`InstallRecordFollowsItsPartitionTest`) and outliving a confirmed uninstall
(`PackageUninstallTest`). In both, the boot repair pass's install-record migration was updating the
record the delete removed.

The flush now refuses such a write. It checks when the write is issued, not when the flush is
built: the delete marks its tombstones before it removes a row (`RunDeletePass`), so a write issued
after the removal always sees the mark. The refusal is a fault, which NACKs the patch, so the writer
learns that its update did not land. A node created again after its delete supersedes the tombstone
(`RecentlyDeletedRegistry.Supersede`), so updates of the new node flush as usual.

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

### A leaf QUEUED behind a moving write lane earns credit, bounded (#1198)

Removals are not the only reset source. A leaf's removal ends in ONE write on a cap-1 `pg:`/`sf:`
write pool, and that pool is one process-wide gate: a leaf queued behind other writers cannot remove
anything until its turn, and before this credit the watchdog read the wait as a stall
(`0 of 1 planned … pg:Postgres(cap 1) 219 waiting, 1 in flight`, memex 2026-10-09). So the commit
stage also merges **queue credits**: the pool registry is sampled every tenth of the budget (at least
100 ms), and a sample resets the clock when BOTH hold since the previous one:

- a cap-1 write lane that holds QUEUED work granted slots — it is advancing, so whoever is queued is
  served in order; and
- **no cap-1 write lane that held work (in flight or waiting) stood still.**

The second condition is the attribution. Admissions are not tied to callers, but a cap-1 lane admits
one writer at a time, so a leaf that was ADMITTED and then hung holds its lane's only slot, and that
lane cannot admit anyone else. Whichever lane the leaf is in, the sample sees a lane with work that
did not move, and no unrelated advancing lane can reset the watchdog for it: **an admitted stuck leaf
still fails at one budget.** The rule errs only toward the old behaviour — an unrelated lane that
stalls denies credit, so a queued leaf may fail at one budget, as it did before.

What the credit cannot see is a leaf stuck OUTSIDE every pool while the lanes advance for others. So
the total credit is capped at **four budgets** (`QueueCreditBudgets`); past that, the watchdog fails
as before, and the line names the pools (`IoPoolQueueReport`). Pinned by
`DeleteCommitQueuedBehindAMovingLaneTest`: a leaf queued behind twelve 600 ms writers on a 4 s budget
completes in its turn, and a leaf admitted and hung in its own lane fails inside two budgets while an
unrelated lane keeps advancing. With the stalled-lane condition removed, the second case ran to 27 s.

🚨 **Raising the budget is not a fix for either half** — the question is never "how much headroom
does this need", it is "is the bound measuring the right quantity, and does the check look at the
whole subtree".

## 4. The pre-flight fan-out is bounded PER LEG, and the stage bound is a backstop

Before the commit runs at all, a recursive delete posts a `ValidateDeleteRequest` at **every**
descendant and waits for every answer (`PreValidateDescendantsObs`). That pre-flight is what makes
the operation atomic: a descendant that refuses — a validator, or the per-leaf
`[RequiresPermission(Delete)]` gate — aborts the whole subtree before one row is removed.

It used to carry **one** bound over the whole fan-out. So one unresponsive per-node hub spent the
entire subtree's budget and the delete was refused by a report about the fan-out rather than about
the node:

```text
[DeleteNode:pre-validate-descendants] 7 of 83 descendant(s) of 'sglauser/AgenticBusiness'
  did not answer ValidateDeleteRequest within 30s …
```

`unanswered=` (issue #1294) made that line READABLE — it names the seven. What one bound could not
do is ATTRIBUTE: the other 76 leaves had answered in milliseconds and still paid the silent ones'
30 s, and the refusal that reached the caller was the STAGE's, at the same rung the whole operation
is bounded at.

Each leg now carries its own bound, one rung inside the stage:

```csharp
PreValidateDescendantsObs(…, budget, opts.Nest(budget), …)   // stage bound, leg bound

// inside, per descendant:
.TimeoutAtStage(legTimeout, () => DeleteStageTimeout(PreValidateDescendants,
    $"the descendant '{p}' did not answer ValidateDeleteRequest within {legTimeout}s …"))
.Catch(ex => ex is TimeoutException
    ? Return((p, ex.Message, NodeDeletionRejectionReason.Unavailable))   // ONE named refusal
    : …)
```

Three properties, and each is the reason for a choice above:

- **The rung is derived, never configured.** `MeshOperationOptions.Nest` is strictly contracting, so
  `leaf answer < leg < stage` holds for every configuration. Equal budgets are not an ordering —
  the outer clock always starts first — and that is the defect #1198 is named after. The leaf's own
  `HandleValidateDeleteRequest` therefore moved one rung deeper too, so a leaf that IS alive still
  gets to say which of *its* reads starved instead of being preempted by the caller's new leg bound.
- **A silent leaf is `Unavailable`, not `ValidationFailed`.** It decided nothing. Reporting an
  availability failure as a verdict sends a correctly-entitled user to request permissions they
  already hold (#1446) — and `IMeshService.DeleteNode` maps the two reasons to different exception
  types, so the distinction reaches the caller.
- **The fan-out is capped** (`PreValidateFanOutConcurrency`). A bare `Observable.Merge` subscribes
  every leg at once, and each post can ACTIVATE that leaf's per-node hub — an unbounded activation
  burst on a subtree of any size. The cap cannot strand the fan-out, because every leg terminates
  within its own budget. It does mean the "outstanding", "posted" and "planned" counts are three
  different numbers, and the stage backstop reports all three: claiming a leaf "did not answer" a
  request that was never sent is the same class of untruth as the `unanswered=-` this issue already
  fixed on the commit stage.

The stage bound is a **no-progress backstop**. It observes every leg answer before the refusal
filter drops valid (`null`) results, so each answer resets its clock. That ordering matters on a
large space: the `UWDeepfield` delete had 31,138 descendants, 64 in flight, and about 1,676 valid
answers in 25 seconds. The old placement after the filter saw none of those answers and refused a
fan-out that was still advancing. The fixed placement keeps the same stage and per-leg budgets;
only a gap between answers can reach the stage backstop, while a silent individual leg reports
itself by name.

### A leg that reports an ABSENCE is confirmed against storage first (#4680)

The pre-flight and the commit both address the **one** snapshot stage 3 enumerated, so a concurrent
delete that removes a planned leaf at any point afterwards leaves the operation holding a path that
really is gone. Such a leg refuses in one of two vocabularies — a `ValidateDeleteResponse` carrying
`NodeNotFound` while the leaf's hub is still activated, otherwise a routing failure whose sentence
names three different situations at once — and neither of them distinguishes "already deleted" from
"still stored, and its NodeType will not load". The second is exactly what this pre-flight exists to
refuse before a row is removed.

So an absence-shaped leg failure is confirmed against the store of record
(`MeshExtensions.ConfirmDescendantGone` → `IStorageAdapter.Exists`) before it is believed: confirmed
gone blocks nothing, still-stored keeps today's refusal verbatim, and a store that cannot answer is
"not confirmed gone" so the refusal stands. It is a read on an error path only, one rung inside the
leg it runs in — the `.Catch` is past the point the leg's own bound covers. The commit leg takes the
same reading and then emits nothing, so a leaf somebody else removed is never counted among the
paths this delete removed. Full account:
[Deleting What Is Already Gone](/Doc/Architecture/IdempotentDelete).

### What is NOT fixed: the commit's writes serialize process-wide

#1198's third item reads *"the `commit` stage's N-deletes-over-a-cap-1 `pg:{adapter}` pool"*. As
written it is **falsified** by §3 above: since the bound became a no-progress watchdog, serializing
the drain's own N writes cannot time it out — every removal resets the clock.

The mechanism that replaced it: the cap-1 `pg:{provider}` write pool is **one process-wide gate**,
not one per partition — every per-schema adapter is handed the same pool and the same shared
`NpgsqlDataSource`. So a delete's next leaf removal queues behind *unrelated* writes from every
other partition, and those do not tick this delete's progress. A portal under sustained write load
could therefore starve one drain for a whole budget with zero removals, which is the
`made no progress for 30s` shape logged on 2026-09-14.

### The reading, 2026-09-16 — and it does not support that

`IIoPool.QueueWait` made the queue readable; the first reading was taken on
**the control instance**, pod `memex-portal-deployment-7cb6684584-jdw7v`, image
`3.0.0+afde4eab` — the first image to carry the instrument — after **828 minutes** of uptime:

| pool | cap | admissions | mean wait | max wait | ≥ 1 s | ≥ 10 s |
|---|---:|---:|---:|---:|---:|---:|
| `pg:Postgres` (write) | 1 | 2,786 | 6.5 ms | **205 ms** | **0** | **0** |
| `pg-read:Postgres` (read) | 16 | 31,897,169 | **342 ms** | 1,661 ms | 48,122 | 0 |

**The write pool's worst single queue wait in 13.8 hours was 205 ms, against a 30 s budget** — 0.7%
of it — with both tail buckets at zero. Nothing waited even one second. The starvation mechanism
above did not occur on that portal, and not within two orders of magnitude of the magnitude it would
need. The cap-1 write gate is **not** the constraint there.

🚨 **The contended pool is the READ pool, and nothing in this issue's history was looking at it.**
It carries 11,450× the traffic at 53× the mean wait, and 77% of its admissions land in the
[100 ms, 1 s) bucket — it queues *routinely*, not occasionally. That matters here because a
commit's per-leaf work is mostly READS: every cascade leg re-enters the handler and pays a root
read, a permission fold, a descendant enumeration and an existence probe before it writes anything.
If pool queueing delays a drain, this is where it comes from.

**What the reading does not settle.** Its denominator is ONE portal, ONE pod, ONE process lifetime —
and it is not the portal that produced any logged occurrence. The public instance, where all of them
happened, runs an image from 2026-09-12 that predates the instrument, so the question cannot yet be
asked there. And a high mean on `pg-read` is not by itself a cap that is too small: `InvokeStream`
holds one slot for a whole enumeration, so long-held slots and too-few slots produce the same mean
and are different problems.

🚨 **Raising either cap is still not the fix, and now there is a measurement saying so rather than
an absence of one.** The write pool has no queueing to relieve. The read pool's queueing has no
diagnosis yet — and the caps are half a connection budget (16 reads + 1 write under the shared
source's `MaxPoolSize=50`, see [Controlled I/O Pooling](../ControlledIoPooling)), so spending the
headroom needs a reason, not a symptom.

### The reading on the producing portal, 2026-10-09 — the backlog was the delete's own

The 2026-09-16 reading was not taken on a portal that produced an occurrence. This one was. On the
control instance (pod `…-cfb58787b-b6klj`, image `3.0.0-ci.10310`) two bulk deletes failed 45 minutes
apart, and each failure line carries the write-pool reading for its own stage:

| delete | removed before the abort | `pg:Postgres` waiting at the timeout | admissions ≥ 1 s in the stage |
|---|---:|---:|---:|
| `Marketing` (13:38:58Z) | 1,383 | 219 | 1,325 |
| `SocialMedia` (14:24:07Z) | 1,060 | 764 | 1,031 |

The failing leaves were themselves leaves of the delete: in the `SocialMedia` burst the governed
`Logs` read `Ops/Actions/logs-sevM-a4315-20261009-q1` landed **194 `[DeleteNode:commit] … made no
progress for 25s — 0 of 1 planned` lines inside one second**, every one under `SocialMedia/`. So the
"unrelated writes from every other partition" mechanism above was not the cause here. The queue was
the operation's own leaf writes.

**Why the cascade queued itself.** `HierarchicalPathDeletion` fans every sibling set out with an
unbounded `Merge`, and the levels nest, so the commit posted one leaf delete per planned path **at
once**. Each leaf re-enters `HandleDeleteNodeRequest` at its own hub, opens its **own** commit stage
(a no-progress watchdog that starts when that hub takes the request), and ends in one write on the
one cap-1 pool. A leaf at position 700 in that queue waits about 700 pooled writes. Its own watchdog
sees nothing move for its whole budget while the cascade it belongs to is removing rows steadily.
Then that leaf's timeout fails the whole delete. This is the same queue-versus-running confusion as
the routing gauge in #5703. The pre-flight had been capped for this reason all along
(`PreValidateFanOutConcurrency`), and the commit had not.

**The fix (#6351).** The commit's per-leaf legs share **one admission lane** for the whole tree,
`HierarchicalPathDeletion.DeleteSubtreeBounded`, sized by `MeshOperationOptions.CascadeFanOutConcurrency`
(64 by default, the pre-flight's value). The lane is shared because a per-level cap does not hold:
nested levels multiply. Ordering is unchanged. A node is admitted only after its descendants
complete, unrelated branches still progress independently, and the first failing leg still fails the
traversal. A leg still queued when the traversal fails or is disposed never starts its delete. With
64 in flight, a leaf waits behind at most 64 of its siblings' writes, which is about a second at the
measured ~20 ms per pooled write. No budget, cap or retry changed.

**A second defect on the same line.** The cascade wraps a failed leaf in an
`InvalidOperationException` to name the path, and the root read that wrapper's type as the reason:
`ValidationFailed` plus an `Unexpected error:` prefix. `MeshService.DeleteNode` maps that to an
`UnauthorizedAccessException`. So the delete-space script reported an availability failure as a
permission denial (`---> System.UnauthorizedAccessException: Unexpected error: Delete failed for
'SocialMedia/PostsHub/Source/PostsHubLayoutAreas': … exceeded 25s timeout in stage 'commit'`). The
wrapper now carries the leaf's own `NodeDeletionRejectionReason`, and the root reports that reason.

**Pinned by** `WideDeleteBoundedFanOutTest` / `WideDeleteUnboundedFanOutTest` (Graph.Test). The test
uses a 160-leaf root over a storage adapter that serves deletes one at a time at 50 ms, which models
the cap-1 pool, against a 2.5 s leaf budget. Bounded, the delete succeeds and the store is empty.
Unbounded, the negative control reproduces the production line, and with the reason carry disabled
it reproduces the `UnauthorizedAccessException`. The lane itself is pinned by
`HierarchicalPathDeletionTests` (`Bounded_lane_*`, plus an unbounded negative control).

### What the commit timeout now says

The reading that decides this is free at the moment the watchdog fires — `IoPoolRegistry` is
mesh-scoped and every counter is lock-free — so the commit stage takes it and puts it on the line:

```text
[DeleteNode:commit] the bottom-up delete of 'Hosting/TriageStatus' made no progress for 30s —
3 of 9 planned path(s) removed from storage so far; still owed by the plan: … . At the timeout:
no I/O pool had work queued at that moment, and no admission during this stage waited a second
for a slot
```

🚨 **It is a WINDOW, not an instant, and that distinction is the whole reading.** A queue depth
sampled once at the timeout cannot exonerate a cap: a leaf can wait most of the budget for a slot,
be granted it, and only *then* stall — by which time the depth is zero and an instant-only reading
would report the pools as innocent. So the stage snapshots the pools as it OPENS and the timeout
differences the wait buckets against that baseline. "Nothing queued now" is half the sentence; "and
nothing admitted during this stage waited a second" is the half that makes it mean anything.

🚨 **The two answers are still not symmetric, and the wording keeps them apart.** The clean reading
rules out **the pool gates** — no cap held this drain up — and says nothing about where the drain
*was* stuck; storage is the likeliest remaining candidate, not a proven one. *These pools had work
queued* is a LEAD in the other direction: the buckets are process-wide, so a slow admission during
the window may belong to an unrelated caller, and it stays a coincidence until something ties this
operation's own leaf to it. Which is why the report says **had work queued** and never *caused*.

A third sentence exists on purpose. `IoPoolQueueReport` distinguishes "no registry on this hub — the
reading was not taken" from "taken, and nothing was queued", because collapsing those is how an
unmeasured pool comes to look like an idle one. `IoPoolRegistry.Snapshot()` enumerates the pools
that EXIST and mints none, for the same reason: `Get` is a resolver, and a readout built on it
answers a wrong name by creating that pool and reporting it, brand new, as idle.

🚨 **And the verdict is only as wide as the word "waiting", which had a hole in it.** Both halves of
the clean sentence are computed from `CurrentlyWaiting` and the `QueueWait` buckets, and both used to
start counting **at the gate**. Since the admission moved to the subscriber's thread (#4555), three
of the four entry points reach that gate one ThreadPool hop later — so a leaf the pool had ACCEPTED,
and that `Drain()` was already waiting for, counted in neither number. Measured on `main`: one leaf
parked in that interval read `InFlight=0 Waiting=0` and the report returned the CONCLUSIVE sentence
over it; and with the ThreadPool saturated, eight leaves waited up to **7,850 ms** from accepted to
running while the distribution's maximum read **0.2 ms**. An exoneration computed from a number that
cannot see the wait is precisely the instrument failure this issue is named after — one level inside
the instrument built to settle it.

The clock now starts where the admission is taken, which is what `InvokeBlocking` always did, so
"waiting" means **accepted and not yet running** on every entry point and the verdict covers the
latency an operator reads it as covering. The reading it changes is the pool's, not this drain's: a
`pg:` write pool whose leaf is queued behind ThreadPool starvation now says so instead of reading
innocent. Pinned by `IoPoolQueueReadingCoversAcceptedWorkTest`, whose fourth case is `InvokeBlocking`
— green before and after, because it is the precedent the other three now follow.

## 5. Record satellites leave in batches, not one leaf round-trip each

Every planned descendant used to cost two activations of its own per-node hub: one to answer the
pre-flight `ValidateDeleteRequest`, and one to commit its own `DeleteNodeRequest`. On
memex.systemorph.com on 2026-10-09, deleting the retired NodeType `Crm/Client` took more than 60 s.
It held several hundred `_Activity/compile-*` records, and each one paid both activations. The
bounded lane from §4 keeps that from failing, but it does not make it cheap.

A row whose first satellite segment is declared in `MeshOperationOptions.RecordSatelliteSegments`
(default `_Activity`) is a RECORD. Nothing registered on its own hub is needed to delete it: it has
no type-specific validator and no post-deletion handler. When its path-derived owner is the
delete's root, or a node in the delete's own plan, the row:

- is validated IN-PROCESS instead of at its own hub. The pre-flight reads the row from storage
  and runs the same delete-validator chain on it under the caller's identity. That chain includes
  the RLS validator, which walks the row's own access rule against its STORED `MainNode` (for an
  activity, `SatelliteAccessRule` requires Update on it). The verdict is therefore the row's own,
  and no hub is activated;
- is removed in the commit through `IStorageAdapter.DeleteMany`, `RecordSatelliteBatchSize` rows
  per call (default 100), children first, BEFORE the per-node walk removes the owners.

Each removed row still gets every per-node side effect that does not need a hub: the
change-feed `Deleted` event in children-first order, the stream-cache invalidation, disposal of an
activated per-node hub, the "delete wins" tombstone and the subtree write guard (both set before
the commit), and one tick of the no-progress watchdog per row. A row the batch did not actually
remove is not reported as removed.

A satellite whose owner is NOT in the plan, such as `{root}/Ghost/_Activity/x` with no node at
`{root}/Ghost`, keeps the per-node lane. A
segment whose rows gain per-node delete semantics must leave the declared set in the same change.
An empty set restores the per-node lane for everything.

**Pinned by** `RecordSatellitesLeaveInBatchesTest` (Graph.Test): 120 activity records under a
deleted root leave in three `DeleteMany` calls with no one-row delete, while the orphan-owned
satellite takes the per-node lane. `RecordSatellitePreflightStillDeniesTest` shows the in-process
validation is not a bypass: one record that a delete validator refuses refuses the whole delete,
by that record's name, before anything is removed. Its negative control, `RecordSatellitesPerNodeWhenUndeclaredTest`,
clears the declaration, and every record then pays its own leaf delete, as production did.

The other half of the fix is upstream: a NodeType no longer accumulates those records without
bound. See [Node Type Compilation](../NodeTypeCompilation), "Compile history is bounded".

## Where this is pinned

`test/MeshWeaver.Graph.Test/DeleteDrainCompletionTest.cs` drives both symptoms on a real Monolith
mesh: a delete that removes plan + 1 while outliving its operation budget must SUCCEED, a root put
back once must be drained by a follow-up pass so that success means it is gone, and a root that
survives every pass must FAIL naming itself.

`UpdateNeverResurrectsADeletedNodeTest` (MeshWeaver.Graph.Test) pins the post-commit flush
deterministically. A flush that arrives after the delete is refused and the node stays gone, while
a flush of a live node and a flush of a node created again after its delete both persist. Reverted,
the first case fails. `InstallRecordUpdateRacingDeleteTest` (Memex.Portal.Shared.Test) races the
install-record migration's update against the record's delete for 40 rounds and allows no record
to survive. Reverted, it fails with survivors.

`DeleteCommitTimeoutNamesWhatIsStuckTest` pins what a stalled commit SAYS — the paths the plan still
owes, by name. `DeletePreflightNamesTheSilentDescendantTest` pins §4: a subtree whose middle
descendant carries a validator that never emits must be refused by that ONE path's name, as
`Unavailable`, inside the stage's budget, with its siblings answered and the subtree untouched.
Reverted against `main` it fails exactly as production did — the stage backstop, at the full budget,
reporting the fan-out instead of the node.
`DeletePreflightProgressTest` drives the pre-flight's timeout with virtual time: three valid
answers over more than two stage budgets keep it alive, while one full budget with no answer still
fails. It pins the ordering between the timeout and the valid-answer filter.

## Related

- [Data Access Patterns](../DataAccessPatterns) — the delete API and the rest of node CRUD
- [CQRS — Queries vs. Content Access](../CqrsAndContentAccess) — why the drain verifies against
  storage and never against the query catalog
- [Asynchronous Calls](../AsynchronousCalls) — `Observable.Timeout` semantics and the reactive
  composition rules the delete pipeline follows
