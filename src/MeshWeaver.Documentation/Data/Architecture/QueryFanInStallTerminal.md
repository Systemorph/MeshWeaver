# The query fan-in's Initial gate is a bound, not a wait

`MeshQuery.MergeProviderObservables` is the one fan-in every mesh query goes through. It subscribes
every registered `IMeshQueryProvider` and emits ONE merged `Initial` frame — the union of their
slices — and it gates that frame on **every** provider having delivered one. It has to: a missing
slice and an empty slice are indistinguishable from the merge's side, so answering before every
provider has spoken would be answering with a result set nobody assembled.

That gate had three possible provider terminals and only two of them were represented.

| the provider… | what the merge does | how it is reported |
|---|---|---|
| emits an `Initial` | unions its slice | the merged frame |
| **completes** without one | counts it empty so the merge proceeds | `QueryResultChange.SilentProviders` names it on the frame (#4557), and `MeshNodeStreamCache` refuses to keep that frame as the cached answer |
| **neither emits, completes nor errors** | *used to wait for ever* | **now** `QueryProviderStalledException`, naming the providers that did not answer |

The third row is what this page is about. Policy
[`query-fanin-stall-terminal`](../PolicyNotProse).

## What the hang actually cost

A stalled provider produced a consumer that hung with **no error, no consumer-side log line and
nothing to grep**. The only trace was a warning from `MeshQuery`'s own `InitialStallProbe`, which
detected the condition at 20 s and deliberately only logged it. Measured on `memex` over the 400
minutes to 2026-09-21T04:12Z: **200+ of those warnings**, alongside 95 `No MeshNode emitted for`
faults (~14/hour) — and nothing acted on any of them, because a warning is not a terminal and the
consumers that could have acted were the ones parked.

Earlier instances of the same shape, from the code that documents them: CI 2026-07-21, where
`ExportImportAccessControlTest` was watchdog-killed at 60 s with a flat heap and not one log line;
and issue #1446, where a `CreateNodeRequest` sat `Executing` for 33 s until its *caller's*
`RequestTimeout` ended it — naming the caller's impatience rather than the read that starved.

## Why a warning could not simply become "count it empty"

The fan-in already has a permissive shape for an unanswered provider — count it empty and name it on
the frame — and reaching for that here would have been a hole rather than a diagnosis. The argument
is [Access Control](../AccessControl) → "The convergence contract": `ObserveScopePolicies`'
contribution is **subtractive** (`PermissionCap`, `BreaksInheritance`, and their *absence widens*),
so an empty policy snapshot handed to the permission fold is a verdict that ignores every runtime
cap. `ObserveEffectiveAssignments` and `ObserveAllMembershipNodes` are the same. There is **no
permissive seed that is not a hole and no conservative seed that is not a spurious denial**, so the
only sound terminal for a starving read is an **error**.

## Why the consumers needed no change

Because the seeding rule above had already forced them to classify a fault correctly. The terminal is
the input they were built for and never received:

- **`CheckPermissionOutcome`** → `PermissionCheckOutcome.Undetermined`, whose `IsGranted` is `false`,
  reported on the bus as `ErrorType.Unavailable` — retryable, and never "Access denied" (#974, #2742).
- **`RlsNodeValidator`** → `NodeRejectionReason.Unavailable`; the write does not proceed and the
  message says the check *could not be established*, not that access was refused (#1446).
- **`AnonymousGate`** → `Undetermined`, warned once naming the path; `AllowAnonymous` projects to
  `false`.
- **`MeshNodeStreamCache.EvictFaultedQuery`** → drops the `(id, query set)` chain, so a `Replay(1)`
  cannot replay one terminal to every later subscriber for the life of the process (#1316).
- **`PartitionOwningTypes`** → already `.Timeout(→ null).Catch(→ null)`, a tri-state every caller
  fails closed on. Its own 10 s bound fires *before* the fan-in's, so its behaviour is unchanged.

## The budget is rung 4, and deriving it was not optional

The probe's diagnostic delay was a hard-coded **20 s** — *exactly*
`MeshOperationOptions.PermissionEstablishmentBudget` at the production default. Promoting that
constant to a terminal as-is would have recreated issue #1198's defect precisely: equal is not an
ordering, the outer clock starts first, so the fan-in could never win and every stalled read would
still have been reported as "the check could not be established" with no provider named.

`MeshOperationOptions.QueryInitialBudget` is therefore `Nest(PermissionEstablishmentBudget)` — one
configured value, every rung derived, strictly contracting, the collision unrepresentable:

| rung | what it bounds | default |
|---|---|---|
| `Timeout` | the mesh operation, as its caller bounds it | 30 s |
| `NestedTimeout` | a handler running inside one of that operation's stages | 25 s |
| `PermissionEstablishmentBudget` | one authorization fold inside such a handler | 20 s |
| **`QueryInitialBudget`** | **one query fan-in's Initial, inside such a fold** | **15 s** |

15 s is roughly twice the observed healthy worst case for a cold provider under suite load
(single-digit seconds). A false positive is self-correcting and cheap — the answer is a retryable
availability failure and the cache re-probes on the next read; a false negative is the hang, and is
neither.

🚨 **The ladder reaches the unsecured surface only because it is passed explicitly.** The
`IMeshQueryCore` registration constructs `MeshQuery` with a **null hub**, so the fan-in's own lazy
`hub.ServiceProvider` lookup cannot see a configured `WithMeshOperationTimeout` — and the security
fold reads through exactly that registration. `PersistenceExtensions` therefore hands it
`sp.GetService<MeshOperationOptions>()`. Without that the innermost rung would have been the one value
nobody could configure, which no test would have noticed.

## The exception is deliberately not a `TimeoutException`

`QueryProviderStalledException` lives in `MeshWeaver.Data.Contract` — the same assembly, and for the
same reason, as `StorageFaults`: the layers that must recognise it sit on opposite sides of the
assembly graph (`MeshWeaver.Hosting` raises it, `MeshWeaver.Graph` names it, `MeshWeaver.Layout`
classifies it, and `MeshWeaver.Mesh.Contract` *references* `MeshWeaver.Layout`).

It does not derive from `TimeoutException`, and that is a decision rather than an omission. Several
catch arms in the tree match that type to mean "MY OWN bound elapsed" and then print their own budget
(`MeshOperations`, `MeshNodeCompilationService`, `BuildProtocolDriver`); inheriting it would hand them
a fault they would re-attribute to themselves — the exact misattribution
`StreamPostGuardTimeoutException` was minted to prevent, in reverse. It also keeps the stall out of
`TransientStorageFaults.RetryTransientConnect`'s class and out of
`AreaErrorClassifier.IsTransientHubFailure`'s bounded retry: **a stalled provider must be fixed, never
retried behind the caller's back.**

What *does* classify it is `AreaErrorClassifier.IsStorageUnavailable`, so a render that hits one shows
the host's **localized** "temporarily unavailable, worth re-opening" frame instead of a generic panel
carrying a framework sentence and provider class names at an end user.

## Where the terminal is armed, and what it holds

A terminal has to REACH the observer, so the timer's callback necessarily holds the merge's fault sink
— and transitively the observer chain and its hub — while it is armed. `InitialStallProbe.MarkSeen`
therefore disposes the arm the instant the last provider's `Initial` lands, so the rooting window is
exactly the window in which the query has no answer.

This makes the `TimerQueue` root shape that
[Debugging Disposal and Leaks](../DebuggingDisposalAndLeaks) hunts **shorter-lived than before**, not
newly introduced:

- a **healthy** query now roots nothing for the budget, where the old log-only probe stayed armed for
  its full delay;
- a **stalled** query's observer chain was already rooted *for ever* by the provider's own pending
  subscription, and firing releases both.

## Four consumers that needed work anyway

Making the stall terminal turned four latent defects from unreachable into routine. None is an open
door; each turned a hang into something worse, so each is fixed in the same change.

1. **`MeshDataSource.HandleRunTests`** had a one-arm `.Subscribe(onNext)` directly on the fan-in. Rx
   hands an unhandled `OnError` to its default handler, which **rethrows on the delivering pool
   thread** — an unhandled exception *and* a `RunTestsRequest` that never gets a response. It now
   answers the caller with the failure.
2. **`UserIdentityCache`** — the portal's user directory — opened its read once in the constructor over
   a `Publish()`. One terminal set its failure reason for the life of the **process**: every `Lookup`
   answered `Unavailable`, every `WhenDetermined` waited on a snapshot that could never arrive, and
   only a pod restart cleared it. This was the one latch on the fan-in with **no repair path at all**.
   The chain is now re-openable and `IndexChanged` is a stable façade over an instance `Subject`, so a
   terminal is not replayed to later waiters. Nothing re-subscribes on its own — no timer, no poller:
   the dead chain is dropped and the **next caller** re-opens it, which on a live portal is the next
   request or circuit start, and that chain's first snapshot un-parks whoever was already waiting.
   Same discipline as `EvictFaultedQuery`, one class over.
3. **`EventSubscriptionRunner.WatchTriggerNodeType`** keyed its one-watch-per-node-type guard on a
   dictionary entry it never removed on a terminal, so a faulted watch was **dead for the life of the
   runner** — silently stranding the deferred invite/grant reconcile (a user onboards and gets no
   access). The entry is now dropped pair-exact, so the next emission that needs the watch rebuilds it.
4. **`MeshNodeBindingExtensions.Exists`** promised to "stay subscribed so a late answer still lands".
   Its `ReadBudget` (10 s) still degrades to `false` and draws the control empty *first*; past the
   fan-in's wider bound the read faults, and that fault is deliberately **not** caught — a
   `.Catch(→ false)` would turn "nobody can answer this" into the value a real absence produces. The
   doc comment now says so.

## The four fail-open consumers, and why the terminal does not reach them

Four consumers in `src/` answer a fault with a MORE permissive value than silence. All four already
reach that value through **their own bound, which is strictly shorter than rung 4**, so the terminal
changes nothing for them — but the relationship is worth stating, because it is what makes "no new
open door" a checkable claim rather than a hope.

| consumer | permissive answer | its own bound |
|---|---|---|
| `CreatableTypesCreationValidator` | `NodeValidationResult.Valid()` — the create proceeds (a **recorded** fail-open decision; `Permission.Create` is the real gate) | 10 s |
| `SpaceAdminInvariantValidator` | `Valid()` — last-admin removal not blocked | 10 s |
| `PluginSurfaceProbe.Exists` | `false` — "not here" | 800 ms |
| `NotificationService.HasRoutingRules` | `false` — send now rather than defer to triage | its `LookupTimeout` |

A deployment that configures `MeshOperationOptions.Timeout` low enough to drive rung 4 under 10 s puts
the first two back in the fan-in's reach — but a fault there is already their documented answer, so
the direction is unchanged.

## The dependent half (MeshWeaver.Plugins)

This is **break shape 7** — behaviour changing behind an unchanged signature — so no gate sees it, and
Plugins carries **no core pin any more**: every run resolves the newest *sealed* `main-cd` set, so this
reaches it on the next seal with nothing to co-ordinate. Swept read-only; what the sweep found, so the
next session does not have to re-derive it.

**Compatible already.** No custom `IPermissionEvaluator`, no `ObserveScopePolicies` call site, no
`RlsNodeValidator` override, no `NodeRejectionReason.Unavailable` use, and no `Subscribe(onNext)`
without an `onError` arm on a mesh-query chain. No Plugins test asserts that a query must hang — every
"hang" test asserts the opposite. `ContentGateUndeterminedTest` already requires a faulted *and* a
silent evaluator to yield **503, not 404**, and `PermissionSwallowRatchetGuard` fails the build on
`CheckPermission(...).Catch(→ verdict)`.

**What the Plugins half is:**

1. `Hosting.Monolith.Test/StarvedPermissionReadTest` is written *around* the old behaviour — its class
   doc states the leg "never errors … so it can only STALL" and contrasts it with a throwing provider
   that "fails fast today". Its assertions still hold (`UnestablishedCheck` produces "could not be
   established" on the fault arm too), but its prose is now wrong and its 20 s
   `MeshOperationOptions.Timeout` contracts to a 5 s rung, so its elapsed expectations want revisiting.
2. `CompileSourceSnapshotWedgeTest` becomes timing-sensitive: its `Release()` may land after the
   terminal has already ended the subscriptions it means to flush.
3. `Store/Core/Source/MeshQueries.cs` documents relying on the hang — *"waiting writes nothing,
   guessing rewrote everything"*. The invariant it protects **survives** (an error writes nothing
   either), but the paragraph describes a contract that no longer exists.
4. `Store/Catalog/Source/StoreCatalogLayoutAreas.cs`'s `.Catch(→ empty / ViewerFacts.Anonymous)` on the
   entitlement reads will render a *confident wrong* answer — "Get" for a plugin the viewer has bought
   — where a stall used to show a spinner. This is the one place the terminal makes a user-visible
   statement worse rather than better, and it is a Plugins-side decision.
5. `LogIncidentControlPlane`'s unbounded incident watch now retries once a minute against a
   permanently stalled provider and escalates at five — new, bounded, visible noise.

## What is pinned, and what is not

- **`QueryFanInStallIsTerminalTest`** (`test/MeshWeaver.Hosting.Test`) — a stalled provider faults and
  names itself; the **control** is the same merge with the same budget where both providers answer and
  nothing faults, held live *past* the budget so the arm-release is what is measured. Plus the
  single-provider branch both ways, the "completes without an Initial is named, not faulted"
  separation, and the ladder's strict contraction at three configured values.
- **`StalledPolicyReadFailsClosedTest`** (`test/MeshWeaver.Graph.Test`) — one user, one role, two
  partitions, and a provider that stalls one partition's `_Policy` read on a real monolith mesh: the
  control partition still **allows**, and the stalled one answers `Undetermined` with the provider
  named, for a caller who holds Admin there. Its negative control is that with the terminal disarmed
  the stalled assertion does not fail on a value — it never arrives at all.

**Not established.** Nothing here was exercised against PostgreSQL or a multi-silo Orleans mesh; both
tests run on an in-memory monolith, so what is measured is the fan-in, the fold and the classifiers,
not a real partitioned provider's timing. The 15 s default is calibrated against the healthy worst
case *recorded in the probe's own comment*, not against a fresh measurement of the deployed fleet.

## See also

- [Access Control](../AccessControl) → "The fold can produce NO answer, and that is a third outcome",
  "The convergence contract", "The stall is now that error"
- [Hub Initialization Failure](../HubInitializationFailure) → "Resolving the node is a READ" (#1186 —
  the consumer-side half: a floor snapshot is not a resolution)
- [Policy Not Prose](../PolicyNotProse) → `query-fanin-stall-terminal`
- [CQRS and Content Access](../CqrsAndContentAccess) — what a query may and may not be asked
