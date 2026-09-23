---
Name: Retiring an Activation
Category: Architecture
Description: Why a hub that retires itself must fail its gate BEFORE it disposes — the classification has to be stated, not derived, or two drains race for the same deferred backlog and the caller loses the cause.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12a9 9 0 1 0 3-6.7L3 8"/><path d="M3 3v5h5"/></svg>
---

# Retiring an Activation

> **TL;DR** — A hub whose initialization met a **transient** infrastructure fault does not latch
> itself FAILED; it **retires** so demand routing re-creates it. Retiring means two things, in this
> order: **`FailGate(name, cause, ErrorType.ShuttingDown)` first**, then `Dispose()`. The order is
> not a style choice — reversed, the teardown's own drain races the gate failure for the same
> deferred backlog, and the caller is told "the message was never processed" instead of naming the
> database. Stating the `ErrorType` is what makes failing the gate first *possible*.

---

## The shape

Two sites retire an activation, one per initialization seam:

| Seam | Site | Runs on |
|---|---|---|
| A `BuildupAction` faulted | `MessageHub.TryRetireAfterTransientInitializationFault` | the hub's own action block (inside the `InitializeHubRequest` turn) |
| A data source's initial load faulted | `DataContext.SettleInitializationGate` | the **thread pool** (`OpenInitializationGate` ends with `ObserveOn(TaskPoolScheduler.Default)`) |

Both do the same two things: answer everything parked behind the initialization gate with the
**cause** ("ask again — the database was unreachable"), and dispose so the next delivery activates a
fresh hub against a dependency that has come back.

## Why the order is load-bearing

`Dispose()` does not tear the hub down. It **posts** a `ShutdownRequest` and returns; the teardown
runs later, inside that request's turn, on the hub's action block, and reaches
`MessageService.Dispose()` at the `ShutDown` phase. `FailGate` runs **synchronously on the calling
thread**.

So on the `DataContext` seam the two live on different threads, and both of them drain the *same*
deferred backlog through `MessageService.DrainDeferredDeliveries`:

- the teardown's drain answers *"Hub X is shutting down (…) — &lt;message&gt; was still deferred;
  initialization gates closed at deferral: […] — the message was never processed"*;
- `FailGate` → `FailDeferredBacklog` answers with the transient cause.

