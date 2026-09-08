---
Name: Bake Seal — NodeOps Saturation
Category: Architecture
Description: "Why the CD bake+seal gate fails intermittently. The hub that stops answering is portal/nodeops — the mesh's ONE node-CRUD execution hub — not the per-node hubs the symptom names. Measured on a same-morning pass/fail pair 8 minutes apart."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2v4M12 18v4M4.93 4.93l2.83 2.83M16.24 16.24l2.83 2.83M2 12h4M18 12h4M4.93 19.07l2.83-2.83M16.24 7.76l2.83-2.83"/></svg>
---

# Bake seal — the hub that stops answering is `portal/nodeops`

The CD job
`Plugins: bake + seal the publication for this identity / Bake + publish NodeType assemblies to portal storage`
fails intermittently. It is filed as
[#2543](https://github.com/Systemorph/MeshWeaver/issues/2543), *"per-node hubs stop answering
mid-bake"*.

**The per-node hubs are not where the silence starts.** Every per-node owner in a failing run is
`RunLevel=Started` with an EMPTY queue, waiting on the one hub they all share. This page records
what was measured, so the next session does not re-derive it.

## 🚨 The one sentence

**`portal/nodeops-{meshId}` is the mesh's single node-CRUD execution hub — every
`CreateNodeRequest` and `CreateOrUpdateNodeRequest` in the entire mesh is serialised on its one
action block — and during a bulk node-repo install it stops draining for tens of seconds. Everything
downstream then reports its own bound expiring, which is what makes the failure look like N
unrelated defects.**

## What actually fails the gate

Not the write wedge the issue is named after. The verdict, in **6 of 6** failing seals measured on
2026-09-03, is byte-identical:

```
[FAIL] Hosting (counts unavailable — the pipeline threw before the install reported)
    install: [install] TimeoutException: The operation has timed out.
GATE FAILED — install: Hosting
```

`"The operation has timed out."` is `TimeoutException`'s DEFAULT message, i.e. Rx's own
`.Timeout(...)` — here `PluginGateRunner`'s `InstallTimeout` (10 minutes,
`tools/MeshWeaver.PluginTester/PluginGateRunner.cs`). The hub's `RequestTimeout` throws a
*different*, richly-worded `TimeoutException` (`"No response received in hub X within 00:01:00 …"`),
so the message alone tells you which bound fired.

Measured, every failing run, to the second:

| | install starts | adoptions done | install ends |
|---|---|---|---|
| PASS (run `33727875031`) | 07:55:24 | 07:56:43 | **07:57:15 — `installed (140 written)`** |
| FAIL (run `33728545478`) | 07:57:12 | 07:58:46 | **08:07:12 — exactly T+600 s, TimeoutException** |

The same shape in `33702118699`, `33713287866`, `33714777607`, `33720758000`, `33730276201`: the
install begins, the seed adopts 15/15 prebuilt assemblies, the compile-state mirror's satellite
writes fail, and then the process emits **nothing at all** until the gate's own 600 s bound fires.
The passing control did the identical work in **111 s**.

### The write wedge is a co-symptom, not the cause

Run `33730276201` failed exactly this way with **ZERO** `VERDICT_TIMEOUT` and zero
`OwnerUnreachable`. The `_Activity/compile-state` write failures that #2543 quotes are
`CompileStateMirror` background writes — caught, logged, retried on the next change — and they are
neither necessary nor sufficient for the gate to fail. Chasing the 31 s write verdict is chasing a
sibling of the real thing.

## Where the silence starts — measured

**1. The requesting hub is idle.** `CompileStateMirror`'s own diagnostic in run `33730276201`:

```
[CompileStateMirror] Hosting/FleetConsole: satellite write failed …
System.TimeoutException: No response received in hub Hosting/FleetConsole within 00:01:00
for request CreateOrUpdateNodeRequest (id=lO-4g1N-Fkye6JnS6vzjAQ)
  → target portal/nodeops-Q-40lJF-CEWErGWdWTeelQ.
This hub: RunLevel=Started Queue(buffer=0,deferred=0,openGates=0,deliveryActionCompleted=True).
This hub was idle while waiting, so it processed everything delivered to it and the silence is
upstream of here.
```

Started, empty queue, no open gates. The per-node owner is not wedged — it is *waiting*.

**2. The delivery is sitting in `nodeops`' inbox, never executed.** From the request-fate trail of a
stale callback in the same run:

```
… → RECEIVED runLevel=Started@portal/nodeops-Q-40lJF-CEWErGWdWTeelQ(+2ms)
  → ENQUEUED@portal/nodeops-Q-40lJF-CEWErGWdWTeelQ(+2ms)
⇒ the delivery reached a hub but no handler was ever entered — it is still being routed,
  or it was accepted and never executed.
```

`ENQUEUED` at +2 ms, and 33 seconds later still no `HANDLER_ENTER`. Note there is **no `DEFERRED`
stage** — the message is not parked behind an init gate, it is in the buffer of an action block that
is not advancing.

