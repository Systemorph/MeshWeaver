---
NodeType: Markdown
Name: "Teardown Layers — work finishes, nothing is forced"
Abstract: "The mesh tears down in two layers: the APPLICATION layer (activities, pooled I/O, handler turns) is drained first and given the chance to finish its job; the INFRASTRUCTURE layer (hubs, action blocks, scopes) goes down behind it. Nothing is cancelled on entry and nothing is torn down out of band: a unit of work that stops making progress is detected by a stall budget, cancelled once cooperatively, and reported at Error with everything a reproduction needs. Why the forced teardown was removed, what the five stall verdicts are, and what production measured."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#00695c'/><path d='M5 8h14M5 12h10M5 16h6' fill='none' stroke='white' stroke-width='1.8' stroke-linecap='round'/></svg>"
Thumbnail: "images/DataMesh.svg"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Disposal"
  - "Lifecycle"
  - "Operations"
---

# Teardown Layers — work finishes, nothing is forced

> **The rule.** A teardown lets the work the mesh has accepted **finish its job**, and it never
> pretends to be done while that work is running. Two layers go down in order: the
> **application layer** — activities, pooled I/O, handler turns — is drained first; the
> **infrastructure layer** — hubs, action blocks, scopes — goes down behind it. Nothing is
> cancelled on entry. A unit of work that has **stopped making progress** for one stall budget is
> *wedged*: it is cancelled once, cooperatively, and reported at **Error** by name with the
> evidence a reproduction needs. A unit that ignores that is reported again and left behind so
> the process can exit — but the hub that owns it stays honestly *pending*, it never signals a
> completion that is not true. **Forced teardown does not exist.**
>
> The hub-level mechanics are in [Hub Disposal Model](/Doc/Architecture/HubDisposalModel); the
> mesh-level order in [Mesh Lifecycle](/Doc/Architecture/MeshLifecycle); the pool drain in
> [Controlled I/O Pooling](/Doc/Architecture/ControlledIoPooling). This page is the policy those
> three implement, and the evidence that fixed it (maintainer directive, 2026-09-04).

---

## Why forced teardown was removed

Until 2026-09-04 every hub armed an 8 s watchdog on `Dispose()`. When the disposal state machine
made no progress for 8 s the watchdog ran the teardown **itself, from its timer thread**: hosted
hubs disposed, callbacks cancelled, registered cleanups run, message service stopped, `Dead`
signalled — while the turn that had wedged the hub was still executing on the action block. Two
things were true of that design and both were measured:

- **It lied about completion.** A parent whose child was force-torn-down advanced to its own
  `ShutDown` phase against a subtree that was mid-flight. In `MeshWeaver.Graph.Test`, the
  `OwnerDisposing` NACK for a patch in flight at teardown could only be minted **after** the force
  had fired, so the waiting caller's whole budget was "the watchdog plus two seconds" — and a
  loaded CI shard lost those two seconds (run 33847949620).
- **It ran in production dozens of times per shutdown, and while serving.** Loki on `memex-cloud`
  (2026-08-29 → 09-03) shows `DISPOSAL DEADLOCK DETECTED: Hub sync/… (last progress: sync/… →
  ShutDown). RunLevel=ShutDown` followed by `[FORCE-TEARDOWN] … out-of-band teardown complete
  after 22706ms`, on sync-stream hubs and on the node hubs above them (`AgenticBusiness/Handbook`,
  `app/Northwind`, `rbuergi/OpenStreetMap/Examples`), including at 16:24 on a pod that was not
  shutting down at all. Each of those was a hub reported disposed with work still holding it.

