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

## Who asked for the teardown, and why

`[QUIESCE-START]` names the requester **and the reason** (#3510):

```
[QUIESCE-START] Hosting: requested by portal/nodeops-1 (routed DisposeRequest); why: PackageInstaller.SettleRetypedRoot: recycling the root 'Hosting' WHILE INSTALLING that package …; 4 pending callbacks …
[QUIESCE-START] Hosting: requested by itself — a self-posted DisposeRequest (Hosting), i.e. a rebind or self-heal recycle; why: NodeType rebind: node 'Hosting' is now typed 'Store/Plugin' but its hub activated on '(none)'; …
[QUIESCE-START] Hosting: requested by a direct Dispose() (no routed DisposeRequest); why: reason not stated by the caller; …
[QUIESCE-START] Hosting/Admin/_Activity/compile-state: requested by a cascade from its owner Hosting; why: the owner's own teardown — Hosting was torn down by portal/nodeops-1 (routed DisposeRequest); why: PackageInstaller.SettleRetypedRoot: recycling the root 'Hosting' WHILE INSTALLING that package …
```

**Why it was missing, and why the obvious source could not supply it.** `Dispose()` posts its
`ShutdownRequest` to ITSELF, so that message's `Sender` is always the dying hub — the field that
looks like it answers "who asked" is the one field that cannot. The discriminating fact is one frame
earlier: whether a `DisposeRequest` arrived over the bus at all, and from where. That is recorded in
`HandleDispose`, where such a request is *honoured* (not merely received — the root-mesh refusal
returns without disposing).

**The four readings, and what each rules out:**

| line says | means | rules out |
|---|---|---|
| a named sender | another hub asked — e.g. `PackageInstaller` posting to a package root | a recycle; a host teardown |
| `itself — a self-posted DisposeRequest` | an automatic recycle: `NodeTypeRebindWatcher`, the stale-build convergence, `WithOverlaySelfHeal` | an external actor |
| `a cascade from its owner <address>` | this hub went down because its OWNER did; the `why:` carries the owner's own attribution | this hub having been asked at all |
| `a direct Dispose() (no routed DisposeRequest)` | host teardown or a `using` | **the whole message path** |

### The `why:` field, and why WHO alone was not enough

Three of core's recyclers post to their **own** hub — `NodeTypeRebindWatcher`, the stale-build
convergence in `NodeTypeEnrichmentHelpers`, and `WithOverlaySelfHeal` — so the self-posted reading is
**one word covering three states** ([Controls That Cannot Fail](/Doc/Architecture/ControlsThatCannotFail)).
#3510's leading hypothesis was exactly *"which of those was it?"*, and the sender could never answer
it. `DisposeRequest.Reason` closes that: the poster always knew, and now the request carries it.

🚨 **An omission is REPORTED, never blank.** A `DisposeRequest` posted without a reason prints
`why: reason not stated by the caller` (`DisposeRequest.ReasonNotStated`) — the same rule as
`NodeDiagnosticsOutcome`: a field that simply disappears reads to the next person as *"there was
nothing to report"*. One core poster is still unnamed at the time of writing —
`MeshOperations.cs`'s recycle — and it is that token that says so.

### The cascade reading is the one #3510 actually needed

A hosted hub is torn down by `HostedHubsCollection.DisposeHubsReactive` calling `Dispose()` on it —
a **direct** dispose, so before this every cascaded child printed the fourth line above: the same
sentence a `using` produces. That is #3510's trail one level down. Its stranded writes were owed by
those children (`Hosting/*/_Activity/compile-state`, `Hosting/_Access/Public_Access`), so the child's
line is where a reader lands — and it attributed the teardown to nobody. The root recycle that took
it was invisible from there.

The child now names the cascade, its owner, **and the originating teardown**, which is propagated
unchanged down the tree (so the string cannot grow with depth and a leaf still names the event that
started it). One line, no second log to correlate against.

🚨 The third is not an absence of information — it is the deduction #3510 could not make. That issue
turned on one unknown: the Hosting root was disposed at 23:37:51Z *while its own 145-file install was
in flight*, its per-node children went with it, the writes they owed acks for were stranded, four
creates never got a reply and the install ran out a ten-minute bound. Its own words: *"Who disposed
the root is not in the log at this level"* — so the leading hypothesis (a NodeType rebind posting
`DisposeRequest` to the root) stayed a hypothesis, and its Expected section asks for this line.

🚨 The **self-posted** case is called out rather than printed as an address on purpose: the automatic
recycles post to their own hub, so a bare sender renders `Hosting: requested by Hosting` — true,
useless, and easy to misread as a routing oddity. That case is #3510's leading hypothesis, so it is
the one reading that must not be ambiguous.

