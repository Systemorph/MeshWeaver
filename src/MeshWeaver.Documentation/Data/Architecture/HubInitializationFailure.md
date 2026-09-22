# Hub initialization failure — fail gracefully, never wedge

A message hub initializes by running its **BuildupActions** (the observables registered via
`WithInitialization(...)`) and then opening the **`Initialize` gate**. Until that gate opens, every
message targeted at the hub is **deferred** (held in the deferred queue). The gate opening is what
lets the hub start processing real traffic.

## The failure mode this guards against

If a BuildupAction **throws**, the naive composition propagates the error out of the
`Observable.Concat`, so the step that calls `OpenGate(Initialize)` **never runs**. The gate stays
closed *forever*. Every subsequent message then sits in the deferred queue until the
**30-second deferral timeout** (`MessageService.deferralTimeout`, per hub off
`WithDeferralTimeout`, defaulting to 30 s) fires a generic `DeliveryFailure` —
*"deferred >30s; initialization gates closed at deferral: […] — gate(s) […] are STILL closed"*.
That answer names the gates **recorded when the delivery was parked**, and states separately which
of them are still shut: the two readings are different diagnoses, and re-deriving the first from
the live gate set is the defect in
[A Failure Report Answers Its Own Instruction](../AFailureReportAnswersItsOwnInstruction).

To a user this is an **unrecoverable wedge**: the node is reachable (HTTP 200) but every interaction
times out at 30s, and the GUI shows a useless "Area unavailable — did not become addressable after N
retries." The root error (what actually went wrong in init) is **invisible**.

> **Production incident (2026-06-16).** Selecting an agent in the chat composer triggered a
> hub whose init threw. The `AgenticPension` grain's action block was stuck behind the closed gate,
> so every `DeliverMessage` to it timed out at 30s and the whole node went dark.

## The rule: a faulting init must FAIL GRACEFULLY

A hub whose initialization throws must:

1. **Record the failure as status.** `MessageHub.InitializationError` is set to the init exception.
   The hub stays `RunLevel.Started` (it is *not* a new run level — the lifecycle enum is strictly
   ordered) but is now in a FAILED state.
2. **Still react to messages.** The `Initialize` gate is opened **anyway**, so the hub can answer
   traffic and be torn down. A closed gate is the wedge; an open gate on a failed hub is recoverable.
3. **Refuse requests with a proper status.** Every non-lifecycle request is answered immediately with
   a typed `DeliveryFailure { ErrorType = ErrorType.Failed, Message = "Hub '<addr>' initialization
   failed: <reason>" }`. Callers get a `DeliveryFailureException` carrying the *real* reason — FAST,
   not a 30s timeout.
4. **Let lifecycle/control traffic through.** `DeliveryFailure`, `ShutdownRequest`, `DisposeRequest`,
   `InitializeHubRequest`, `HeartBeatEvent` are **not** refused — disposal must still work, keep-alive
   must not deactivate the grain, and a `DeliveryFailure` must never beget another (storm). This is
   the same bypass set `MessageService` applies at the gate.

## A TRANSIENT infrastructure fault retires the activation instead — #4067 / #4068

The FAILED state is the right outcome for a fault that belongs to the activation: a NodeType that
does not compile, a handler that throws, a data source that is misconfigured. No retry would
change it, and a terminal answer is honest. It is the wrong outcome in kind for a **transient
infrastructure fault** — the database unreachable, a host name that did not resolve, a
connection attempt that timed out. Nothing about the activation is broken; the dependency was
away for a moment. Latching the activation for it turned a two-minute DNS blip into "this address
is broken until the process restarts": every later request answered with a terminal
`DeliveryFailure`, the data streams errored, nothing ever re-run.

