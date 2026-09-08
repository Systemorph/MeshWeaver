---
Name: Teardown Verdicts Are Causal, Not Timed
Description: "A verdict minted in a hub's ShutDown phase arrives when that hub's teardown reaches ShutDown — and the framework deliberately puts no duration bound on getting there. Any assertion that budgets such a verdict in seconds is asserting a guarantee the framework does not make, and it flakes."
---

# A teardown verdict arrives on a CAUSE, not on a clock

Some of this system's most important answers are minted during a hub's **teardown**, not during its
working life. The owner-disposing patch NACK is the canonical one: when a per-node hub goes down
owing an answer for an in-flight `PatchDataRequest`, a `RegisterForDisposal` registrant mints
`MeshNodeErrorCode.OwnerDisposing` and hands it to the waiting caller
(`DataExtensions.RegisterOwnerDisposingNack`, #2778).

**That registrant runs in the ShutDown phase — the LAST phase of the hub's teardown.** Everything
in front of it must finish first: Quiescing, then the whole `DisposeHostedHubs` join over the hub's
own subtree.

The rule this page exists to state is:

> **Nothing bounds how long that takes, on purpose. So nothing downstream may budget it in
> seconds — not production code, and above all not a test.**

## Why there is no bound, and why that is right

`HostedHubsCollection.DisposeHubsReactive` used to cap the hosted-hub join at a flat
`Timeout(5s)`. #1317 removed it, and the reasoning is worth restating because it is exactly the
reasoning that applies one layer up:

- Every leg of that join answers **from its own terminal state** — each child is a `MessageHub`
  whose disposal signals `DisposalCompleted` as its last act. The answer is guaranteed to come; it
  is not guaranteed to come inside any particular number.
- Nesting made the cap fire with nothing wedged at all: a child's disposal is its own quiesce
  budget plus its own subtree join, so a busy two-level tree exceeds a flat 5 s while every
  individual step stays inside its own budget.
- On expiry the collection signalled done *anyway*, and the owner tore down the DI container while
  children were still resolving out of it — the cap was **manufacturing** the leak it was meant to
  contain.

What replaced it is a **stall detector**, not a deadline (`MessageHub`'s disposal watchdog, #1701).
It is re-armed by `SubtreeDisposalProgress` on every `RunLevel` transition anywhere in the subtree.
So a large teardown that keeps making progress **never trips it, however long it takes** — which is
the correct behaviour and is precisely why "the teardown is taking a while" produces no report.

Read those two facts together and the consequence is unavoidable: **a healthy teardown has no
upper bound in time, and no signal that says how long it took.**

## The failure this produced (measured)

`NackReachesTheWaiterDuringTeardownTest` is the regression test for #2778. It parks the owner's
merge turn, posts a patch, fences on the caller being armed in `LatePatchResponseRegistry`,
disposes the **mesh**, and then asserted that the armed watch was consumed **within 6 seconds**.

That bound was chosen for two good reasons and one bad one. The good reasons: it is far below
`LateResponseWatchBound` (30 s), so an entry that merely *expired* could not be mistaken for one
that was answered; and it is below the 8 s disposal stall budget, so a stall verdict could not be
what produced the answer. The bad one, unstated: it also raced **the owner's entire teardown**,
which the paragraphs above say is unbounded.

### The rate, and why the asymmetry is the finding

| repo / suite | failures | executions examined | window | rate |
|---|---|---|---|---|
| MeshWeaver.Plugins, `src/MeshWeaver.Hosting.Monolith.Test` slice #2 | **5** | **138** | 2026-09-07T15:19Z → 2026-09-08T07:12Z | **3.6% — 1 in 28** |
| MeshWeaver (core), `test/MeshWeaver.Graph.Test` twin | **0** | **796** runs | 2026-09-05T15:24Z → 2026-09-08T07:23Z | 0% |

Same test body — they are twins, held in step by Plugins' `TeardownTwinParityTest` — and the same
framework. What differs is the size of the mesh being torn down, and therefore how long the owner's
teardown takes. A flat 6 s is comfortable in core's fixture and marginal in the portal's, which is
exactly what "asserting a bound the framework does not provide" looks like from the outside: it
tracks mesh size, not correctness.

Two measurement notes worth keeping, because both would have understated it:

* **Filter on JOB conclusion, not run conclusion.** Two of the five failures sit in runs whose *run*
  conclusion is `cancelled` — the test failed, then a newer push cancelled the run. Filtering on run
  conclusion finds 3 of 5.
* **One of the five was on `main`** (run `34176831015`, 2026-09-08T01:45Z). This reddens the trunk,
  not only pull requests.

### The failing run in detail

Run 34195323935, `Portal hosts (shard 2)`, 2026-09-08. The evidence is entirely **negative**, and
that is the tell:

| signal | present? | what its absence rules out |
|---|---|---|
| `LATE_NACK_REENQUEUE` (Warning) | no | the caller's watch never received any verdict, early or late |
| `[PatchAck] … reached no route` (Warning) | no | the owner never minted a verdict and lost it on both routes |
| `[LateWatch] VERDICT_EXPIRED` (Warning) | no | no verdict arrived after the 30 s window either |
| `Message delivery failed … cannot route` (Warning) | no | routing never refused the patch or its answer |
| `[QUIESCE-WAIT]` / `[QUIESCE-CUT]` | no | no hub re-armed its quiesce budget on an owed reply |
| `DISPOSAL DEADLOCK DETECTED` / `[DISPOSE-WEDGE]` | no | the stall detector never fired: the teardown kept moving |
| `MEM_WATCHDOG` on its normal 5 s cadence | **yes** | the process itself was healthy and not starved |

The one warning in the window was `[QUIESCE-TIMEOUT] cache/…: 0 callback(s) still pending after 2s
— Pending: <none>`, which `OnQuiesceComplete` documents as the *benign* shape: the poll said
not-drained and the dictionary was empty by the time the verdict was rendered.

Nothing had gone wrong. **The owner had simply not reached its ShutDown phase yet.** The test
asserted a timing guarantee that does not exist, and the failure it produced was a bare
`System.TimeoutException` naming a line number — no state, no armed ids, no run level. Six
CI-artifact greps were needed to establish "nothing happened", which is why this had recurred
without ever being diagnosed.

## The shape that is correct

Assert the **cause**, then read the state. The ordering inside `MessageHub.HandleShutdownCore` is
fixed and is what makes this exact rather than lucky:

```
ShutDown phase:
    CancelCallbacks()
    DisposeImpl()          ← disposables.Dispose() runs the RegisterForDisposal registrants
                             — this IS where the NACK is minted and dispatched
    messageService.Dispose()
    RunLevel = Dead        ← set BEFORE the signal, deliberately (see its comment)
    SignalDisposalCompleted()
```

So `RunLevel == Dead` (equivalently, `DisposalCompleted` having fired) is **the exact instant at
which "the owner has had its chance to answer" becomes true**. Waiting for that and then reading
the registry is a decision, not a race:

```csharp
// Wait for the CAUSE …
await Observable.Interval(50.Milliseconds()).StartWith(0L)
    .Where(_ => owner.RunLevel == MessageHubRunLevel.Dead)
    .FirstAsync().Timeout(ownerDownBound).Await(ct);

// … then the assertion is instantaneous and unconditional.
registry.ArmedRequestIds.Should().NotContain(armedId, "…the #2778 defect…");
registry.ExpiredVerdicts.Should().Be(0, "CONSUMED, not lapsed");
```

Three things get strictly better:

1. **It still catches the defect — sooner.** Falsified by restoring the pre-#2778 shape (dropping
   the `ILatePatchVerdictSink` dispatch so only the run-level-guarded parent post remains): the
   test fails in **2 s** instead of 6, and the message reads *"Did not expect collection
   {"gKQ_…"} to contain "gKQ_…" because the owner minted an OwnerDisposing NACK …"* instead of
   `TimeoutException`. The owner reaches `Dead` just as fast when it is broken; it is the *watch*
   that is still armed.
2. **"Consumed" stops being inferred from a stopwatch.** The 6 s bound was standing in for
   "it did not merely expire". `LatePatchResponseRegistry.ExpiredVerdicts` counts that case
   directly — #3197 made an expired verdict a *reported fact* rather than silence — so the
   distinction is now **observed**, and it stays true however long the teardown took.
3. **The remaining bound guards a PRECONDITION and says so.** If it trips, the failure is "the
   owner never finished disposing", which is a different defect with a different investigation —
   and the test's stall-verdict assertion (`verdicts.Entries.Should().BeEmpty()`) names it. It must
   stay below `LateResponseWatchBound`, because a question asked after the entry has lapsed cannot
   be answered either way; deriving it (`LateResponseWatchBound - 10.Seconds()`) keeps that true
   without a second literal, per [Bounds must be ordered](../BoundsMustBeOrdered).

### A second defect the same change removes

The old assertion polled `registry.ArmedCount == 0` — a **whole-registry** count. But dispatching
the NACK runs the caller's `LATE_NACK_REENQUEUE` arm, which posts a fresh `PatchDataRequest` and
**arms a second watch** microseconds later. `ArmedCount` therefore goes `1 → 0 → 1 → 0`, and the
zero the test was looking for exists for less than a millisecond. It passed only because the
re-attempt is refused quickly during a teardown — i.e. the assertion was reading a transient it had
no right to observe. `ArmedRequestIds` exists for exactly this (its own remarks say so): name the
correlation the verdict has to land on, and a second, unrelated arm cannot falsify it.

## The rule, generalized

**If an answer is produced by a `RegisterForDisposal` registrant, a `ShuttingDown` handler, or
anything else in a hub's teardown, then its latency is the teardown's latency — which is
deliberately unbounded and deliberately unreported.**

- Do not budget it. A duration assertion over it is measuring the teardown, not the answer.
- Wait for the terminal fact instead: `RunLevel == Dead`, or `DisposalCompleted`. Both are
  guaranteed to arrive or to be reported by the stall detector.
- 🚨 When you wait on `DisposalCompleted` directly, remember that awaiting an observable resumes
  the continuation **on the signalling thread** — here, inside `SignalDisposalCompleted()`, inside
  the hub's ShutDown `try`/`catch`. An assertion that throws there is swallowed and logged as
  "Error during shutdown of hub". Polling `RunLevel` off a timer, as above, avoids that entirely.
- In production this same fact has a caller-visible consequence, which is not a bug but is worth
  knowing: a write whose owner is torn down inside a large teardown wave waits for that teardown,
  and if it exceeds `LateResponseWatchBound` the caller is told `OwnerUnreachable` for a verdict
  that was minted. `VERDICT_EXPIRED` exists so that case is distinguishable from silence.

## Related

- [Bounds must be ordered](../BoundsMustBeOrdered) — the sibling rule: an inner bound just under an
  outer one destroys the outer one's diagnosis. This page is the case where the inner bound was over
  something with **no** outer bound at all.
- [Write verdict totality](../WriteVerdictTotality) — every in-flight patch gets exactly one
  terminal; this page is about *when* the teardown-phase one can arrive.
- [Disposal stall verdicts](../DisposalStallVerdicts) — why the watchdog is a stall detector and
  what it does and does not report.
- [Hub disposal model](../HubDisposalModel) · [Teardown layers](../TeardownLayers) — the phase
  ordering this page depends on.
- [Refused replies during teardown](../RefusedRepliesDuringTeardown) — the routing-side seam
  (`IUndeliverableReplySink`) that catches a verdict the transport cannot forward.
- [Writing tests](../WritingTests) — wait on the condition, never on a delay. A teardown-phase
  verdict is one more condition that must be waited on causally.