**This names the disposer; it does not stop the wedge.** #3510's other two Expectations — deferring a
recycle while an install holds the root, or NACKing every in-flight write under it so the install
fails in milliseconds with a name — are untouched here. `QuiesceStartNamesTheAskerTest` pins all four
readings plus both halves of the `why:` field, and the line is `Information`: a test asserting it must raise its own filter
(`AddFilter("MeshWeaver", LogLevel.Information)`), because `TestBase` binds levels from
`test/appsettings.json` and an Information line is otherwise dropped before any provider sees it.

## Sighting 2026-09-10 — a verdict that was MINTED and still did not reach the waiter

Recorded here because the evidence is a CI log that ages out, and because it is the *other* half of
#3510's family: a caller owed a reply from an owner that has gone away.

**Measured.** `MeshWeaver.Graph.Test.NackReachesTheWaiterDuringTeardownTest.OwnerDisposingUnderMeshTeardown_StillAnswersTheWaitingCaller`
failed on shard 4 of run
[34513634943](https://github.com/Systemorph/MeshWeaver/actions/runs/34513634943)
(PR #3956, 2026-09-10T18:29:17Z), `Graph.Test Total: 1519, Failed: 1`:

```
Did not expect collection {"3YOdHJMOHEKUFJlwHFjJmg"} to contain "3YOdHJMOHEKUFJlwHFjJmg"
because the owner minted an OwnerDisposing NACK for this …
  NackReachesTheWaiterDuringTeardownTest.cs(242,0)
```

Its own trace, in order:

```
[write]   patch posted with marker teardown-nack-d3197828e2; owner merge is parked
[fence]   caller is armed on the late watch (request=3YOdHJMOHEKUFJlwHFjJmg)
[dispose] mesh disposal invoked — parent is past DisposeHostedHubs
[owner]   TestData/teardown-nack-node is Dead — its disposal registrants have run
```

**Not the diff, and not a catalogued flake.** #3956 touches `deploy/whisper/**` plus two doc pages
and cannot reach `MeshWeaver.Graph.Test`; six sibling PRs built in the same window (#3952, #3957,
#3955, #3959, #3947, #3946) have zero failing jobs; neither twin is in `.github/known-flakes.json`.

### It is a DIFFERENT seam from #3510's, and the difference is where the verdict is

| | #3510's trail | this sighting |
|---|---|---|
| the owner | **lives** — the ROOT under it is recycled | **dies** — it reaches `Dead` |
| the verdict | **never minted**: `PATCH_MERGE_STAMPED → PATCH_ECHO_SEEN → (nothing)`, no bound armed, nobody owes one | minted by the ShutDown-phase registrant, per the assertion this test makes |
| what the caller sees | `ADVANCE_WITHOUT_HANDOFF` at 5 s, then `OwnerUnreachable … no verdict within 31s` | silence, then the full 31 s budget |

So they are two points on one lane, not one defect: #3510 is *nobody owns the answer*, this is *the
answer exists and the waiter does not observe it*. Both surface identically to the caller, which is
why the two get conflated on a first read.

### 🚨 The one thing to check first on the next occurrence

The test's precondition is `owner.RunLevel == Dead`, and its assertion message reads it as *"the
owner is now terminally Dead, so its ShutDown-phase registrant has already run"*. **That implication
holds on ONE of the three paths to `Dead`.** `HandleShutdownCore`'s ShutDown case sets `Dead` in the
success path (after `DisposeImpl()`), **again in the `catch`**, and **again in the `finally`
backstop** — so a `DisposeImpl()`/`messageService.Dispose()` that threw reaches `Dead` with the
registrant's work incomplete, and the precondition is satisfied without the causal fact it stands
for. That is the [Controls That Cannot Fail](/Doc/Architecture/ControlsThatCannotFail) shape *an
input the operator supplies*, applied to a precondition instead of a check.

This is stated as **the first hypothesis to eliminate, not as the cause** — the run carries no
evidence of which path was taken, which is precisely the gap. The discriminator is one line: the
`catch` logs `Error during shutdown of hub {address}` and the success path does not. Read that
before reading anything else, and see [Teardown Verdicts Are Causal](/Doc/Architecture/TeardownVerdictsAreCausal)
for why the precondition was made causal in the first place.

🚨 **Both twins must move together.** `NackReachesTheWaiterDuringTeardownTest` and
`LateNackReenqueueTest` are hand-synced with copies in MeshWeaver.Plugins, compared by that repo's
`TeardownTwinParityTest` at `MW_PLATFORM_REF` — so it reddens in the PIN BUMP, not in core. A change
to what either twin asserts is not done until the Plugins copy carries it (break shape 7; see
[Cross-Repo Pair Gate](/Doc/Architecture/CrossRepoPairGate) → "Shape 7's worst form").

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
