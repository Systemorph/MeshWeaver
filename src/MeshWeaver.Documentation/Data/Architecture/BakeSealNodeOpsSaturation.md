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

## Open — what the next measurement must be

**Which rule holds `nodeops`' action block?** The 25 s `Executing(CreateNodeRequest, …)` capture is
from 2026-08-28 and has not been reproduced since; today's job logs carry no hub-reader dump. Until
that is measured, the saturation's *cause* is named but not identified. Two candidate shapes, both
consistent with the actor loop awaiting the full rule chain:

- a rule that performs IO per delivery (a storage read, a permission fold, a path resolution) and is
  therefore charged to the block rather than to the pool; or
- amplification — a delivery whose failure produces more deliveries on the same hub, the shape
  [Action-Block Wedge Prevention](../ActionBlockWedgePrevention) exists to forbid.

Do **not** raise `InstallTimeout`, `LateResponseWatchBound` or `QueueAdvanceBound` to make the gate
pass. Every one of those bounds is already reporting the truth: the shared hub is not draining.

## Related

- [Action-Block Wedge Prevention](../ActionBlockWedgePrevention) — the invariants a single-threaded hub
  must satisfy so no input can saturate it.
- [Bounds Must Be Ordered](../BoundsMustBeOrdered) — why an inner bound just under an outer one
  destroys the outer one's diagnosis.
- [Reading a Write Verdict](../ReadingAWriteVerdict) — what each owner-side error code means and which
  ones are auto-retried.
- [Reading CI Signals](../ReadingCiSignals) — why a skipped or absent required context reads as green.
