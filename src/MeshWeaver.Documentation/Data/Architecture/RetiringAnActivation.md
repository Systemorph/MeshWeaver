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

- the teardown's drain answers *"Hub X was disposed while &lt;message&gt; was still deferred — the
  message was never processed"*;
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

## Reading a refusal in the field

A retired activation's refusal is `ErrorType.ShuttingDown` and its message is built by
`ShutdownNack.RetryForTheAuthoritativeAnswer`, so `ShutdownNack.IsAnsweredByOwner(message, address)`
is true and the sentence names the fault:

```
Hub host/1 is shutting down — its DataContext initialization met a transient infrastructure fault
(NpgsqlException: Failed to connect to …) and this activation is retired. The address may
reactivate (recycle / restart); retry to get the authoritative answer.
```

If instead you read *"…was disposed while X was still deferred … the message was never processed"*,
the retirement lost its own race: something failed the gate after the teardown had begun.

## Related

- [Initialization Gates](/Doc/Architecture/InitializationGates) — declaring, opening and bypassing a gate.
- [Hub Disposal Model](/Doc/Architecture/HubDisposalModel) — the phases `Dispose()` posts and what runs in each.
- [Teardown Layers](/Doc/Architecture/TeardownLayers) — teardown lets accepted work finish; it must stop accepting work it cannot finish.
- [Refused Replies During Teardown](/Doc/Architecture/RefusedRepliesDuringTeardown) — the intake gate's two tiers and what a `Quiescing` hub still admits.
- [Error Propagation and Wedges](/Doc/Architecture/ErrorPropagationAndWedges) — why a silent abandonment costs the caller its whole budget.