The replacement is a **stall detector** that observes and reports. It keeps everything the
watchdog got right — it measures a stall, re-armed on every `RunLevel` transition anywhere in the
subtree, so a slow nested teardown never trips it (#1701) — and it stops doing the one thing that
was wrong: it performs no teardown. See the five verdicts below.

---

## Layer 1 — the application layer drains first

| Work | Where it is tracked | Progress signal | Grace | Kill |
|---|---|---|---|---|
| **Activities** (`RunActivity`) | `ActivityTracker` — one `ActivityRunHandle` per run, labelled with its activity path | `handle.Progress()` on every `ctx.Log(...)` line the run appends | `ActivityStallBudget` (8 s) of **no progress** | the run's cancellation token — the same one a user's cancel request trips |
| **Pooled I/O leaves** (`IIoPool.Invoke/InvokeStream/InvokeBlocking`) | the pool's gate permits and blocking-idle event | a leaf completing (a permit released) | `IoPoolOptions.DrainGrace` (8 s) of **no completion** | `_poolCts` — the pool token linked into every leaf |
| **Handler turns** | the hub's action block (`MessageService`) | a turn completing (`TurnsCompleted`) or a `RunLevel` transition | `DisposalWatchdogTimeout` (8 s) of **no progress** | `CancelExecution()` — the token the handler was given |

The same number everywhere is deliberate: "no progress for 8 s" means the same thing at every
layer, and every grace is a **stall** bound, not a duration. A burst of ten short writes drains in
ten completions, a run that logs a line every second is waited for as long as the caller's outer
budget allows, a backlog of 800 accepted turns drains in 800 turns.

### Activities: `Quiesce`, not `WhenIdle`

`MeshTeardownExtensions.TeardownAsync` (production) and `MonolithMeshTestBase.DisposeAsync`
(tests) both start with `ActivityTracker.Quiesce(ActivityStallBudget)`:

```
idle?            → done at once, Clean
run progressing  → wait (the caller's timeout is the only outer bound)
run stalled      → RequestCancel() once   → listed in report.Cancelled  → Error
run ignores it   → after one more budget  → listed in report.Abandoned  → Error, and the quiesce
                                             no longer waits on it
```

`ActivityRunner` registers each run with `TrackRun(activityPath, () => cts.Cancel())` and calls
`Progress()` from the `ActivityContext.Log` seam, so a run that is writing its log is provably
alive. A run cancelled this way finishes `Cancelled` with the message *"Cancelled by teardown: the
run made no progress while the mesh was shutting down"* and the runner logs it at **Error** — it
is not the ordinary user-cancel outcome, it is a run that did not finish its job.

### Pooled I/O: grace, then cancel, then join

`IoPool.Drain()` used to cancel first and join second, so every in-flight write at teardown was
aborted the instant the mesh decided to go down. It now:

1. **Joins under the grace** — re-acquires the gate's permits one at a time, waiting up to one
   `DrainGrace` for *each*; every acquisition is a leaf that finished (or a free permit), and the
   clock restarts. Blocking leaves are waited for on their idle event under the same grace.
2. **Names what did not finish** — the permits it could not re-acquire are wedged leaves; their
   call sites are captured **now**, before the cancel erases them (`CancelledLeafSites`).
3. **Cancels** — which is also what ends the long-lived pooled *subscriptions* (change feeds hold
   no permit past their subscribe and have no job to finish; ending them is not a kill).
4. **Joins under the drain budget** as before, and reports the residual as before.

`IoPoolRegistry.DrainAll(out residual, out cancelledAfterGrace)` carries both lists; the teardown
report exposes them as `CancelledIoLeaves` / `CancelledIoByPool` and logs each at **Error**.

### Handler turns: accepted work drains ahead of the shutdown

`MessageHub.Dispose()` no longer calls `CancelExecution()` on entry. The `ShutdownRequest` that
starts the phases queues FIFO behind whatever the hub had already accepted, and the pump drains it.
A turn that returns on its own is never cancelled.

The one exception is a hub that never finished **starting**: its `InitializeHubRequest` is the
hub's own bring-up, not work anyone handed it — intake is gated behind it, so no accepted turn
can depend on its result — and a bring-up still running when the owner tears down produces
nothing the owner will keep. That turn is cancelled on entry, so a hung initialization releases
the block at once instead of holding its whole ancestry pending for a stall budget; whatever was
parked behind its gates is answered `ShuttingDown` and reported (`[DISPOSE-DISCARD]`).

---

## Layer 2 — the hub goes down behind its work: the five stall verdicts

`MessageHub.OnDisposalStall` runs every `DisposalWatchdogTimeout` (8 s) during which nothing in the
subtree changed `RunLevel`. It reads the pump's completed-turn counter and the executing turn, and
reaches exactly one of these:

| Verdict | Condition | Action | Level / event |
|---|---|---|---|
| **busy** | turns completed since the last look | none — keep waiting | Information `[DISPOSE-BUSY]` |
| **wedged turn, first strike** | a turn has held the block for the whole budget | `CancelExecution()` once | **Error** `[DISPOSE-WEDGE]` (7311) |
| **wedged turn, ignores cancellation** | same turn, another budget later | none — report again every budget | **Error** `DISPOSAL DEADLOCK DETECTED` (7312) |
| **ShutDown phase blocked** | the executing turn *is* the `ShutdownRequest` | none — it runs with `CancellationToken.None`; the finding is the registrant that blocks inside `DisposeImpl` / `messageService.Dispose` | **Error** (7314) |
| **stalled below** | no turn executing, no progress | none — the stall is in a child or a join; the diagnostics name it | **Error** (7313) |

Every Error carries the hub address, the message type and its age, the queue depth, the last
progress signal, the `RunLevel`, and the **recursive disposal snapshot** (every hosted hub's
`RunLevel`, queue depths, executing turn, pending callbacks). That is the reproduction: which hub,
which message, how long, what it was waiting on.

**The bound that ends a wedged teardown belongs to the caller**, and it reports rather than
forces: `MonolithMeshTestBase.DisposeTimeout` (30 s) fails the class with the snapshot;
`MeshTeardownHostedService.TeardownTimeout` (30 s) logs an **Error** with the snapshot and lets the
host proceed to `HostOptions.ShutdownTimeout` (90 s); Kubernetes ends the process at its grace
ceiling. At no point does a hub say it has finished when it has not.

### Quiescing waits for a reply a shutting-down sibling still owes

The Quiescing phase gives a hub's pending `Observe` callbacks `QuiesceTimeout` (2 s, 0.5 s in
the test base) to drain, then cancels them and records a leak. Measured with #3261's departed-child
detector, every callback that cancel hit in a whole-mesh teardown was a request to a **sibling hub
that was itself disposing** — `CreateOrUpdateNodeRequest@portal/nodeops-*` from a type hub,
`SubscribeRequest@…` and `PatchDataRequest@Plugins/_Policy` from a `cache/*` hub — and production
shows the identical `[QUIESCE-TIMEOUT] … CreateNodeRequest@portal/nodeops-*` at 2 s. Those replies
were on their way: a disposing hub answers every delivery it accepted before it leaves its owner's
registry (served ahead of its own `ShutdownRequest`, or NACKed `ShuttingDown` by its
`messageService.Dispose()`). Cancelling them discarded accepted work and reported a leak that was
not one.