**3. `nodeops`' queue latency, measured across the failing runs.** Every
`state=Submitted@portal/nodeops(+Nms)` in the request trails, bucketed:

```
≤ 3 171 ms   (36 deliveries — normal)
33 374 ms · 39 454 · 39 808 · 40 725 · 43 522 · 44 192 · 47 791 · 49 344 ms   (22 deliveries)
```

Nothing between 3.2 s and 33 s. That is not a slow hub; it is a hub that stops and restarts.

**4. Who reports it.** Stale callbacks in the failing runs are almost entirely the `Hosting/*`
NodeType hubs — 13 `FleetConsole`, 11 `InstanceRequest`, 7 `InstanceAction`, 6 each `Issue` /
`DeploymentStatus` / `Admin`, … — every one of them a `CreateOrUpdateNodeRequest` addressed to
`portal/nodeops`. The passing control has **three**, all from one unrelated package. They do not
stop answering independently; they queue behind one hub.

## Why this presents as "many unrelated owners at once"

`portal/nodeops-{meshId}` is documented in `MeshExtensions.cs` as *"The mesh's ONE dedicated
node-CRUD execution hub"*, and it is additionally the router's designated carrier
(`RouterCarrier`). So `Store/*`, `Edu/*` and `Hosting/*` failing to ack inside the same
30-second window is not a coincidence to be explained — **it is one hub, seen from eight places.**

The actor loop awaits the delivery's **whole rule chain**, not just the handler
(`MessageHub.HandleMessageAsync` composes the rules with `SelectMany` and the actor-loop edge
subscribes). A rule that goes async holds the block for its full duration. #2543 captured this
directly on 2026-08-28:

```
Reader: Hub portal/nodeops-… RunLevel=Started Queue(buffer=45,deferred=0,exec=0)
  Executing(CreateNodeRequest, 24888ms)
  PendingCallbacks=26[ GetDataRequest@Store/Core, @Store/Install, … ]
```

One delivery occupying the block for 25 s, 45 queued behind it. That is the mechanism; **which**
rule spends the 25 s has not yet been measured and is the next thing to find (see *Open* below).

## 🚨 It is not a recent regression — do not bisect

Measured over core's CD workflow, counting only runs where the bake/seal job reached a terminal
conclusion:

| period | success | failure | N | rate |
|---|---|---|---|---|
| 2026-08-29 → 09-02 | 76 | 31 | 107 | **29 %** |
| 2026-09-03 (to 08:37Z) | 3 | 8 | 11 | 73 % |

and the "before" period is not stationary — **2026-09-01 alone was 16/19 = 84 %**, higher than
today. All 39 failures are on the same step. A bisection against recent plugin merges cannot
separate this signal from its own day-to-day variance; the job itself is only ~4 days old
(it first appears in `main-cd.yml` at `3a5dfe45`, 2026-08-29T21:13Z), so no longer baseline exists.

## Bounds that make the symptom unreadable

Downstream of the saturation, four owner-side seams turn "the shared hub is busy" into "the owner
produced no terminal". Each is a real defect in its own right; none of them is the cause.

1. **The generic patch path has no bound at all.** `DataExtensions.ApplyJsonMergePatchAndUpdate`
   opens with `stream.Take(1).WhenCompletesEmpty(…).Subscribe(onNext, onError)` and **no
   `.Timeout(...)`**. Rx's fourth outcome — never emits, never completes — is uncovered, and the
   only bounded watcher on that path (`postSub`) is created *inside* `onNext`, so it is never
   armed. A MeshNode patch reaches this path whenever
   `GetDataSourceForType(typeof(MeshNode))?.GetStreamForPartition(null)` is null.

2. **`deferSub` has no completion arm.** In `ApplyMeshNodePatchInTurn` the cold-store re-arm is
   `primary.Where(…).Take(1).Timeout(10s).Subscribe(_ => RunMergeTurn(true), _ => AckOnce(…))`. If
   the primary store *completes* inside the bound, `Take(1)` completes, the `Timeout` is cancelled
   by that completion, neither arm runs, and the merge turn already returned `null`.

3. **`AckOnce` latches the once-only gate BEFORE the post and discards the post's result.** When the
   post is refused (`POST_REFUSED_SHUTTING_DOWN`), the gate is already claimed, so
   `RegisterOwnerDisposingNack`'s `tryClaimAck()` returns false and the
   `ILatePatchVerdictSink.Dispatch` route — the one that reaches an armed waiter with no message
   routed — is skipped. Every `AckOnce` call site is exposed; only the stand-aside case is guarded.

