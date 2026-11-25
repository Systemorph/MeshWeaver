---
NodeType: Markdown
Name: "Reading a Disposal Stall Verdict"
Abstract: "What every field of a hub's disposal snapshot actually measures — and the three that were read as evidence while measuring nothing: exec was a hard-coded literal 0, deliveryActionCompleted printed the negation of its own name, and 'last progress -> Started' is the watchdog's own BehaviorSubject echo. The verdict taxonomy, the hole that sent 47 reports to children that were not the problem (#3593), and the two counters that now discriminate a pump that never ran from one blocked before its handler."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#00695c'/><path d='M12 6v6l4 2' fill='none' stroke='white' stroke-width='1.8' stroke-linecap='round' stroke-linejoin='round'/><circle cx='12' cy='12' r='7.2' fill='none' stroke='white' stroke-width='1.6'/></svg>"
Thumbnail: "images/DataMesh.svg"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Disposal"
  - "Diagnostics"
  - "Operations"
---

# Reading a Disposal Stall Verdict

> **The rule this page exists to enforce: a verdict may only assert what its snapshot measured.**
> `MessageHub.OnDisposalStall` publishes an Error line that production triage files as an issue and
> that a human then acts on. Every clause in it is read as a measurement — so a field that is a
> constant, a field whose printed name is the negation of its datum, and a clause that reports the
> instrument's own echo are not cosmetic problems. They send readers to the wrong place, and in
> [#3593](https://github.com/Systemorph/MeshWeaver/issues/3593) they did it 47 times in one pod
> shutdown.

For the shutdown state machine itself see [Hub Disposal Model](/Doc/Architecture/HubDisposalModel);
for the policy the phases implement — accepted work finishes, wedged work is reported, nothing is
forced — see [Teardown Layers](/Doc/Architecture/TeardownLayers). This page is about **reading the
report**.

---

## The report that started this

On a public-instance pod, 47 `sync/*` hubs logged event **7313** at the same moment:

```text
DISPOSAL DEADLOCK DETECTED: Hub sync/XXXX made no teardown progress for 00:00:08
(last progress: sync/XXXX -> Started). RunLevel=Started, queue depth 1. No turn is executing on
this hub, so the stall is in a hosted hub or a join it is waiting on — the diagnostics below name
it. Disposal is NOT forced.
Hub sync/XXXX RunLevel=Started Disposal=Pending Queue(buffer=1,deferred=0,exec=0,openGates=0,
deliveryActionCompleted=False)
```

Read literally, that line says: nothing is executing, no drain is running, the delivery action has
*not* completed, and the hub last progressed *to* `Started`. Three of those four are artefacts of
the instrument.

### Misreading 1 — `exec=0` measured nothing

`MessageService.GetQueueSnapshot` returned a **hard-coded literal `0`** in that position, a
leftover from the TPL-Dataflow pump that had an `executionBlock` whose count it once named. The turn
loop that replaced it has no such block, and nothing updated the tuple. So `exec=0` was true of
every snapshot ever taken, on every hub, in every state — a verification step that cannot fail.

It is now **`drainsInFlight`**, an `Interlocked` count of `DrainOne` bodies actually executing:
`0` while the pump is idle *or* while a scheduled drain has not started, `1` while a turn is being
pumped, transiently `2` when an async turn's terminal schedules the next drain before the previous
body unwinds.

### Misreading 2 — `deliveryActionCompleted=False` meant a drain WAS in flight

The field was `!draining`, printed under a name that reads like a completion flag. So
`deliveryActionCompleted=False` did not mean "the pump has not finished" in any useful sense — it
meant **`draining == true`**: a drain had been scheduled and the loop had not yet found the queue
empty. This is the single most load-bearing field in the report, and its name asserted the opposite
of its datum. It is now printed as **`draining`**, with the value unnegated.

### Misreading 3 — `last progress: … -> Started` is the watchdog's own echo

`runLevelChanged` is a `BehaviorSubject`. The stall detector subscribes to `DisposalProgress`
*inside* `Dispose()`, so the subject immediately replays the **current** level as the first
"progress" signal — an observing-from-here mark, not a transition that happened after `Dispose()`.
A hub that never moved at all still reports `-> Started`.

---

## What the snapshot actually measures

`Queue(buffer=…,deferred=…,drainsInFlight=…,openGates=…,draining=…,drainsAwaitingScheduler=…)` plus `Executing(type, ms)`:

| Field | Source | Means | Does **not** mean |
|---|---|---|---|
| `buffer` | `mainQueue.Count` | turns waiting to be pumped | anything about who put them there |
| `deferred` | `deferredQueue.Count` | turns parked behind a closed initialization gate | that they will ever run |
| `drainsInFlight` | live `Interlocked` count | `DrainOne` bodies executing right now | that one is making progress |
| `openGates` | `gates.Count` | initialization gates still **closed** (the name is historical) | that traffic is blocked — system messages and awaited replies pass |
| `draining` | the `draining` flag | a drain is scheduled *or* running; the loop has not yet found the queue empty | that a thread is executing it — pair it with `drainsInFlight` |
| `drainsAwaitingScheduler` | `drainsScheduled - drainsStarted` | drains the turn scheduler ACCEPTED and has not started — on a hosted hub, work sitting on the pool's global queue | that the hub is at fault — a value > 0 hands the stall to the scheduler (M1, below) |
| `Executing(T, ms)` | `currentlyExecutingMessageType` | a **handler** is on the block, and for how long | that no turn is in flight — it is set by `RunHandler`, so a turn still in `NotifyAsync`'s pre-handler stages reads as absent |

And two counters the verdicts read but do not print raw:

| Counter | Incremented | Blind to |
|---|---|---|
| `TurnsCompleted` | `RunHandler`'s `.Finally` | every turn that never reached a handler — including one never dequeued |
| `TurnsDequeued` | `DrainLoop`, at the moment the turn leaves `mainQueue` | nothing on the dequeue path; this is the counter that separates "never started" from "wedged in a handler" |

🚨 **`TurnsCompleted` and `TurnsDequeued` are not the same measurement wearing two names.** A pump
whose scheduled drain never runs leaves *both* frozen — which is exactly why the completed-turn
counter alone could not tell that case apart from a hub wedged inside a handler, and why the stall
fell into the wrong bucket.

---

## What #3593 established, and what it did not

**Established**, after the three misreadings are removed:

- `RunLevel=Started`, `Disposal=Pending`, `buffer=1`, `deferred=0`, `openGates=0`.
- `draining == true` — a drain **was** latched.
- `CurrentMessage == null` — the 7313 branch requires it, and `RunHandler` sets
  `currentlyExecutingMessageType` as its first side effect, so an in-flight *handler* always has it
  set. Every pre-handler stage of `NotifyAsync` is synchronous.
- `TurnsCompleted` unchanged across the whole 8 s window.

⇒ **No turn was in flight while `draining` was latched true**, and the one queued item is the
`ShutdownRequest` that `Dispose()` posted. The hub was not waiting on a child: at `RunLevel=Started`
it has not reached `DisposeHostedHubs` and has asked nothing below it to do anything.

**Not established at the time** — the mechanism. Three candidates were carried for eight days
(the third was added later; see below), because the instrumentation of the day could not
discriminate any of them. 🚨 **It is established now: the answer is M1, and the reading that settles
it was taken in-process rather than waited for in production — see *"Resolved 2026-09-15 (#3593) —
M1, measured"* at the end of this section.** The table below is kept as the discriminator it was,
and because the counters it describes are what produced the answer.

| | **M1 — the wake-up was never delivered** | **M2 — a drain body is blocked before the handler** |
|---|---|---|
| Mechanism (**this is the one it was**) | `ScheduleDrainOne` used `Task.Factory.StartNew(…, turnScheduler)` **without** `TaskCreationOptions.PreferFairness`. From a thread-pool thread that enqueues onto that thread's **local LIFO** work-stealing queue. `HostedHubsCollection.DisposeHubsReactive` disposes every child sequentially on one thread, so N children's drains land in one thread's local queue, reachable only by stealing. | A `DrainOne` **is** running and is blocked synchronously ahead of the handler — the `GetHub` convoy `HostedHubsCollection` documents from two `dotnet-stack` captures: `Monitor.Enter_Slowpath ← GetHub ← RouteStreamMessage ← DrainOne`. |
| Reproduces all measured fields on N children at once? | yes | yes |
| `drainsInFlight` | **0** | **≥ 1** |
| `TurnsDequeued` | **unchanged** — the body never ran | **advanced** — the body dequeued the turn it is now stuck in |

That table *is* the discriminator, and it is why the counters were added: neither reading existed
when the 47 reports were filed.

### M3 — the latch leaked, and nothing was ever scheduled

🚨 **`drainsInFlight == 0` does not mean a drain is queued.** It means no drain body is *running*,
and the verdict's own prose used to close that gap by assertion — *"the drain flag is latched (a
drain **is** scheduled on this hub's TaskScheduler)"* — which nothing measured. Two states read
identically:

| | **M1** — the scheduler holds it | **M3** — nothing is outstanding |
|---|---|---|
| `drainsInFlight` | 0 | 0 |
| `drainsAwaitingScheduler` (`drainsScheduled - drainsStarted`) | **> 0** | **0** |
| Owner | the `TaskScheduler` — it accepted work and is not running it | **this file** — `MessageService` |

M3 was **reachable**. `draining = true` is set inside the turn gate in `KickDrain` and the schedule
happens *outside* it; `Terminal()` re-schedules with the latch still held. A throw from
`Task.Factory.StartNew` at either site left the flag set with nothing outstanding — and since
`KickDrain` returns immediately whenever the flag is set, the pump was then frozen **for the life of
the hub**, silently, with the verdict blaming a scheduler that had never been asked. A real
scheduler throws here: a completed `ConcurrentExclusiveSchedulerPair`, or an Orleans activation torn
down underneath the hub.

`ScheduleDrainOne` now releases the latch and logs an Error naming the scheduler when a schedule
fails, so the invariant *"`draining` ⇒ a drain is running or queued"* holds by construction and
every verdict that reads it is entitled to.

🚨 **M3 is almost certainly NOT what the 47 reports were**, and saying so is the point of listing
it. `Task.Factory.StartNew` on `TaskScheduler.Default` does not throw, and a hosted hub gets a
**fresh** `MessageHubConfiguration` (`IServiceProvider.CreateMessageHub`) — nothing copies a parent's
`TaskScheduler`, and `WithTaskScheduler`'s own contract says to use the grain's scheduler *only* for
the root grain hub. So `sync/*` hubs run on the default pool scheduler, where M3 is unreachable and
a dead activation scheduler cannot reach them either. M3 is a real defect with the identical
fingerprint, found by reading the code and closed; it narrows the candidate set for the incident
rather than explaining it. **What remains for the reported population is M1-by-pool-starvation or
M2**, and the next occurrence prints which.

🚨 **It does not fall back to another scheduler.** The turn scheduler is the hub's serialisation
guarantee — under Orleans it *is* the grain's activation scheduler — so running a turn elsewhere
would break the actor model to keep a queue moving. Releasing the latch is the honest recovery: the
next post tries again and reports again, instead of the pump going dark.

### A parent's snapshot names its children's M1 (#5820)

A parent parked at `DisposeHostedHubs` (7313) appends the recursive snapshot of every hosted hub
and says the stall is *"in a hosted hub or a join it is waiting on — the diagnostics below name
it"*. Until #5820 those child lines did **not** print `drainsAwaitingScheduler`, so a child reading
`buffer=2,drainsInFlight=0,draining=True` could not be told apart from M3 — and #5820's child, which
was also still `Starting` (`openGates=1`), was investigated as a closed initialization gate. A
private control then showed a closed gate alone does not stall a parent: lifecycle frames bypass it.
The same replica's heartbeat three seconds earlier read **113,021 pending pool work items**, the
M1-by-starvation shape of #5799 — so the child line now carries the counter, and a parent verdict
over a starved child reads `drainsAwaitingScheduler=1` directly. Pinned by
`DisposalStallNamesThePumpTest.AParentsSnapshot_NamesAChildDrainTheSchedulerHasNotStarted`.

That makes the verdict honest; it does not end the stall. The owner of M1-by-starvation is whatever
floods the pool, and in #5799/#5820 the `[HEAPSTEP]` lines of the same minute name per-hub
construction metadata as the heaviest allocation (`Entry[System.String][]`,
`Func<JsonDerivedType,bool>`, the resolve-pipeline handlers of the DI container then in use — Autofac, since removed in MeshWeaver#7145) — the per-hub metadata
multiplication tracked on #5555.

A second owner was named later from a `[POOLSTALL]` capture taken 6 s after a burst of 47 of these
verdicts: 14 of 41 pool workers were parked in Rx's process-wide `SystemClock` lock, which `StartWith`
and `Merge` take on every subscription, inside hub turns. Such a worker runs no other hub's drain. The
hub's own teardown, every data write's merge in the owner's `DataChangeRequest` turn, and the read
admission every followed stream runs per revision no longer take it, and
`SyncHubTeardownTakesNoProcessWideRxLockTest` holds the lock over a hub with live `sync/*` hubs to keep
it that way — see [Rx StartWith and Merge Take a Process-Wide Lock](../RxStartWithProcessWideLock).

### What Orleans makes of M1

`MessageHubGrain` wires the **root grain hub's** turn scheduler to the grain's
**`ActivationTaskScheduler`** (`.WithTaskScheduler(grainScheduler)`), and the same file already
records the consequence one hazard over: *"when a stuck round wedges that scheduler, any rescue that
is itself a hub message can never be processed"* (#147). For a hub on that scheduler, M1 has a
specific shape — a wedged or already-deactivated activation parks every turn it will ever take — and
the verdict now hands the stall to that scheduler by measurement rather than by assertion.

🚨 It does **not** apply to hosted hubs, which is what the 47 reports were: hosted hubs are built
from a fresh configuration and stay on `TaskScheduler.Default`. Since the fix below, a
`drainsAwaitingScheduler > 0` on a `sync/*` hub means the pool genuinely has no thread to give —
the drain is on the global queue, so it is not hiding on one — not a dead activation. The owner is
the same (the scheduler), the remedy is not.

### Resolved 2026-09-15 (#3593) — M1, measured: the drain rode the POSTING thread's own queue

For eight days this page said *"do not fix this by adding `PreferFairness` — it is one of the live
hypotheses, and making a symptom rarer while the mechanism is unmeasured is the band-aid this
repository refuses."* That was the right call then and it is discharged now, because the mechanism
was **measured** rather than guessed, twice — once against the runtime, once against the hub.

**The runtime reading.** `Task.Factory.StartNew(…, TaskScheduler.Default)` issued *from a thread-pool
worker* enqueues onto **that worker's local LIFO queue**. Measured on an 18-core host, with every
worker occupied by a non-blocking loop and one such task queued from each:

| what was queued | drains started after 20 s | `ThreadPool.ThreadCount` | `PendingWorkItemCount` |
|---|---|---|---|
| `DenyChildAttach` (local queue) | **0 / 18** | 18 → 18 (never grew) | 18, flat for the whole 20 s |
| `DenyChildAttach \| PreferFairness` (global queue) | 18 / 18 by ~4.7 s | 18 → **19** (a thread was injected) | 0 |

So the stall is not "slow under load": work on a busy worker's local queue is reachable by **neither**
work-stealing (no worker is idle to steal) **nor** the pool's starvation detection, which does not
look there and therefore never injects a thread. It waits for the worker that enqueued it to return
to the dispatcher — and for the hub pump that worker is running, "return to the dispatcher" means
"that *other* hub's queue emptied". **Unbounded, and a cross-hub coupling the actor model forbids.**

**Why the poster is always somebody else's thread.** `KickDrain` runs inline on whoever posted. A
mass teardown posts every child's `ShutdownRequest` from ONE worker — `HostedHubsCollection
.DisposeHubsReactive` calls `h.Dispose()` sequentially inside the owner's own turn — so N children's
drains pile onto that one worker's local queue. That is the population shape of the incident exactly:
47 `sync/*` siblings, one cascade, 41 ms apart.

**The hub reading**, from `PumpDrainReachabilityTest` — a hub whose drain is scheduled from another
hub's turn while that turn is parked. Unfixed, the verdict prints the incident's own fingerprint:

```
DISPOSAL DEADLOCK DETECTED: Hub victim/… made no teardown progress for 00:00:08
(last progress: victim/… → Started). RunLevel=Started, queue depth 1.
THE PUMP IS NOT TURNING: the drain flag is latched, drainsInFlight=0,
drainsAwaitingScheduler=1, and NO turn was dequeued in that window (0 dequeued in total…)
Hub victim/… RunLevel=Started Disposal=Pending Queue(buffer=1,deferred=0,drainsInFlight=0,openGates=0,draining=True)
```

Read against the discriminator table above — `drainsInFlight=0`, `TurnsDequeued` delta `0`,
`drainsAwaitingScheduler=1` — that is **M1**, and it is every field of the 47 reports plus the two
that did not exist when they were filed.

**The fix**, one flag in `MessageService.ScheduleDrainOne`:

```csharp
Task.Factory.StartNew(DrainOne, CancellationToken.None,
    TaskCreationOptions.DenyChildAttach | TaskCreationOptions.PreferFairness,
    turnScheduler);
```

The drain goes to the **global** queue: visible to every worker, and visible to the pool's own
starvation detection, so a saturated pool grows to service it. What is left is the pool's bounded,
self-healing back-pressure instead of a wait on an unrelated hub. Nothing else changed — no
`PreferFairness` anywhere else, no retry, no re-kick, no widened bound, no watchdog: **M2 and M3 are
untouched**, and the verdict still names them if they ever fire.

🚨 **The negative control ran.** With only the flag removed and the test project rebuilt, the test
fails in 12 s with the verdict above; with it, green in 406 ms. One line is the whole difference.

### Resolved 2026-11-24 (#5799): teardown no longer rides the pool

`drainsAwaitingScheduler>0` on a hosted hub meant the pool had accepted the drain and not run it. Seven
changes (#5931 the snapshot field, #5971 parked registry threads, #7314 allocation drivers, #7877 the
update-backlog writer, #8137 idle-worker count, #8140 and #8385 the `SystemClock` lock) each removed one
thing that held or flooded the pool, and the verdict kept firing (1,224 occurrences, 1,106 on memex-cloud),
because the drain was still a pool work item: it waits behind everything queued before it (the 2026-11-24
heartbeat read `poolThreads=34 poolPending=2865`), whatever put that backlog there.

From the moment `Dispose()` begins, a hub on `TaskScheduler.Default` now drains on the mesh's
`DisposalDrainLane` (dedicated threads, one lane per mesh registered by `AddIoPools`, a 1,024-thread safety ceiling,
kept parked, built on the sanctioned `LimitedConcurrencyLevelTaskScheduler`). A drain already queued on the pool when disposal began is superseded: a new drain is
scheduled on the lane and the pool item becomes a no-op (generation check in `MessageService.DrainOne`).
Orleans grain hubs keep their activation scheduler. Pinned by `DisposalDrainLaneTest`. So a
`THE PUMP IS NOT TURNING` verdict on a hosted hub after this change is no longer "the pool is busy"; read
it as a lane or latch defect.

**Recurrence 2026-11-25 (4 verdicts after the roll, 3 `sync/*` hubs, `RunLevel=Started`, `drainsAwaitingScheduler=1`,
0 dequeued).** The stalled drain was queued and never started during a mass teardown (a `Feedback/Inbox` recycle
loop plus dozens of `Ops/Issue/*` hubs; one dispose action faulted on a 30 s Npgsql timeout at 07:59:36). What is
established: the pump verdict did not name its scheduler, and the lane's 16-thread cap is reachable by any
teardown turn that blocks. What is NOT established: which turn blocked. The dispose action that timed out
(`MeshDataSource.FlushPendingOwnSave`) writes through the storage adapter's write lane (an `IIoPool` leaf), so it
does not hold a lane thread. Therefore: the cap is 64 (a burst must not start hundreds of threads); a drain queued
while all lane threads run drains logs `[DISPOSAL-LANE] saturated` naming the oldest holders and the message each
is executing; and the pump verdict names the scheduler (`on disposal-lane(...)` / `(pool, no disposal lane
resolved)`). The next recurrence will name the blocking turn. Pinned by `DisposalDrainLaneTest`
(`ADisposeActionDoingSlowIo_DoesNotHoldALaneThread`, `ASaturatedLane_WarnsNamingTheBlockingTurns`).