Measured: memex-cloud, 2026-09-12 06:19–06:21Z — three `DataContext` initializations failed on
`SocketException: Name or service not known` inside `NpgsqlConnector.ConnectAsync` and stayed
FAILED (#4068); memex, 2026-08-23 → 2026-09-12 — 19 `sync/*` BuildupActions failed on
`NpgsqlException: Failed to connect … ---> TimeoutException` across four pods, each leaving its
hub refusing (#4067).

### The rule

**A hub that demand routing re-creates does not latch a transient fault — it retires.** Both
seams — `MessageHub.HandleInitialize` (a BuildupAction faulted) and
`DataContext.SettleInitializationGate` (a data source's initial load faulted) — ask
`InfrastructureFault.IsTransient(ex)` first, and for a hub declared
`WithReactivationOnDemand()`:

1. `Dispose()` — FIRST, so every refusal that follows is classified
   `ErrorType.ShuttingDown` (the reporters read `IsShuttingDown`) and carries this activation's
   identity;
2. `FailGate(gate, reason)` — SECOND, so whatever is parked behind the init gate is answered NOW
   with the SPECIFIC cause in the owner's refusal vocabulary (`Hub X is shutting down — its
   initialization met a transient infrastructure fault (NpgsqlException: …) and this activation
   is retired. The address may reactivate; retry to get the authoritative answer.`) rather than
   the teardown's generic "Hub is shutting down" later;
3. **no `InitializationError`, no rejection handler, no errored streams** — the marker describes
   an activation that stays, and this one is going away.

The next delivery to the address activates a fresh hub whose initialization runs again against
the dependency that has come back. That is the same contract every recycle already carries, and
every caller already rides it out: the paced re-probe in `GetMeshNodeOutcome`, the
resubscribe latch in `SynchronizationStream`, the `OwnerDisposing` re-enqueue on the write path.
Under a sustained outage each demand-driven activation pays one connection timeout and is
retired — no latch, and no storm either, because the caller's re-probe is paced and bounded and
ends in `AddressRecyclingException`, which is the honest answer while the database is away.

`WithReactivationOnDemand()` is declared in the ONE funnel every per-node activation passes,
`NodeTypeRebindWatcher.WithNodeTypeRebind` (Monolith routing and `MessageHubGrain` alike).

### What is transient

`InfrastructureFault.IsTransient` walks the inner-exception chain (so a reflective wrapper or an
"initialization failed" wrapper does not hide the cause) for a `System.Data.Common.DbException`
whose own `IsTransient` says so — every ADO.NET provider classifies its connection failures and
timeouts there; Npgsql sets it for exactly the two shapes measured, with no provider reference
needed in core — or a bare `SocketException`. A `TimeoutException` on its own is NOT transient:
the init time-box mints one for a hang, and a hang is a defect. A provider that leaves
`IsTransient` false has made its own classification, which is honoured.

🚨 **An `AggregateException` is transient only when EVERY branch is.** A `DataContext` initialises
its data sources under `Task.WhenAll`, so one aggregate can carry a connection timeout from one
source and a genuine defect from another; reading "any branch transient" as transient would
retire the activation, discard the defect, and re-run the same failing initialization on every
reactivation — a latch traded for a loop. A mixed aggregate keeps the latch, whose recorded error
still carries the transient branch. The full corpus — both incident shapes, the wrappers, the bare
`TimeoutException`, the non-transient provider fault, all-transient / mixed / nested / empty
aggregates, a cyclic chain — is `InfrastructureFaultTest`.

### What keeps the latch, and why

A hub **without** `WithReactivationOnDemand()` keeps the FAILED latch on the same fault, and its
log line now says so explicitly (*"The cause is a TRANSIENT infrastructure fault, but this hub is
not re-created on demand … so it stays FAILED until it is recycled or the process restarts"*):

- **the root mesh hub** — built once for the process, disposed only by the host; retiring it
  would not bring it back. A root whose DataContext cannot reach the store at boot is a
  process-level condition, and the recovery is the process (readiness, restart). The three
  `mesh/…` ids in #4068 are three boots of one pod, i.e. exactly that recovery happening.
- **a hub owned by a live object** — a synchronization stream's `sync/*` sub-hub is re-created
  by the stream's owner's own recovery (a faulted cache entry is evicted on the next read and
  re-created), not by routing. Retiring it from underneath the stream would replace a terminal
  fault its owner already handles with a completion its owner does not.

Pinned by `MeshWeaver.Data.Test.TransientInitializationFaultRetiresTheActivationTest` — both
seams, plus the control without the declaration. Falsified by reverting the two seams: the
positive cases then fail with `ErrorType.Failed` (the latch) while the controls stay green.

## Where it lives

`MessageHub.HandleInitialize` wraps the BuildupAction composition in a **liveness bound plus** a
single high-level `.Catch`:

```csharp
return Observable
    .Concat(actions.Select(a => a(this).DefaultIfEmpty(Unit.Default).Take(1)))
    .ToList()
    // 🚫 A BuildupAction that HANGS raises no exception, so convert "never completed within
    //    the budget" into a TimeoutException the SAME .Catch handles.
    .Timeout(Configuration.NestedInitializationBudget)   // rung 2 of the ladder, never a constant
    .Select(_ => { OpenGate(MessageHubConfiguration.InitializeGateName); return request.Processed(); })
    .Catch((Exception ex) =>
    {
        if (IsShuttingDown || this.IsTerminatedByScopeTeardown(ex))
            …                                                   // a recognised shutdown, no failure state
        if (InfrastructureFault.IsTransient(ex) && TryRetireAfterTransientInitializationFault(ex))
            return Observable.Return(request.Processed());      // retired, not latched — see below
        var reason = ex is TimeoutException
            ? $"BuildupAction {DescribeBuildupAction(actions, pendingAction)} did not complete within …s — …"
            : $"BuildupAction {DescribeBuildupAction(actions, pendingAction)} faulted ({ex.GetType().Name}: {ex.Message})";
        EnterInitializationFailedState(new InvalidOperationException(reason, ex));
        OpenGate(MessageHubConfiguration.InitializeGateName);   // ALWAYS open — a closed gate is the wedge
        return Observable.Return(request.Failed($"Hub '{Address}' initialization failed — {reason}"));
    });
```

`EnterInitializationFailedState` sets `InitializationError` and registers a front-of-chain rule that
refuses every non-lifecycle request with the typed `DeliveryFailure`.

This **generalizes** the per-context guard that already lived in `DataContext.OpenInitializationGate`
(which opens its own `DataContextInit` gate even on fault) up to the hub level, so **every**
BuildupAction — not just the DataContext one — fails gracefully.

## 🚨 A hub's init runs BEFORE its creator's constructor has returned

`MessageHubConfiguration.Build` ends with `StartMessageProcessing()`, and that method **posts
`InitializeHubRequest`**. So a hub is already draining its own init turn — on its own turn
scheduler, i.e. another thread — while the code that asked for it is still inside
`GetHostedHub(...)`.

Anything the BuildupActions reach back into must therefore be **fully bound before `Build`
starts message processing**, not on the creator's return path. There are exactly two safe
places:

| Where | Runs | Use it for |
|---|---|---|
| a **synchronous** `WithInitialization(Action<IMessageHub>)` | inside `Build`, *before* `StartMessageProcessing` | binding the creator's own fields onto the new hub |
| the creator, after `WithDeferredInitialization()` + an explicit `Post(new InitializeHubRequest())` | whenever the creator says so | when the creator cannot finish before `Build` (e.g. `LayoutAreaHost`, whose init lambda reads a property assigned after the constructor returns) |

**Assigning on the return path is a race, and it fails SILENTLY.** `SynchronizationStream`'s
constructor used to do exactly that:

```csharp
var syncHub = Host.GetHostedHub(SynchronizationAddress.Create(ClientId), ConfigureSynchronizationHub, …);
…
Hub = syncHub;              // ← too late: the sub-hub's init may already have faulted
```

A data source whose initial load faults *synchronously* (`Observable.Throw`) reaches
`SynchronizationStream.OnError` inside that window. `OnError`'s `if (Hub is not null)` guard then
skipped **both** `Hub.FailStartup(error)` and `Hub.OpenGate(SynchronizationGate)` — so:

* `SynchronizationGate` never opened ⇒ the sub-hub never reached `Started`;
* `Hub.Started` never settled ⇒ `IDataSource.Initialized` (a `WhenAll` over those tasks) hung;
* `DataContext`'s `DataContextInit` gate was never given its answer ⇒ **every** request to the
  owning hub deferred until an unrelated deadline expired.

The tell is a log that says the hub failed and then goes quiet: `sync/… initialization failed —
a BuildupAction faulted` at ~4 ms, followed by **no** `DataContext initialization failed for …`
and a caller that waits out its whole budget. That was CI flake
[#2625](https://github.com/Systemorph/MeshWeaver/issues/2625), unreproducible in 25 local runs
because the window is a few instructions wide and only CI-shard thread pressure lands in it.

The fix is the first row of the table: `ConfigureSynchronizationHub` now starts with
`.WithInitialization(BindHub)`, so `Hub` is bound on the constructing thread before the sub-hub
can process anything. The same window was a latent `NullReferenceException` on the *success*
path too — `Initialize`'s `SetCurrent(hub, new ChangeItem<TStream>(init, StreamId, OwnerVersion()))`
reads `Hub.Version` for an owner-side stream.

**The general rule: a "not yet available, skip it" guard on an initialization path is a wedge,
never a no-op.** Skipping `FailStartup` does not degrade the failure — it converts a fast, typed
rejection into an unbounded wait with nothing logged. Where such a guard must stay (here: a
stream whose constructor refused before binding a hub), it logs at **Error** and names what will
never be settled.

### How to reproduce an ordering like this deterministically

`HostedHubsCollection` publishes `HubAdded` **twice** per hosted hub, and the two emissions
straddle exactly this window:

1. from `HostedHubsCollection.Add`, called by `Build` *before* `SyncBuildupActions` and before
   `StartMessageProcessing` — nothing can have faulted yet;
2. from `GetHub`'s creation `Lazy`, called *after* `Build` returned (so after the init request was
   posted) and *before* `GetHostedHub` returns to the caller's constructor.

Subscribing on the second emission and holding that thread until the sub-hub records its
`InitializationError` pins the interleaving with no timing luck at all — see
`DataContextFaultedInitBeforeStreamHubBoundTest`. This beats a repeat-until-it-flakes loop: 25
runs at `DOTNET_PROCESSOR_COUNT=4` produced 0 failures, the parked run produces the defect every
time.

## How it shows on the GUI

Because the failure is now a `DeliveryFailure` flowing back through the subscriber rather than a silent
wedge, a layout-area subscription receives a `DeliveryFailureException` carrying
`"… initialization failed: <reason>"`. The area binding (`AreaErrorClassifier` / `NamedAreaView`)
renders that message instead of spinning to the generic "did not become addressable" timeout. The user
sees **what** broke.

## Hangs ARE covered — by the startup bound, not by the per-message deferral

A BuildupAction that **hangs** (never emits, never completes, never throws) used to leave the
`Concat` incomplete, so the gate never opened and every message wedged on the 30 s per-message
deferral timeout. That gap is closed: `.Timeout(Configuration.NestedInitializationBudget)`
converts "did not complete within the budget" into a
`TimeoutException` that the same `.Catch` turns into the FAILED state, and the resulting
`DeliveryFailure` names the hang explicitly rather than reporting a generic deferral — and it names
**which** action: *"BuildupAction 3 of 3 (DataExtensions.StartDataSourcesAndOpenGate) did not
complete within Ns — the actions before it had signalled; …"*. The position is the index the
sequential `Concat` was on when the bound fired, and the name is the method behind the delegate
(a method group names itself; a lambda names the compiler's closure method, which still identifies
the registration site). The sentence used to read *"a BuildupAction did not complete within Ns (a
hung dependency or stuck compile)"* — two candidates, neither measured, and no way to tell which of
the hub's actions was pending. Naming the pending action is the part this layer can say (issue #2886).

🚨 **The budget is a RUNG, not a constant** — `Configuration.NestedInitializationBudget`, one
step inside whatever bounds this hub, and every hub born INSIDE this one's initialization takes a
step inside that again
([The Initialization Budget Ladder](../InitializationBudgetLadder)). While every level was
independently written as the same 120 s, which level reported a hang was decided by scheduling: when
the enclosing one won it errored the inner hub's streams and that hub's initialization ended as a
recognised shutdown, recording nothing at all. A hub tightens its whole ladder via
`Configuration.StartupTimeout`.

**This bound is a liveness guarantee, not a fix** — it makes the failure observable, fast, and
attributable to the level nearest it. When it fires, go and fix the hung dependency; do not raise
the number.

What this still does NOT cover: a hub that initialised *successfully* and then hangs inside a
handler. That is an ordinary wedge — see
[ErrorPropagationAndWedges](../ErrorPropagationAndWedges).

## Resolving the node is a READ, and its two terminals mean different things

Before a per-node hub can be built, the grain has to learn WHICH node it is. That read has two
terminals and conflating them cost issue #1186 six weeks of triage.

| The activation source | Terminal | What it means |
|---|---|---|
| completes with no node | `InvalidOperationException` — *"No MeshNode resolvable for address …"* | **Determinate.** Storage answered, and the answer is "nothing here". Arrives as fast as storage answers. |
| goes silent | `TimeoutException` after `FirstNodeResolutionTimeout` (30 s) | **A stalled READ.** Says nothing whatever about the node. |

The second row used to carry the first row's sentence — *"Either the node does not exist or no query
provider claims its partition"* — because the 30 s window was once the only terminal for both. Once
the absent case got its own prompt terminal (`MessageHubGrain.ComposeActivationSource`: the
authoritative branch alone decides when the source is done), that sentence became false in every
case that could still reach the timer. It is what the incident fingerprint is built from
(`ActivationFaultReason` excludes the reporter's prose and keeps the exception's message), so the
false sentence is what a human reads and what a ticket gets titled after. **Measured on memex: 95 of
these faults in 400 minutes, on an instance whose path-resolution query fan-in was logging, seconds
earlier:**

> Query provider(s) [StorageAdapterMeshQueryProvider] have not emitted an Initial after 20s for
> query `path:… scope:subtree nodeType:AccessAssignment limit:2000` (user 'system-security') — the
> query is silently stalled on its all-providers Initial gate and its consumer hangs with no error.
> Fix the stalled provider; never bump the consumer's timeout.

Both lines were in the same log on the same pod. Only the one that guesses was ticketed.

### A FLOOR is not an answer, and path resolution must refuse it

`MeshQuery.MergeProviderObservables` gates its merged Initial on EVERY provider emitting one. A
provider that COMPLETES without an Initial is counted as empty — the alternative starved the gate
and hung every real-user search — and is NAMED on the frame in
`QueryResultChange.SilentProviders` for exactly one reason: *"nobody answered"* and *"there is
nothing there"* are different facts that otherwise arrive in the same shape.

`PathResolutionService` used to ignore that field, and a resolution is a statement about which node
is **deepest** at a path — which a provider that did not answer cannot be assumed to have had
nothing to say about. Two silent consequences followed:

1. nothing matched → the resolver answered `null` → the grain reported the absent verdict, its
   second clause manufactured out of a frame whose entire content is "nobody said";
2. only a shallower ANCESTOR matched → that is a POSITIVE resolution, so `ResolveSegments`
   **cached** it, and the path answered with its ancestor plus a remainder for the life of the
   process. Nothing refreshes it either: the one event that would is a `Created`/`Deleted` for that
   exact path, and a reconcile re-writing an unchanged node publishes neither.

The rule now: **answer from a floor only when the hit IS the full requested path** — nothing a silent
provider holds can be deeper than that — and otherwise fault, naming the providers that went silent.
An error caches nothing, so the refusal cannot outlive the request. Repro:
`test/MeshWeaver.Hosting.Test/PathResolutionFloorIsNotAnAnswerTest.cs`, which carries a case on each
side: a floor whose hit is the full path still resolves, and a COMPLETE empty snapshot still answers
absent.

### What this does NOT fix

A provider that neither emits, completes nor errors still starves the gate, and its consumer still
waits. The fan-in **detects** it (a 20 s stall probe naming the laggards) and deliberately only
logs; turning that probe into a terminal would change every query in the mesh and is reserved as a
platform decision — see [AccessControl](../AccessControl) → "Silence is not consent", where the
reasoning is spelled out and where delivering a floor as an answer to the permission fold would be a
security hole rather than a diagnosis. So the chain above converts a misattribution into an
attribution; the stalled provider is a separate fault and the warning names it.

## Test

`test/MeshWeaver.Messaging.Hub.Test/InitializationErrorSurfacedTest.cs` pins the contract: a hub with a
faulting BuildupAction answers a probe request with a `DeliveryFailureException` carrying
`"initialization failed: <reason>"` **fast** (a `TimeoutException` would mean the gate never opened —
the regression), and exposes the `InitializationError` status marker.

`test/MeshWeaver.Data.Test/DataContextInitWatchdogTest.cs` pins the DataContext side — the
watchdog's four terminal outcomes, plus
`DataContextFaultedInitBeforeStreamHubBoundTest`, which stages the construction-window ordering
above deterministically and asserts that the faulted arm still settles the gate.

## Related

- [AsynchronousCalls](../AsynchronousCalls) — why init is reactive (`IObservable`, no `await`).
- [InitializationGates](../InitializationGates) — the gate model and the framework-bypassed messages.
- [DebuggingMessageFlow](../DebuggingMessageFlow) — diagnosing a hub that won't process messages.
- [AccessControl](../AccessControl) → "Silence is not consent" — why the query fan-in's stall
  probe is a diagnostic and not a terminal, and what a floor would do to the permission fold.
