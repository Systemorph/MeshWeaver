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

On a `memex-cloud` pod, 47 `sync/*` hubs logged event **7313** at the same moment:

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

`Queue(buffer=…,deferred=…,drainsInFlight=…,openGates=…,draining=…)` plus `Executing(type, ms)`:

| Field | Source | Means | Does **not** mean |
|---|---|---|---|
| `buffer` | `mainQueue.Count` | turns waiting to be pumped | anything about who put them there |
| `deferred` | `deferredQueue.Count` | turns parked behind a closed initialization gate | that they will ever run |
| `drainsInFlight` | live `Interlocked` count | `DrainOne` bodies executing right now | that one is making progress |
| `openGates` | `gates.Count` | initialization gates still **closed** (the name is historical) | that traffic is blocked — system messages and awaited replies pass |
| `draining` | the `draining` flag | a drain is scheduled *or* running; the loop has not yet found the queue empty | that a thread is executing it — pair it with `drainsInFlight` |
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

**Not established** — the mechanism. Three candidates remain (the third was added later; see below),
and the instrumentation of the day could not discriminate any of them:

| | **M1 — the wake-up was never delivered** | **M2 — a drain body is blocked before the handler** |
|---|---|---|
| Mechanism | `ScheduleDrainOne` uses `Task.Factory.StartNew(…, turnScheduler)` **without** `TaskCreationOptions.PreferFairness`. From a thread-pool thread that enqueues onto that thread's **local LIFO** work-stealing queue. `HostedHubsCollection.DisposeHubsReactive` disposes every child sequentially on one thread, so N children's drains land in one thread's local queue, reachable only by stealing. | A `DrainOne` **is** running and is blocked synchronously ahead of the handler — the `GetHub` convoy `HostedHubsCollection` documents from two `dotnet-stack` captures: `Monitor.Enter_Slowpath ← GetHub ← RouteStreamMessage ← DrainOne`. |
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

### What Orleans makes of M1

`MessageHubGrain` wires the **root grain hub's** turn scheduler to the grain's
**`ActivationTaskScheduler`** (`.WithTaskScheduler(grainScheduler)`), and the same file already
records the consequence one hazard over: *"when a stuck round wedges that scheduler, any rescue that
is itself a hub message can never be processed"* (#147). For a hub on that scheduler, M1 has a
specific shape — a wedged or already-deactivated activation parks every turn it will ever take — and
the verdict now hands the stall to that scheduler by measurement rather than by assertion.

🚨 It does **not** apply to hosted hubs, which is what the 47 reports were: hosted hubs are built
from a fresh configuration and stay on `TaskScheduler.Default`. Read a
`drainsAwaitingScheduler > 0` on a `sync/*` hub as **pool starvation or a local-queue LIFO stall**,
not as a dead activation — the owner is the same (the scheduler), the remedy is not.

🚨 **Do not "fix" this by adding `PreferFairness`.** It is one of the live hypotheses, and shipping
a change that makes a symptom rarer while the mechanism is unmeasured is the band-aid this
repository refuses. The next occurrence now names which of M1, M2 or M3 it is; that is what the fix
waits on.

---

## The verdict taxonomy, and the hole

`OnDisposalStall` runs once per stall budget (8 s of no `RunLevel` movement anywhere in the subtree)
and reaches exactly one verdict:

| Verdict | Condition | Event |
|---|---|---|
| **busy** | turns completed since the last look | Information `[DISPOSE-BUSY]` |
| **busy — young turn** | the turn on the block is younger than the budget | Information `[DISPOSE-BUSY]` |
| **quiescing, reply owed** | a pending callback is owed by a shutting-down local hub | Information `[DISPOSE-BUSY]` |
| **wedged turn, first strike** | a turn held the block for a whole budget | **Error** `[DISPOSE-WEDGE]` (7311) |
| **wedged turn, ignores cancellation** | same turn, a budget later | **Error** (7312) |
| **ShutDown phase blocked** | the turn on the block *is* the `ShutdownRequest` | **Error** (7314) |
| **the pump is not turning** | queue non-empty, `draining` latched, nothing dequeued for a whole budget, nothing on the block | **Error** (7316) |
| **stalled below** | none of the above, **and there is something below** | **Error** (7313) |
| **unclassified** | none of the above | **Error** (7317) |

