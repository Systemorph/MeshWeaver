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

### A write admitted BEFORE the scope opened is waited for, not outrun

The scope refuses a write at **admission** — when the write is subscribed at the outermost storage
decorator (`SubtreeDeletionGuardStorageAdapter`). That alone left one ordering open (#6719): a write
admitted a moment before the scope opened is already on its way to the store — queued on the I/O
pool, or slow at the backend — and commits whenever the backend reaches it. Nothing ordered that
commit against the delete, so it could land AFTER the storage delete, recreate the node the delete
had just answered "removed" for, and its post-commit `Supersede` then erased the tombstone, so the
recreate looked like a legitimate re-create. It also outran the drain: it landed after the last
verification.

The ordering is closed from the delete's side, in two halves:

- **The guard REGISTERS every write it admits** (`RecentlyDeletedRegistry.AdmitWrite`) until that
  write ITSELF terminates — completion or error. A caller unsubscribing is NOT a settlement: the
  store adapters are eager (`IIoPool.Run` keeps the I/O running whoever unsubscribes), so a write
  whose caller timed out can still commit. The guard therefore connects every admitted write
  itself, releases the registration only on the write's own terminal, and hands the caller a
  replay; an unsubscribe only stops the caller listening. Three details make "terminal" mean what
  it says:
  - The caller is attached to the replay BEFORE the write is connected. A write completing
    synchronously inside the connect would otherwise release its registration — and let a waiting
    delete remove the row and publish `Deleted` — while its result still sat in the buffer, so the
    caller's own `Created`/`Updated` publish would follow the delete's.
  - A batch (`WriteMany`) is snapshotted once: the paths registered and tested are the very nodes
    handed to the store.
  - A write's secondary writes under its own path settle WITH it. The monotonic guard's
    write-conflict trace (`{path}/_Activity/write-conflict-{version}`) is written below this guard;
    it used to be fire-and-forget, so the parent settled, a delete ran, and the trace landed after
    it. The conflict resolution now completes only once the trace has terminated (still best
    effort: a failed trace is logged, never a failed write).
- **The delete, once its scope is open, waits for every write admitted under its root to settle**
  (`AdmittedWritesSettled`, its own bounded stage `settle-admitted-writes`) before it checks the
  expected version, takes the plan, or removes anything. `WithinSubtreeDeletion` — the move and the
  partition teardown — does the same before its body runs. A **row-only** delete (`KeepDescendants`)
  waits for writes to the row alone: it guards only that row (`BeginRowDeletion`), so writes to the
  descendants it keeps stay admitted throughout and would otherwise time it out for nothing.

The scope stops new admissions, so the set it waits on only shrinks, and in the common case it is
empty and the wait answers at once. 🚨 **Each side acts first and checks second:** the guard
registers and THEN tests the scope; the delete opens the scope and THEN reads the registrations. Both
are full-fence dictionary writes, so at least one side always sees the other — the write is refused,
or the delete waits for it. Checked the other way round, both could miss each other.

So, in-process, every write under the root is either **refused** or **committed BEFORE the delete,
which then removes it** — never committed after it. The delete answers success only once that holds.
A write that never terminates fails the delete by name at `settle-admitted-writes`, listing the
paths it waited on, with nothing deleted. Not covered: a writer in ANOTHER process, which this
registry cannot see; the drain's re-enumeration remains its only cover, and only up to the last
verification. Regression test: `AWriteAdmittedBeforeADeleteNeverRecreatesItTest` (parks an admitted
write at the store of record, starts the delete, releases the write only once the delete's scope is
provably open, and asserts the delete waited and the store holds nothing afterwards).

### A write admitted at its owner's COMMIT is waited for too

The guard registers a write only when the write reaches it, and a per-node hub reaches it late: a
cross-hub patch is checked against the scopes in the owner's merge turn (`IActiveDeletionScopes`,
#6953), committed to the owner's in-memory node, and only then flushed on the commit echo
(`IPostCommitFlush`). A delete opening its scope in that gap found nothing registered, ran, and the
flush was then refused — by the open scope, or by the tombstone once the delete had answered — after
the owner had committed and served the write (#7346). The writer heard "not applied" for a write that
had happened, the #6953 disagreement in its narrower form.

So the owner's check is an admission too: `IActiveDeletionScopes.AdmitCommit` registers the write
with the admitted writes FIRST and tests the scope SECOND (the same order as the guard's), and the
handle travels from the merge turn to the flush, which releases it on its own storage write's
terminal — never on the acknowledgement, which the flush bound may post while the write is still in
flight. Every path that commits nothing (refused, unchanged, faulted, an echo that never arrives)
releases it at once. While the handle is held:

- a deletion opening its scope waits for it in `AdmittedWritesSettled`, exactly as for a write the
  guard admitted;
- the guard lets a write to that PATH through the open scope (`IsAdmittedAtCommit`) — the deletion is
  waiting for it, so it lands BEFORE the deletion, which then removes it. By path, because the commit
  and the storage write are different objects on different threads; a second write to the same path
  passing in that window is registered by the guard and waited for as well, and no new admission is
  granted under an open scope, so the set still only shrinks.

Two orderings keep the admission honest. The guard's bypass for a path is published only once the
scope check has ACCEPTED the commit — published before it, a refused commit briefly let another write
to that path through a scope whose deletion had already finished waiting
(`ARefusedCommitNeverOpensTheGuardTest`). And the commit decision is one compare-and-swap against the
end of the patch's echo watcher (`CommitAdmissionHandoff`): the watcher is the only route to the
flush, so a turn whose watcher has ended (its bound expired while the merge was queued, or the owner
went away) commits NOTHING and answers "not applied", and the watcher never releases the admission of
a turn that is still deciding (`ACommitAdmissionNeverOutlivesItsWatcherTest`). A turn that committed
and whose echo then never arrives is released at the watcher's end; the sampler stores it, as for the
owner's own write below.

Because the bypass lets a NEW registration appear under an open scope, the drain's "is anything
pending under the root" test reads an atomic snapshot of the registrations, never the live
enumerator: the guard's write registers before the commit admission it overlapped is released, and a
live enumeration could pass the new entry's bucket before the add and reach the old entry's after the
remove, seeing neither (`AnOverlappingAdmissionHandoffIsNeverMissedByTheDrainTest`). And a turn that
aborts because its watcher ended during the owner's teardown stands aside for the ShutDown-phase
disposal NACK exactly as the watcher does, so the writer's retry still lands on a fresh activation.

A patch that reaches the owner AFTER the scope opened is still refused at commit. Regression test:
`AWriteAdmittedAtCommitIsWaitedForByTheDeleteTest` (parks the owner's flush above the guard after the
commit, starts the delete, releases the flush only once the delete has provably begun, and asserts the
write is acknowledged, the delete waited for it, a late patch is refused, and the store holds nothing
afterwards). Not covered by this: the owner's OWN write (`UpdateOwn`) is acknowledged at its commit and
stored by the persistence sampler, which has no per-write answer to end such an admission on. It is
deferred under an open scope (`MovingNodes`); one committed just BEFORE a scope opened and not yet
sampled is not waited for. For a delete that is harmless (the delete removes the node after the write
was acknowledged); whether a move's copy can miss such a write has not been established.

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

### An enumeration that gave NO answer is not an empty subtree (#7461)

`ListDescendantPaths` "emits one complete snapshot and completes". A store that completes WITHOUT
emitting has not said the subtree is empty — it has said nothing. The production composite
(`PersistenceService`) folded every writable provider's answer into a SEEDED union, so a silent
provider contributed nothing and the union still emitted; the interface's default walk had the same
seeded fold one level down, so a nested `ListChildPaths` that completed empty dropped its whole
subtree. Both read "no answer" as "no descendants". The planner would delete the root alone, and the
verifier, asking the same silent store, would report the subtree drained.

An empty completion is therefore a FAULT, `SubtreeEnumerationUnansweredException`, raised before any
fold and naming who gave no answer. It is raised at four places: each provider in the composite, each
level of the default walk, the routed `ListDescendantPathsRequest` handler (which otherwise posted no
response at all), and the outermost storage decorator (`SubtreeDeletionGuardStorageAdapter`), which
covers a single-store mesh that has no composite. Every caller that already failed closed on a
fault (the delete planner and drain, the move's delete leg, `SpaceDeletion`'s install-record read,
the partition teardown sweep) now fails closed on a missing answer too. A store with nothing under
the root answers with an EMPTY collection, which is an answer and passes. Measured before the fix
on a Monolith mesh, a recursive delete over a silent store did not fail. It never answered at all,
and the caller waited out its whole budget.

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
`DeleteCommitQueuedBehindAMovingLaneTest`: a leaf queued behind at least nine 600 ms writers that are
at the gate when it asks for its slot (`QueuedAhead` — 5.4 s of queue on a 4 s budget) completes in
its turn, and a leaf admitted and hung in its own lane fails inside two budgets while an
unrelated lane keeps advancing. With the stalled-lane condition removed, the second case ran to 27 s.

### A delete queued in a serial write lane behind another kind of write earns credit (MeshWeaver#7464)

The Postgres backend writes each partition schema through its own `SerialWriteLane` (one write at a
time, on slots of the `pg-write:{provider}` budget) rather than through one process-wide cap-1 pool. A
partition-root or membership write parked on a database-wide lock then holds that schema's lane, and a
delete leaf queued behind it waits in a lane that does not move — which the rule above reads as a
stall. The lanes are therefore read too (`IoPoolRegistry.SerialLaneSnapshot`), and unlike a pool a
lane knows the KIND of its writes: how many deletes wait, and whether the write in flight is a delete.

- A delete queued behind a write that is NOT a delete earns credit: it is waiting its turn behind a
  write that is not this delete's own leaf.
- A lane whose head IS a delete and stood still still denies credit, exactly as a cap-1 pool always
  has: that head may be this delete's own admitted leaf hanging.
- With nothing queued, nothing is credited, and the four-budget cap still bounds the total.

Pinned by `DeleteQueuedInASerialWriteLaneEarnsCreditTest` (with the new clause removed, the first case
fails), and in MeshWeaver.Plugins by the Postgres lane tests.

🚨 **A pool's `Waiting` is not its queue order — read `BehindCap` before claiming a leaf is behind
other work.** A pool counts a leaf as waiting from the moment it is ACCEPTED, one ThreadPool hop
before the leaf reaches the gate (`IoPoolAdmission.BeforeGate`), and the gate is first-come-first-served
among the leaves that have REACHED it. The first version of that test loaded the lane with twelve
holders, waited for `CurrentlyWaiting >= 11` and started the delete. Run alone it passed; run with the
whole project in one process (macOS arm64, Release) it failed four runs out of four with
`Expected 00:00:01.8188890 to be greater than 00:00:04`: the delete's leaf reached the gate behind
three of the twelve accepted holders, was served after 3 × 600 ms, and the watchdog was never
involved. The same reading is reproduced with no load at all by parking nine holder prologues before
the gate (`IoPool.OnLeafPrologueStarting`): `11 waiting, 1 in flight` with `before the gate 9, behind
the cap 2`, and the delete returns in 1.9 s. So the test now reads the load where the order is decided
(`BehindCap`), keeps the queue at depth until the delete's leaf asks for its slot, and compares
the budget with the leaf's own wait, measured by the store it queued in — never with a wall clock
started at the request, which also charges the watchdog for every stage before the commit. A
replacement holder is itself only ACCEPTED until it has had a thread, so the queue ahead of the leaf
is pinned rather than hoped for: the instant the leaf asks — before the pool has heard of it — the
test counts the holders at the gate (all ahead of the leaf by construction) and stops the lane
draining, and it lets the lane move again only when nothing accepted is still before the gate, which
is the leaf having joined the queue behind exactly the holders that were counted. The
product was not at fault here, and the credit's own rule is unaffected: a lane whose work is still
before the gate reads as busy-and-not-advancing, which denies credit — the conservative direction.

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

### A failure after removals says the subtree is partial, and that a retry is meaningful (#6351)

A recursive delete that fails AFTER it removed nodes has left a torn subtree, and the leaf's own
sentence says nothing about that. Measured in production: `[DeleteNode] unexpected path=Marketing
partial-deleted=1383` — the operation had removed 1,383 paths when one leaf's commit watchdog
fired, and the caller read only the leaf's watchdog line.

So when the removals before the failure are non-zero and the failure is an **availability** one —
the stage's own timeout, or a leaf refused as `Unavailable` — the failure the caller reads keeps its
cause unchanged and appends one sentence: how many nodes were already removed, that the subtree is
partially deleted, and that the delete is idempotent, so retrying it is meaningful and removes what
is left. The same sentence is added to the response's `Log` as a Warning, keyed
`activity.delete.partialRetriable`, so the activity a user opens carries it in their language.

🚨 **Only availability failures get the retry advice.** A permission denial or a validator's refusal
is a decision, and asking again changes nothing — those arms keep their own sentence with no retry
note, and an availability failure never reaches the caller as a permission denial.
`PartialDeleteIsReportedAsRetriableTest` holds the lane after 40 of 160 removals and pins both the
sentence and the retry: once the lane serves again, a second delete removes everything left.

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

**A batch that fails part-way retires what it removed (#7396).** `DeleteMany` used to report its
removed set only when the whole batch succeeded, so a batch that removed some rows and then faulted
left the hubs at those rows activated under addresses that name no node. The storage contract now
says what a part-way failure removed: the fault is a `BatchDeletePartiallyAppliedException` whose
`Removed` lists exactly the rows that left storage, and any other fault means no removal was
confirmed. The default implementation (one row at a time), the composite's union over its writable
providers (every provider is heard to the end, so one provider's failure does not hide another's
removals; a provider whose `DeleteMany` throws while being called is that provider's failure, not the
batch's; and a path one provider removed but a FAILED provider still holds is not reported, because
the node is still readable), the history-purging and write-guard decorators and the remote partition handler
(`DeleteBatchResponse.DeletedPaths` alongside its `Error`) all keep it. The commit gives those rows
the same side effects as a completed batch's rows, and the commit's failure carries them. A row the
batch did not remove keeps its hub. A backend override that removes in several statements, such as
the Postgres adapter's one statement per target table (MeshWeaver.Plugins), must report the rows of
the statements that committed; until it does, its part-way failure is a bare fault and retires
nothing, as before. The fault's message is the store's own, verbatim, so no framework-authored
English reaches a move's or a delete's failure text. Pinned by
`APartWayBatchDeleteRetiresWhatItRemovedTest` (FaultInjection.Test) and
`ABatchDeleteThatFailsPartWayNamesWhatItRemovedTest` (Hosting.Test).

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

`AnUnansweredSubtreeEnumerationIsNotAnEmptySubtreeTest` (MeshWeaver.Hosting.Test) pins #7461 on the
composite, the default walk and the outermost decorator. In each, a store that completes empty must
fault, and a store that answers with nothing must still emit an empty collection.
`AnUnansweredSubtreeEnumerationRefusesTheDeleteTest` (MeshWeaver.Graph.Test) drives a recursive
delete over a silent store on a real Monolith mesh. The delete must fail, name the missing answer and
leave the child stored, and a leaf delete over an answering store must still succeed. Reverted, the
delete never answers within 60 s.

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