4. **The `ownerIsShuttingDown()` stand-aside is unbounded, and its safety argument cites a cap that
   was deleted.** `ArmPatchAckWatcher` deliberately posts nothing when `hub.IsShuttingDown`,
   deferring to the ShutDown-phase disposal NACK. `IsShuttingDown` is
   `disposalStarted || hostedHubs.IsCreationFrozen`, and `IsCreationFrozen` flips on an **ancestor's**
   `CloseCreation()` cascade — "potentially seconds" before this hub's own `DisposeRequest`.
   `LatePatchWriteWatch` justifies its 30 s window as dominating *"the disposal NACK after the
   owner's phased teardown (hosted-hub drain **capped at 5 s**)"* — but that cap was removed in
   #1317: `HostedHubsCollection.DisposeHubsReactive` now reads
   *"No Timeout — … the owning hub's disposal watchdog is the single backstop"*, and that watchdog
   is a **stall** detector re-armed on every subtree `RunLevel` transition, not a duration.

   Past 30 s the late verdict is not merely late: `LatePatchResponseRegistry.Dispatch` removes the
   entry *before* checking expiry and returns `false` without re-adding, so it is discarded in
   silence — which is why a failing run can carry `VERDICT_TIMEOUT` with **zero**
   `LATE_NACK_TERMINAL`.

There is a fifth, arithmetic one. `LatePatchWriteWatch` enumerates the owner-side paths as
alternatives and takes their MAX (20 s) as the thing its 30 s must dominate. In
`ApplyMeshNodePatchInTurn` they compose **additively** — cold-store defer (10 s) → identity-gated
echo (20 s) → durable flush (10 s) — and the owner's clock starts at HANDLER ENTRY while the
caller's starts at POST. With `nodeops` queue latency measured at 33–49 s, the interval between
those two instants is larger than the entire margin. See
[Bounds Must Be Ordered](../BoundsMustBeOrdered).

## How to read a failing seal in 60 seconds

1. `gh api "repos/Systemorph/MeshWeaver/actions/jobs/<id>/logs"`, then strip ANSI. **The Actions log
   echoes the script source with a `[36;1m` prefix — those lines are the SCRIPT, not output.**
2. `grep "GATE FAILED"` — if it says `install: <Package>`, the gate died on
   `PluginGateRunner`'s 600 s `InstallTimeout`, not on a write verdict.
3. `grep -o "state=Submitted@portal/nodeops[^)]*)"` — bucket the `+Nms` values. A bimodal
   distribution with a tail past 30 s is this defect.
4. `grep "This hub was idle while waiting"` — if the requester was idle, the silence is at the
   target, and the target named after `→ target` is the hub to investigate.
5. Signature counts (`STALE-CALLBACK`, `ADVANCE_WITHOUT_HANDOFF`) are **amplitude, not identity** —
   they are present in passing runs too. The fate trail discriminates; the counts do not.

## 🚨 2026-09-08 — the capture this page reasons from was read through a BROKEN INSTRUMENT