So on expiry the phase now asks, per pending callback, whether its target resolves — at the mesh
root, the way `HierarchicalRouting` resolves it — to a hub that `IsShuttingDown` and has not
signalled `Dead`. If any does, the budget is **re-armed** (`[QUIESCE-WAIT]`, Information) instead
of cancelled; the wait ends by construction when the reply lands or the sibling has gone. A cycle
breaker (`MaxQuiesceRearms`, 20) covers two hubs each holding a deferred request of the other's:
past it the callbacks are cancelled as before and the case is logged at **Error** (`[QUIESCE-CUT]`,
7315). The stall detector treats a Quiescing hub with owed replies as *busy*.

### What a hub still discards, and why that is an Error too

Two things a disposing hub cannot carry across: deliveries **deferred** behind an initialization
gate that never opened, and turns still **queued** when `messageService.Dispose()` stops the pump.
Both were accepted; neither ran. The first is answered `ShuttingDown` (transient, so the sender
retries against the fresh activation — the #2176 hang was the silent version); both are logged at
**Error** (`[DISPOSE-DISCARD]`, events 7301/7302) with the message type, id, sender, the gates it
sat behind and the run level. A hub that disposes with its gates still shut is the defect those
lines point at.

---

## Errors become issues — in production

Every kill and every discard above is an **Error**, and every Error carries an `EventId`. The
red-log pipeline ([Log watch and triage](/Doc/Architecture/LogWatchTriage)) keys an incident on
*category + event id + exception + top frame* and never on prose, so each verdict shape files as
**one** issue however many hubs reach it, and the prose is free to carry every address, message
type and snapshot a reproduction needs. Triage checks for an existing issue before it files.

That pipeline runs only against production Loki. In a test run the same lines land in the test
output and in the per-test trace (`DISPOSE_WEDGED_WORK`, `DISPOSE_ACTIVITIES_QUIESCED`), and the
test base **does not fail the class** on killed work — the dirty-teardown and quiesce-leak gates
keep their existing verdicts, and a killed run is logged where the test author will see it.

---

## What production measured (Loki, memex / memex-cloud, 2026-08-28 → 09-04)

The maintainer's report was *"blocked rolls — disposal seems to be keeping something"*. Two
different things were keeping pods, at two different scales:

- **28 minutes, pod level — not disposal.** `terminationGracePeriodSeconds` is 1800 s and the
  `preStop` hook polls `/drain` until no Blazor circuit is open. In every captured case one real
  signed-in user's tab stayed pinned to the terminating pod, the count sat flat (`1→1`), and preStop
  gave up at 1680 s (`Drain: GIVING UP after 00:28:02 — 1 circuit(s) are STILL OPEN`). Fifteen
  such pods in the window. A Terminating pod keeps its 4 CPU / 8 GiB reservation, the HPA fills
  the remaining slots, and `maxSurge=1` then has no slot for the surge pod — measured on 09-03
  17:14, 6 m 39 s `Pending` until a manual kill freed one. That is a scheduling question
  ([Deployment on AKS](/Doc/Architecture/DeploymentAKS)), not a teardown one.
- **55–61 seconds, process level — disposal, bounded.** After SIGTERM the slow shutdowns were
  `RoutingQuiescence` spending its 30 s on route legs that never landed (3 to 17 of them),
  `IoPoolSiloTeardown` spending its 30 s on a leaf ignoring cancellation, and the sync-stream hubs
  and their node hubs being force-torn-down after 8–23 s. Those are the defects this page's
  verdicts now name individually — and the reason the force was removed: a hub that reported
  itself disposed at 8 s while its work ran on is exactly "keeping something".

One more finding for the operator: `MeshWeaver` logs at `Warning` in the production
`appsettings.json`, so the Information-level teardown narrative (`Drain: TERMINATION BEGUN`,
`MeshTeardownHostedService … drained cleanly`, `IoPoolSiloTeardown: pooled I/O joined`) is
invisible in Loki. Every verdict on this page is therefore an **Error** — not to be loud, but so
that it exists in the one log level that ships.

---

## Rules for adding teardown work

- **A unit of work reports its own progress.** Never invent a heartbeat for it; if it has no
  observable step, it has no claim to be waited for beyond one budget.
- **Cancel is a verdict, never a reflex.** Nothing on the teardown path cancels on entry.
- **Never tear down around a running turn.** If a hub cannot finish, it stays pending and says so.
- **Kills and discards are Errors with event ids and the snapshot attached.** A Warning does not
  ship, and a line without the address, the message type and the age cannot be reproduced.
- **The outer bound reports; it does not force.** Tests fail with the snapshot; the host logs it
  and exits on its own timeout.