The last three are the repair.

**7316 is the verdict the hole needed.** "Queue non-empty, drain latched, nothing dequeued" had no
bucket, so it fell through to the catch-all. It now names the pump, prints `drainsInFlight` and the
dequeue delta, and says in the line itself which reading means what:

```text
THE PUMP IS NOT TURNING: the drain flag is latched, drainsInFlight=0,
drainsAwaitingScheduler=1, and NO turn was dequeued in that window (0 dequeued in total since
Dispose()). … MECHANISM: the turn scheduler ACCEPTED 1 drain(s) and has not run them
(drainsInFlight=0). The stall is in THAT SCHEDULER, not in this hub: under Orleans the hub's turn
scheduler IS the grain's ActivationTaskScheduler (MessageHubGrain.WithTaskScheduler), so a wedged
or already-deactivated activation parks every turn this hub will ever take.
```

The `MECHANISM:` clause is chosen from the two counters, never asserted: `drainsInFlight > 0` names
M2, `drainsAwaitingScheduler > 0` names M1 and hands the stall to the scheduler, and zero on both
names M3 — a defect in `MessageService.ScheduleDrainOne`, which that method now makes unreachable.

**7313 is now guarded.** "The stall is in a hosted hub or a join it is waiting on" is printable only
when there *is* something below — hosted hubs still in the collection, an outstanding hosted-hub
join, or a teardown that has reached `DisposeHostedHubs`. As an unguarded fallback it asserted a
cause the snapshot could not support.

**7317 says it does not know.** A verdict that picks the nearest bucket teaches every subsequent
reader to trust a claim it never earned; an explicit unknown that prints the measurement is worth
more than a confident wrong one. Its usual real cause is a pending callback owed from outside this
mesh, which the attached recursive snapshot lists.

---

## The order to read a 7313 / 7316 in

1. **`draining` and `drainsInFlight` together.** `draining=True, drainsInFlight=0` is a scheduled
   drain that has not run — a scheduler question, not a hub question. `draining=True,
   drainsInFlight>0` with no `Executing(…)` is a drain body blocked ahead of its handler.
2. **The dequeue count in the line.** Zero since `Dispose()` means nothing this hub was asked to do
   has begun; a non-zero count with a frozen `RunLevel` means work is moving and something specific
   is not.
3. **`Executing(T, ms)`.** Present ⇒ read 7311/7312/7314 instead; the turn is named.
4. **The recursive snapshot below the line.** It carries every hosted hub's `RunLevel`, queue
   depths, executing turn and pending callbacks — that is the reproduction.
5. **`last progress`** — last, and only for the address. Its *level* may be the `BehaviorSubject`
   echo described above.

---

## What this page does not claim

The wedge in #3593 is **not fixed**. What changed is that the next occurrence is nameable: the
verdict no longer sends readers to children that were never asked to dispose, and `drainsInFlight`
plus the dequeue counter decide M1 versus M2 from the log alone. The regression test
(`DisposalStallNamesThePumpTest`) builds the state directly — a hub whose `TaskScheduler` stops
delivering threads after bring-up — and asserts on the emitted verdict, deliberately **not** on
disposal completing.

Related: [Debugging Disposal, Storms and Leaks](/Doc/Architecture/DebuggingDisposalAndLeaks) ·
[Debugging Message Flow](/Doc/Architecture/DebuggingMessageFlow) ·
[Asynchronous Calls](/Doc/Architecture/AsynchronousCalls).