`DrainDeferredDeliveries` claims each entry with `TryRemove`, so exactly one of them answers any
given delivery — and **whichever arrives first decides what the requester is told**. Measured on a
failing run (#4261): the retirement's warning at `06:18:12.542` and `[DISPOSE-DISCARD]` at
`06:18:12.543`. One millisecond.

### And the loss is total, not partial

`MessageService.Dispose()` **opens every remaining gate** on its way to the drain. A `FailGate` that
arrives after the teardown therefore finds no gate to fail: it returns `false` having recorded
nothing at all. The cause is not merely outraced — it is discarded, and the marker that would have
answered *later* deferrals with it is never written either.

## Why the reversed order was there, and what replaced it

Both sites used to call `Dispose()` first, and both carried a comment stating that as a requirement.
The reason was real: `AnswerUnreleasableDelivery` derived the refusal's classification at **drain
time**, from `hub.IsShuttingDown`:

```csharp
ReportFailure(delivery.WithProperty("Error", reason),
    hub.IsShuttingDown ? ErrorType.ShuttingDown : ErrorType.Failed);
```

A backlog answered before the teardown started came out `ErrorType.Failed` — terminal, the exact
verdict a retirement exists to avoid. So the teardown had to be under way for the answer to be
right, and the ordering the comments demanded could not be enforced, because ordering two calls on
two threads is not something a comment can do.

The repair is not to order them harder. It is to **stop deriving the classification from teardown
progress**: the `ErrorType` is a property of *why the gate died*, not of *how far the hub's shutdown
has got*, so it travels with the reason.

```csharp
// The form every in-tree caller uses.
hub.FailGate(gateName, cause, ErrorType.ShuttingDown);
hub.Dispose();
```

`MessageService` stores `failedGates[name] = (reason, errorType)`, and both sites that answer from a
dead gate — the immediate backlog drain and the later-deferral path — use the stored value. The
two-argument `FailGate` still derives, so nothing outside the tree changes behaviour; it is simply
never the right call for a retirement.

## What this does NOT change

- **Teardown still lets accepted work finish.** Nothing here cancels anything; the backlog is
  *answered*, which is what it was always owed.
- **No bound moved.** The quiesce budget, the deferral timeout and the disposal stall detector are
  untouched.
- **The generic disposal drain stays.** It is the right answer for a delivery abandoned by an
  ordinary teardown — one that has no more specific cause to offer. A retirement does have one, and
  now gets to say it first.

## How it is pinned

| Test | What it holds |
|---|---|
| `RetirementNamesTheCauseWhenTheTeardownWinsTest` (`MeshWeaver.Data.Test`) | The discriminator. At the first instant of the teardown (`IMessageHub.ShuttingDown`, which fires synchronously inside `Dispose()`), the victim's own `deferred=` count must already be **0**. With the two statements swapped back it reads `1`, every time — verified as an A/B, with a positive control asserting the backlog was non-empty before the retirement ran. |
| `FailedGateAnswersBeforeTheTeardownTest.AfterTheTeardown_FailGateCanNoLongerNameTheCause` | The loss, directly: after `DisposalCompleted`, `FailGate` returns `false` and the requester keeps the generic sentence. |
| `…FailingTheGateFirst_TheRequesterLearnsTheCause` | The contract, asserted **before** `Dispose()` is called at all. |
| `…TheClassificationIsStatedByTheFailer_NotDerivedFromTheRunLevel` + `…WithoutAStatedClassification_TheSameRefusalComesOutTerminal` | Why the stated form had to exist: the same refusal, unstated, comes out `ErrorType.Failed`. |
| `TransientInitializationFaultRetiresTheActivationTest` | The end-to-end contract (#4067/#4068), which was failing 3 runs in 36 on this race. |

## What is NOT a retirement — a teardown that lands during bring-up

This page covers ONE kind of teardown: a hub that retires itself because its own initialization
met a transient fault, and therefore has a gate-fault classification — the cause of the gate's
death, stated as an `ErrorType` — for `FailGate` to record ahead of the drain. It is not the rule for every hub that
goes down with work parked behind its gates, and reading it that way files the wrong issue
(#5274, #5424, #5426 each asked for a `FailGate`/drain before an external teardown).

Two teardowns routinely reach a hub whose `DataContextInit` / `MeshNodeInit` gates are still closed:

| Teardown | Who posts it | Attribution in `[DISPOSE-DISCARD]` |
|---|---|---|
| **Overlay self-heal** | `NodeTypeEnrichmentHelpers.WithOverlaySelfHeal` — the watcher armed in the overlaid hub's `WithInitialization`. Its first emission is a REPLAY, so when the type's build landed between enrichment and activation it posts the self-`DisposeRequest` before the gates open. | *"requested by itself … Overlay self-heal: the instance is bound to an overlay of NodeType '…'"* |
| **Orleans grain deactivation** | `MessageHubGrain` on a deactivation (e.g. `DirectoryFailure`, the directory owner's silo is shutting down), via `NoteDirectDisposalBy` + `Dispose()` | *"requested by Orleans deactivating grain …; why: …"* |

Both have a cause, and both already print it: `DisposalAttribution()` puts the teardown's WHO and
WHY into the `[DISPOSE-DISCARD]` line and into the NACK the sender reads (the table above). What
neither has is a separate **initialization-gate fault** — the gates did not die of anything, the hub
was taken down around them — so there is nothing for a `FailGate` to add to the attribution the
generic drain already carries. And neither can "drain first": the parked
deliveries are waiting for a bring-up that the teardown ends. That is the carve-out
[Teardown Layers](/Doc/Architecture/TeardownLayers) states under *Handler turns*: a hub that never
finished **starting** has its `InitializeHubRequest` cancelled, and whatever was parked behind its
gates is answered `ShuttingDown` and reported. The answer is composed through
`ShutdownNack.RetryForTheAuthoritativeAnswer`, so the transient classifiers re-probe
([Riding Out a ShuttingDown Address](/Doc/Architecture/RidingOutAShuttingDownAddress)) and the retry
activates a fresh hub — for the self-heal, one bound to the build that made the overlay obsolete.
Serving the parked work first would not be better: a compile-in-progress overlay answers typed
requests with a `CompilationInProgress` NACK anyway, and an error overlay with its fault card.

**It does not loop.** The self-heal is version-gated at the type version the overlay captured, so the
replay that fires it cannot fire the next activation's watcher; the only un-gated routes (the grace
and the re-read ladder) start at 45 s, and `OverlayHealBudget` spaces repeats across hub lifetimes.
`OverlaySelfHealWatcherTest` pins all three (`VersionGated_FiresExactlyOnce_…`,
`UsableBuildAtUnchangedVersion_HealsAfterTheGrace`, `NonConvergingInstance_RecyclesAreSpaced_…`).
A burst of discards at one instant on every replica after a NodeType publish is one recycle per
replica of a hot hub, not a loop — a loop would show the same hub discarding again ≥ 45 s later.

What is OPEN is the level: `MessageService` still logs a foreign sender's bring-up discard at
**Error** with the sentence *"Accepted work must be drained before a hub goes down"*. The Error was
justified by #4866's finding that the NACK was not recognised as retryable; that part is fixed, and
whether the level should now follow the same fact-based rule as the self-sender case (#4178) is a
maintainer decision, recorded on #5426.

## Reading a refusal in the field

A retired activation's refusal is `ErrorType.ShuttingDown` and its message is built by
`ShutdownNack.RetryForTheAuthoritativeAnswer`, so `ShutdownNack.IsAnsweredByOwner(message, address)`
is true and the sentence names the fault:

```
Hub host/1 is shutting down — its DataContext initialization met a transient infrastructure fault
(NpgsqlException: Failed to connect to …) and this activation is retired. The address may
reactivate (recycle / restart); retry to get the authoritative answer.
```

If instead you read *"…X was still deferred; initialization gates closed at deferral: […] — the
message was never processed"*, the retirement lost its own race: something failed the gate after the
teardown had begun.

🚨 **Tell them apart by the CLAUSE, never by the banner.** Since #4866 the teardown's drain composes
through `ShutdownNack.RetryForTheAuthoritativeAnswer` too — it was the last owner-side refusal that
did not, and the omission made it unrecognisable to every transient classifier, which is
[the defect that page records](/Doc/Architecture/RidingOutAShuttingDownAddress). So
`IsAnsweredByOwner` is now true of *both* answers, as it should be: both really are this owner
speaking. What separates them is what they say — a named fault, or a delivery that was still parked
behind its gates.

## Related

- [Initialization Gates](/Doc/Architecture/InitializationGates) — declaring, opening and bypassing a gate.
- [Hub Disposal Model](/Doc/Architecture/HubDisposalModel) — the phases `Dispose()` posts and what runs in each.
- [Teardown Layers](/Doc/Architecture/TeardownLayers) — teardown lets accepted work finish; it must stop accepting work it cannot finish.
- [Refused Replies During Teardown](/Doc/Architecture/RefusedRepliesDuringTeardown) — the intake gate's two tiers and what a `Quiescing` hub still admits.
- [Error Propagation and Wedges](/Doc/Architecture/ErrorPropagationAndWedges) — why a silent abandonment costs the caller its whole budget.