Everything above that says *"the hub stops draining"* rests on one dump, quoted twice on this page
and throughout [#2543](https://github.com/Systemorph/MeshWeaver/issues/2543):

```
Reader: Hub portal/nodeops-… RunLevel=Started Queue(buffer=45,deferred=0,exec=0)
  Executing(CreateNodeRequest, 24888ms)
```

**`exec=0` measured nothing.** `MessageService.GetQueueSnapshot` returned a **hard-coded literal
`0`** in that position — a leftover from the TPL-Dataflow pump that the turn loop replaced — so it
was true of every snapshot ever taken, on every hub, in every state. It is the field a reader
naturally takes as *"no drain is running"*, i.e. as the very evidence that the pump has stopped, and
it never said that about anything. Replaced by a live `Interlocked` count in
[#3593](https://github.com/Systemorph/MeshWeaver/issues/3593) (`4e440f630`, **2026-09-07T15:05Z** —
about an hour after the last comment on #2543, so **no reading in that entire thread used the fixed
instrument**). The same commit renamed `deliveryActionCompleted`, whose printed name asserted the
negation of its datum, to `draining`. Full taxonomy:
[Reading a Disposal Stall Verdict](../DisposalStallVerdicts).

🚨 **And there are TWO diagnostics here, not one — which is why the dumps were never found.**
Measured over the 61 bake jobs (see the denominator below): the `Hub <address> RunLevel=… exec=…`
form is `GetPendingRequestDiagnostics`, written on a **disposal stall**, and it fires **0 times in
1,512,136 log lines**. `exec=` therefore appears **nowhere** in a live bake — searching for it finds
nothing and reads as "no dumps exist". What a bake actually emits is
`BuildTimeoutMessage`'s clause, embedded in every hub `TimeoutException` and beginning
**`This hub:`** (lower-case *h*) — **34 occurrences in 20 of 61 jobs.** Its old-vocabulary field is
`deliveryActionCompleted`, not `exec`; #3593 renamed both. Grep for `This hub: RunLevel=`, never for
`Hub .* Queue\(buffer=`.

### 🚨 The 34 dumps describe the WRONG HUB — and the message says so itself

Every one of the 34 is the **requester** talking about itself, and all 34 are byte-identically idle:

```
This hub: RunLevel=Started Queue(buffer=0,deferred=0,openGates=0,drainsInFlight=0,draining=False).
This hub was idle while waiting, so it processed everything delivered to it and the silence is
upstream of here. Cause is UNKNOWN between: the target never received the request (routing), the
target received it and is wedged …, or the target answered and the reply was lost.
This message cannot distinguish them; the target's own RunLevel and queue can.
```

`buffer=0, deferred=0, openGates=0, drainsInFlight=0, draining=False` in **34 of 34**; `Executing(`
absent in **34 of 34** (the clause is appended only when a handler is on the block). 32 of the 34 are
`CreateOrUpdateNodeRequest → portal/nodeops-…`.

**The last sentence names the measurement and nothing takes it.** `portal/nodeops`' own `RunLevel`
and queue have not been observed since the single 2026-08-28 capture — not because the state is hard
to read, but because no diagnostic in the system prints the TARGET's. That is the gap
`[STALE-CALLBACK] … TARGET …` closes.

### What survives, and what was never established

`Executing(T, N ms)` **is** a real measurement and always was: `MessageService.RunHandler` sets
`currentlyExecutingMessageType` immediately before `HandleMessageAsync` and clears it in that
observable's `.Finally`. So the dump does say a `CreateNodeRequest` handler had been on the block for
24.9 s with 45 turns behind it — and since `MessageService.DrainLoop` advances **only** when a turn's
observable terminates (`Terminal()` is the sole thing that re-schedules the drain; no timer, no
watchdog, nothing else restarts it), those 45 were going nowhere until it did.

What was never established is **which of the two roots** that is, because they are the same picture
until `drainsInFlight` is read beside it:

| `drainsInFlight` | `Executing(T, N ms)` | Reading |
|---|---|---|
| **> 0** | set | A **thread is inside the handler** and has been for N ms. The turn is BLOCKED, not slow — look for a synchronous wait taken on the block. |
| **0** | set | **No thread is in the handler**, yet the turn's observable has not terminated. The handler returned something that never completes; the terminal is owed by an async leaf. |
| 0 | absent, `draining=True`, `buffer>0` | The drain was **scheduled and never ran** — a scheduling stall in the target, not a handler at all. |
| 0 | absent, `buffer` small | The target is **IDLE**. The silence is not queueing; look at the reply leg. |

Those are four different defects. Rows 1 and 2 are the two candidates named below, and until
2026-09-07 nothing in the fleet could tell them apart.

### The instrument now emits itself — no dump to capture

The reason the 08-28 dump *"has not been reproduced since"* is that nothing emits it on a live mesh:
the hub reader dump is written on a **disposal stall**, and a mesh saturating mid-bake never reaches
one. Meanwhile `[STALE-CALLBACK]` fires every 5 s and 300+ times in a failing bake — and described
the wrong hub. Every field on it (`{Address}`, the pending list, the pool counters) belongs to the
hub that is **waiting**, and a waiting hub is idle by construction, so the line reported truthfully
and uselessly that the reporter had nothing to do. The hub whose state decides the verdict is the
TARGET named inside the detail (`…@portal/nodeops-…`), and it was never described.

It is now. `MessageHub.ScanStaleCallbacks` resolves each distinct target to a local hub — a pure
read, `HostedHubCreation.Never`, because a diagnostic must never construct a hub — and appends its
turn-loop state:

```
[STALE-CALLBACK] Hosting/Admin: 1 callback(s) pending > 30000ms: …=CreateOrUpdateNodeRequest@portal/nodeops-…(31018ms)
  [pool threads=12 pendingWork=0 completed=88401]
  TARGET portal/nodeops-… RunLevel=Started Queue(buffer=45,deferred=0,drainsInFlight=1,openGates=0,draining=True) Executing(CreateNodeRequest, 24888ms)
```

Deliberately the COMPACT snapshot (`GetTurnLoopSnapshot`), not `GetPendingRequestDiagnostics`: the
target's own callback list runs to kilobytes and multiplying that by 300 lines is how a hub once
emitted a ~100 KB line that broke the TRX parsers. Capped at three distinct targets per line. The
target's own scanner tick prints its callbacks.

### The denominator, 2026-09-07T00:00Z → 2026-09-08T18:45Z

Every `Plugins: bake + seal … / Bake + publish NodeType assemblies to portal storage` job in core's
`Continuous Delivery (main)` (workflow `303778518`): 182 runs created, 80 non-cancelled, **61 bake
jobs**, **61/61 logs fetched, 0 unfetchable**, 0 re-attempts. Durations 8m23s / 24m30s / 33m41s
(min / median / max).

| | count | of 61 |
|---|---:|---:|
| `success` | 50 | 82 % |
| `failure` | 11 | 18 % |
| carrying `GATE FAILED` | **6** | **9.8 %** |
| `ADVANCE_WITHOUT_HANDOFF` present | 61 | **100 %** |
| `STALE-CALLBACK` present | 59 | 97 % |
| `OwnerUnreachable` present | 25 | 41 % |
| `PATCH_ECHO_SEEN` present | 16 | 26 % |
| `Executing(` present | **0** | **0 %** |

🚨 **Only 6 of the 11 failures are this defect.** The other 5 are a concurrent-publish collision on
the prebuilt-bundles PVC (*"the shelf holds a different sha"*, *"could not read back after uploading
it"*) — a different bug that a `failure` conclusion alone does not separate. **Counting red bake
jobs overstates this issue by ~2×**; count `GATE FAILED`.

And the weak form is **universal, not diagnostic**: `ADVANCE_WITHOUT_HANDOFF` is present in 61 of 61
jobs including all 50 green ones. Signature counts are amplitude; presence discriminates nothing.

**One cohort worth re-measuring before anything else.** Splitting the 61 on `3893dc486`
(*"arm the patch flush bound BEFORE the flush is built"*, merged 2026-09-08T06:55Z — the #3510 fix
whose reasoning is recorded at the end of this page):

| cohort | n | `OwnerUnreachable` jobs | `PATCH_ECHO_SEEN` jobs | `GATE FAILED` |
|---|---:|---:|---:|---:|
| before `3893dc486` | 45 | 25 (56 %) | 16 (36 %) | 6 |
| **after** | **16** | **0** | **0** | **0** |

The last `OwnerUnreachable` and the last `PATCH_ECHO_SEEN` in the whole window are the **same log
line**, 2026-09-08T07:45:53Z; the first post-fix bake started 08:35:34Z. 🚨 **Confounded, and not to
be banked**: all 16 post-fix jobs are short (≤ 23m31s), and prevalence tracks duration — the
duration-matched pre-fix rate is 4/13 (31 %) and 2/13 (15 %), so n=16 clean is suggestive, not
decisive. Re-measure once a long post-fix bake exists. This is the *write-verdict* half; it says
nothing about queue latency on `nodeops`.

**Where it does NOT reproduce.** In core outside the bake, over 32 jobs / 426 k lines
(`Doc content compiles and runs` ×16, `Run tests (shard 0)` ×16): `STALE-CALLBACK`,
`OwnerUnreachable` and `PATCH_ECHO_SEEN` are **0**; only `ADVANCE_WITHOUT_HANDOFF` appears, twice.
🚨 The issue's claim that this reproduces in PR gates is about `test-repos / Compile + render node
repos` and `Gate shard`, which are `node-repo-gate.yml` jobs — **core never calls that workflow**;
they exist only in the satellites. That half is still unmeasured.

🚨 **Scope — this covers the SATURATION shape, not the release-wave park.** The 2026-09-07 reading
below measured **zero** `[STALE-CALLBACK]` lines during its eight-minute park, so an instrument
riding on that line says nothing about it (#3510 closed that one separately). What it covers is the
shape this page's first half measures: bimodal queue latency with a wall of stale callbacks, where
by construction the line fires and the target is exactly the hub in question.

**Calibration, so the reading is not itself a guess.** `NodeCrudTurnStateIsMeasuredTest` parks a real
`CreateNodeRequest` inside the node-CRUD execution hub's turn (an `INodeValidator` that blocks —
the same harness [The /api/content 503](../ContentRoute503) uses to establish that a validator runs
*inside* that turn) and asserts the hub then reports `drainsInFlight` non-zero with
`Executing(CreateNodeRequest, …)`; a negative control asserts an idle hub reports neither. That pair
is what stops the field quietly becoming a constant again — which is the whole defect above, and
the reason a field that cannot fail is not a measurement.

## Open — what the next measurement must be

**Which rule holds `nodeops`' action block?** Not a rule at all: the fold has been walked and
**no rule after the create handler can hold it**. `Executing(T, ms)` is set inside
`MessageService.RunHandler`, so the window it measures is `HandleMessageAsync` — the rule fold —
and nothing before it. (Worth knowing on its own: `AccessControlPipeline`'s permission fold is a
DELIVERY-PIPELINE step, not a rule, so it runs while `Executing` reads *idle*. It is the obvious
"IO per delivery" suspect and the field would never have shown it.)

Inside the fold, the eight `WithNodeOperationHandlers` handlers are registered through
`WithHandler<T>(sync)`, which is `Observable.Return(delivery.Invoke(...))` — **the handler body runs
before the observable is even built**. `HandleCreateNodeRequest` is detached by design (it
`.Subscribe(...)`s its chain and returns `Processed()`), so everything downstream of the first
storage read runs off-block. What is charged to the block is therefore exactly: the handler's
synchronous prologue, plus whatever `persistence.Read(node.Path, …)` does **at composition time** —
that line is a CALL, not a `Defer`.

🚨 **Measured, not assumed: on the bake's wiring the create pipeline does NOT hold the block.**
`NodeCrudDoesNotOccupyTheExecutionBlockTest` parks a real create inside its own validator and reads
the execution hub as `Queue(buffer=0,deferred=0,drainsInFlight=0,openGates=0,draining=False)` —
completely idle, because storage reads go through `IIoPool` and the chain leaves the block at that
hop. The first draft of that test asserted the opposite and failed. **So a create that holds the
block for 24.9 s is doing something this path does not normally do**, and "the create pipeline is
expensive" is not the answer.

## 🚨 CORRECTION (2026-09-08): half of the paragraph above is wrong, and the fix is one line

Everything above about the *request turn* holds. Two inferences drawn from it do not, and both were
falsified by measurement rather than by re-reading the code.

**Correction 1 — `Executing(T, N ms)` does NOT measure time on the block.** The mechanism is stated
correctly earlier in this page (`currentlyExecutingMessageType` is cleared in the handler
observable's `.Finally`) and then read the wrong way round. `.Finally` fires when the handler's
**observable completes** — i.e. when the whole detached chain finishes — not when the pump is
released. So the field measures the handler's in-flight LIFETIME, including every pooled hop the
chain has already left the block for. Demonstrated: with a create parked in its validator, that
counter read `Executing(CreateNodeResponse, 36001ms)` and tracked the observer's sampling window
exactly. **Therefore #2543's `Executing(CreateNodeRequest, 24888ms)` never established that the
block was held for 24.9 s** — the `buffer=45` beside it is the part that was real. The
`drainsInFlight` table above inherits this error and reads `> 0` as "a thread is inside the
handler"; it is "a chain is still in flight", which is not the same claim.

**Correction 2 — the create pipeline DID hold the block, via its continuation.** The request turn
is genuinely free (`HANDLER_EXIT state=Processed` at +1 ms, confirmed on the fate trail). But the
detached chain does a partition bootstrap, whose nested response comes back to the SAME hub as a NEW
turn — and the rest of the pipeline, validators included, ran inline inside that response's turn.
The earlier reading of an idle hub was taken at a single instant before that turn began, and in
isolation the path differs; under load the test caught it at 0–1 ms and the assertion read it as
noise.

**How it was settled — behaviourally, because the snapshot fields cannot decide it.** With a create
parked, post an INDEPENDENT node create to the same hub:

| | result |
|---|---|
| probe alone, nothing parked (**positive control**) | completes promptly |
| same probe, while a create is parked | never runs — `Queue(buffer=1,…)` for the whole budget |

The control is what makes the second row mean "the pump is held" rather than "this probe never
completes anyway".

**The fix, and it is not in the node-CRUD path at all.** A response subject is signalled from inside
the turn that handled the response, and Rx runs a continuation on the thread that signalled it — so
*every* `hub.Observe(x).SelectMany(…)` chain in the framework ran its remainder on that hub's action
block, inside that turn. `MessageHub.ContinueOffBlockRestoringUserContext` now hops the continuation
off the block (`ObserveOn`) before restoring the caller's identity — the hop must precede the
identity restore, or the chain resumes unauthenticated. Create, delete, move and copy are fixed by
the same line because they all await nested node ops the same way.

`NodeCrudDoesNotOccupyTheExecutionBlockTest` carries the three tests: the probe, its positive
control, and `TwoCreates_AreInFlightSimultaneously`, which parks two creates at once and requires
both validators to be entered before either is released. With the hop removed the first and third
fail and the control still passes; that pairing is the evidence, not the green.

Two shapes remain, and the `drainsInFlight` table above picks between them:

- **A blocking construct in the composition prologue.** Exactly one exists on the path:
  `HostedHubsCollection.GetHubWithOutcome`'s per-address creation `Lazy`
  (`LazyThreadSafetyMode.ExecutionAndPublication`, `lazy.Value`), which blocks a second caller for
  the whole of `CreateHub` — unbounded, and `CreateHub` is *recursive* (`SyncBuildupActions` reach
  `SynchronizationStream`'s constructor, which always creates a `sync/{clientId}` hub; 1,350 nested
  Builds measured in one green test run). It is reached inline because
  `RoutingProxyAdapter.Read → LegacyUserPartitionRepair.ReadWithRepair → ReadCore →
  PartitionStorageRouter.AddressFor` evaluates `SpawnOrReuse` inside `Observable.Return(...)`, i.e.
  eagerly on the calling thread. That router's hub cache carries a **5-minute sliding expiration
  with a post-eviction `Dispose`**, so after any lull the next create re-pays a full hub
  construction on the turn — a bimodal cost by construction, which is the shape this page measures.
  🚨 **But this path is NOT in play for the bake:** `AddPartitionStorageHubs` is called nowhere in
  `src/`, and the bake host wires `AddInMemoryPersistence()`
  (`tools/MeshWeaver.PluginTester/PluginGateRunner.cs`). It is live for a portal wired that way, and
  is a real defect there.
- **Amplification.** Confirmed on the SUCCESS path, which is the case
  [Action-Block Wedge Prevention](../ActionBlockWedgePrevention)'s invariants do not cover — they
  are written about failure producing more messages. Every hop lands back on `nodeops`: an upsert
  dispatches an inner `CreateNodeRequest` **to its own address** (so one upsert ≥ 3 turns — upsert,
  inner create, response callback); `EnsurePartitionBootstrap` issues nested `meshService.CreateNode`
  calls whose `NodeOperationTarget` resolves to `nodeops` again; and `nodeops` is the router's
  `RouterCarrier`, so `Workspace.AnnounceRecycleToClientSubscriptions` posts one `StreamEndedEvent`
  per orphaned client subscription through it — during exactly the package-root recycles a bulk
  install performs (~120 per bake gate run). A handful of concurrent creates is arithmetically the
  `buffer=45` in the capture.

**Ruled out on the way, so the next session does not re-derive them:**

- **`ActivatePendingControlPlane`** — fire-and-forget, strictly after the response, gated on the
  content carrying a `RequestedXxx`. It cannot delay a create.
- **The access-control permission fold** — runs outside the window `Executing` measures.
- **Every leg of `EnsurePartitionBootstrap`** — it does three storage reads and a permission fold,
  but all are bounded (`Timeout(15s)` + `Catch`, `DefaultIfEmpty`, 5 s absence probes) and all are
  downstream of the first read, hence off-block. Its *amplification* is the live concern, not its
  duration.

**The two measurements that would settle it**, in order of cost:

1. Read the next failing bake's `[STALE-CALLBACK] … TARGET portal/nodeops-… drainsInFlight=…` line
   and take the row from the table. No capture, no instrumentation round — the line already fires
   300+ times in a failing run.
2. If row 1 (a thread inside the handler): time `persistence.Read(node.Path, …)` at its call site,
   before the `.Subscribe`. It is the only inline call in the handler that can block, so one run
   discriminates. **And note the unresolved binding question**: `RoutingProxyAdapter` is documented
   as registered per-hub, but the only registration in `src/` is a container-level
   `services.Replace(Singleton<IStorageAdapter>)` resolving `sp.GetRequiredService<IMessageHub>()` —
   if that binds to the mesh hub, every node create's storage round-trips are issued from and
   dispatched on **the router's** block, which is the `ROUTER_TRAFFIC` shape `nodeops` exists to
   remove.

- a rule that performs IO per delivery (a storage read, a permission fold, a path resolution) and is
  therefore charged to the block rather than to the pool; or
- amplification — a delivery whose failure produces more deliveries on the same hub, the shape
  [Action-Block Wedge Prevention](../ActionBlockWedgePrevention) exists to forbid.

Do **not** raise `InstallTimeout`, `LateResponseWatchBound` or `QueueAdvanceBound` to make the gate
pass. Every one of those bounds is already reporting the truth: the shared hub is not draining.

### 🚨 One confound has been removed — read `PendingCallbacks` differently from now on

The 2026-08-28 capture reads `PendingCallbacks=26[ GetDataRequest@Store/Core, @Store/Install, … ]`
alongside `Executing(CreateNodeRequest, 24888ms)`. Those 26 were **not** issued by anything running
on `nodeops`: they are one-shot `GetMeshNode` reads from mesh-singleton services that hold the DI
root hub (the plugin catalog's boot services, the credential resolvers, the content route), and
`NodeOperationIssuingHub()` hopped every one of them onto this hub because it was the only
off-router hub there was. They then sat in the same block that could not dispatch them.

They now register on `portal/reads-{meshId}` instead — a hub with no handlers at all — via
`MeshExtensions.ReadIssuingHub()`. See [The /api/content 503](../ContentRoute503) for why, and for
the deterministic repro of the read side. Two consequences for the next measurement here:

- **A contributor is gone, not the cause.** Those reads no longer add deliveries to this block, and
  no longer burn 10 s budgets that their callers retry — but nothing about the duration of a
  node-CRUD turn has changed. If the bimodal latency survives, that is the real answer.
- **`PendingCallbacks` on `nodeops` is now attributable.** A read still pending there was issued by
  something running **on this hub**, which is a much smaller set to search than "any mesh singleton
  in the process".

## 🚨 2026-09-07 — the park is INSIDE the release wave, and it is not an unanswered node op (#3510)

Six seals were lost to this failure between 2026-09-06 23:36Z and 2026-09-07 06:09Z — CD 7950, 7959,
7967, 7968, 7976 failed while 7955, 7962, 7964, 7969, 7974, 7977, 7981 sealed. Roughly one started
run in two, `Hosting` every time, always `install: TimeoutException`. The reading below is from
**CD 7976** (`19b077868`, bake job
[101634597130](https://github.com/Systemorph/MeshWeaver/actions/runs/34085298189)).

### What the sixth occurrence measured

```
05:44:31.5  ── Hosting: installing 145 file(s)…
05:45:28.8  Installed node-repo plugin Hosting: 144 written, 0 unchanged
05:45:56.7  Install: Hosting: adopted 15 prebuilt assembly(ies) for 15 installed type(s)
05:45:56.7  [PackageInstaller] recycling root Hosting …            ← by design; it SUCCEEDS
05:45:59.6  InitializeHubRequest | Hub: Hosting                     ← the root is back, 2.9 s later
05:46:04.5  [UpdateQueue] ADVANCE_WITHOUT_HANDOFF path=Hosting/Admin … /Backup … /LogEntry
05:46:34.9  (last log line of any kind)
                                     ⋯ EIGHT MINUTES OF COMPLETE SILENCE ⋯
05:54:31.5  ── Hosting.Instance: installing 3 file(s)…              ← exactly T+600 s
```

**Nothing was pending.** `[STALE-CALLBACK]` reports every pending callback older than 30 s, every
5 s; it fired four times for HomeAssistant minutes earlier in this very run and **zero times** during
the eight-minute park. So the install was not waiting on a message at all — not on an unanswered
`CreateOrUpdateNodeRequest`, which is what #3510 was originally attributed to. It was parked on an
observable that never terminated.

**The control is in the same log.** `Edu` — same shape, same run — printed
`[PackageInstaller] warmed installed root Edu` 2.5 s after its own deferred release wave.
`warmed installed root Hosting` **never printed**. `WarmInstalledRoots` runs immediately after
`RequestReleases(deferredWave)`, so the park is inside the wave.

### Why the wave, by elimination from code

Every other composition between the deferred wave and the warm carries its own bound:
`AffectedNodeTypes` (`TypeEnumerationBudget`, plus a `Catch`), `SeedPrebuiltAssemblies`
(`SeedBound`), and the trigger write itself (`BaseStateWaitBound` 30 s, then verdict windows of
`LateResponseWatchBound + VerdictBoundGrace` = 31 s across `MaxConflictRetries`, ≈124 s worst case).
`PackageInstaller.RequestReleases`' `nodeTypePaths.Select(ObserveNodeTypeRelease).Merge().ToList()`
carried **none** — which `MeshNodeStreamHandle.BaseStateSource`'s remarks had already named in
advance: *"no per-leg bound and no outer bound — so ONE non-terminating leg parks the entire package
install, silently, until the gate's own 600 s `InstallTimeout` reports `install: TimeoutException`
against a package that installed fine 8 minutes earlier."*

### The fix, and what it is NOT

`ObserveNodeTypeRelease` states its own contract in its remarks and in its closing
`DefaultIfEmpty(false)`: **exactly one emission, always**. That covered three of Rx's four outcomes.
The fourth — a source that neither emits nor faults nor completes — reached no handler, and neither
`DefaultIfEmpty` nor `Catch` can see it. `NodeTypeReleaseExtensions.BoundReleaseLeg` now closes it:
`Take(1)` (so the deadline is TOTAL, not Rx's inter-emission one) then `ReleaseRequestBound`, whose
elapse answers `false`, logs, and **names the NodeType**.

🚨 **No bound was raised and nothing is retried.** `ReleaseRequestBound` is 180 s: strictly above the
≈124 s a legitimate trigger write can compose, and far below the installer's 600 s — the ordering
[Bounds Must Be Ordered](../BoundsMustBeOrdered) requires, so it can only fire on a leg that is
genuinely non-terminating and never on one that is merely slow. The outer 600 s bound knows only
which PACKAGE did not finish; this one knows which TYPE never answered.

**It does not claim to explain why a leg stops answering.** It converts an eight-minute silent park
into a named warning and lets the install finish, which is what makes the next occurrence one grep
instead of a full bake-log read — the same role #3512's recycle line plays. Pinned by
`ReleaseWaveLegIsTotalTest` (pure composition, `TestScheduler`, no mesh).

### What this retires

The `#3510` reading in [Write Verdict Totality](../WriteVerdictTotality) — *"the upsert lane's CREATE
leg has no disposal NACK"* — is a real totality gap and is worth closing on its own merits, but it is
**not** what fails these seals: 7976's one stale callback took the UPDATE leg
(`UPSERT_READ existing → update`), resolved inside 35 s, and the park that killed the run had no
pending callback at all.

## Related

- [Reading a Disposal Stall Verdict](../DisposalStallVerdicts) — what every field of a hub snapshot
  measures, and the three that measured nothing while being read as evidence.
- [Action-Block Wedge Prevention](../ActionBlockWedgePrevention) — the invariants a single-threaded hub
  must satisfy so no input can saturate it.
- [Bounds Must Be Ordered](../BoundsMustBeOrdered) — why an inner bound just under an outer one
  destroys the outer one's diagnosis.
- [Reading a Write Verdict](../ReadingAWriteVerdict) — what each owner-side error code means and which
  ones are auto-retried.
- [Reading CI Signals](../ReadingCiSignals) — why a skipped or absent required context reads as green.
