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

   🚨 **The re-open window has exactly two ways to lie, and both are about identity** (found by review
   on the change that introduced it). `Classify` answers `Found(hit)` *before* it consults either
   flag — a hit outranks both, by design — so keeping the dead chain's rows serves a user deleted or
   renamed since the terminal, and *retaining the unavailable state does not close that*: the hit wins
   over it too. And clearing the failure before lowering the hydrated flag lets a reader see "healthy
   and authoritative" over the dead index, so a MISS becomes a definitive **Unknown** — "no such
   user", which is the false actionable verdict #974/#637 exist to prevent and the exact input that
   drives onboarding. The cure is `Apply`'s own Initial/Reset order reused verbatim — **down first,
   clear, then un-fail** — under which the worst a concurrent reader observes is `Unavailable`, which
   `UntilDetermined` turns back into a pending question. Pinned by
   `UserIndexReopenDoesNotServeADeadSnapshotTest`, whose control is that the released replacement
   chain does fill the index again: a permanent blackout would be the opposite failure and must not
   pass.
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

## The terminal reached a subscriber with no error arm — and killed the process (2026-09-23, #5650)

The "compatible already" sweep above said Plugins had **no `Subscribe(onNext)` without an `onError`
arm on a mesh-query chain**. That was wrong: it did not reach the `.razor` components. Two memex-cloud
replicas died of it — `…-wl8mv` at 17:57:09Z and `…-n7g6b` at 18:10:36Z — with `createdump` reporting
`Unwind: exception type MeshWeaver.Mesh.QueryProviderStalledException`. The runtime's stderr trace
(a `Logs` action filtered to `stream="stderr"`) is the same on both:

```
Unhandled exception. MeshWeaver.Mesh.QueryProviderStalledException: Query provider(s) [...] did not
  emit an Initial within the query fan-in's 15s bound for query
  'namespace:Admin/_Notification nodeType:Notification sort:CreatedAt-desc' (user '…')
   at System.Reactive.Stubs.<>c.<.cctor>b__2_1(Exception ex)          ← Rx's default onError: rethrow
   at System.Reactive.AnonymousSafeObserver`1.OnError(Exception error)
   at System.Reactive.Linq.ObservableImpl.Switch`1._.InnerObserver.OnError(...)
   at System.Reactive.Linq.ObservableImpl.CombineLatest`2._.SourceObserver.OnError(...)
   at MeshWeaver.Hosting.Persistence.Query.MeshQuery.<>c__DisplayClass26_4`1.<MergeProviderObservables>b__11(...)  MeshQuery.cs:886
   at MeshWeaver.Hosting.Persistence.Query.MeshQuery.InitialStallProbe...   ← the stall timer
   at System.Threading.TimerQueueTimer...
```

The query is the PLATFORM bell (`NotificationService.BellQuery(PlatformAddressee)`), and the chain is
`NotificationFeed.ForViewer` (MeshWeaver.Plugins, `MeshWeaver.Blazor.Portal`) —
`IsGlobalAdmin → Select(CombineLatest(one Query per bell leg)) → Switch` — subscribed by
`NotificationCenter.razor` and `NotificationCenterPanel.razor` with a single `onNext` argument. The
terminal did exactly what it is for: it faulted the one query that had no snapshot. Nothing owned the
fault, so Rx's default `onError` rethrew it on the stall timer's pool thread, where it was unhandled.

The terminal is correct and stays; the defect is the owner that was never wired. The bell is the
owner, so the bell takes the fault: it logs it and renders the bell as unavailable (a warning glyph
whose tooltip and accessible name are the localized `error.checkUnavailable`), and the panel says the
same. The fix is in MeshWeaver.Plugins, with a deterministic test that stalls a query provider and
asserts the bell's feed delivers the `QueryProviderStalledException` to its error arm.

**Why no guard saw it.** Core's `SubscribeErrorArmRatchetGuard` does select `.razor` files (its file
selection is `SourceScan`'s `.cs`/`.razor`/`.csx`, and its self-test now plants a bare subscription
in a `.razor` `@code` block and fails if the scan misses it) — but core holds no `.razor` files, and
it cannot see another repository. MeshWeaver.Plugins, which holds every Blazor component in the
fleet, had **no copy of the guard at all**. The follow-up sweep (MeshWeaver.Plugins, `Memex.Hosts.Test`
`SubscribeErrorArmRatchetGuard`) gives Plugins its own ratchet over `src/` and every module's in-mesh
`Source/`, `.razor` included, and converted the one-armed subscriptions it found; the platform
checkout that CI nests inside the Plugins workspace is deliberately outside its roots. Like the core
copy it counts only a visible lambda — a `Subscribe(MethodGroup)` is textually identical to
`Subscribe(observer)` and is not counted, so the sweep read those by hand.

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

- **`PluginBundleStalledReadTest`** (`test/Memex.Portal.Shared.Test`) — both plugin-bundle routes
  answer a stalled catalogue read with **503 + `Retry-After`**; its control is the same host with the
  provider answering, which serves 200.

**Not established.** Nothing here was exercised against PostgreSQL or a multi-silo Orleans mesh; both
tests run on an in-memory monolith, so what is measured is the fan-in, the fold and the classifiers,
not a real partitioned provider's timing. The 15 s default is calibrated against the healthy worst
case *recorded in the probe's own comment*, not against a fresh measurement of the deployed fleet.

## The first fleet readings (memex-cloud, 2026-09-22)

The terminal's first day on a PostgreSQL fleet filed four incidents: #5315, #5345, #5390 and #5393.
They read as "the Postgres provider stalls", and the question each one asks is whether to fix the
provider or the budget. Neither is the answer.

**Every occurrence falls inside a ThreadPool-starvation episode on the same pod**, logged by other
instruments:

| incident | pod · window | starvation on the same pod |
|---|---|---|
| #5315 (9 activations at filing, 13:41:30–13:43:23Z; a 10th at 13:46:48Z was folded in as a recurrence) | `7cc85f47c-nm2qp` · 13:41:30–13:46:48Z | routing legs waiting for pool threads 13:36:07–13:49:40Z (#5306, #5313, #5316, #5317); TLS handshake to Postgres timing out on a new connector 13:42–13:47Z (#5314) |
| #5345 (2 HTTP 500s) | `5c444645f8-8pdzb` · 14:43:42Z | 88 CompileWatcher stalls 14:43:02–14:43:35Z (#5344); routing starvation 14:41:08–19Z and 14:45:45–14:47:37Z (#5335, #5349) |
| #5390 (108 activations) | `857546649-2c25b` · 18:49:44–56Z | `.NET Thread Pool execution stalled for 24.7s` at 18:47:10Z (#5388); routing legs starved 18:47:40–18:49:57Z (#5389, #5391) |
| #5393 (4 activations) | `857546649-2c25b` · 18:53:19Z | the same episode as #5390, 3.5 minutes later |

The finding the four share is **starvation of the process**, not one query shape. The three grain
incidents (#5315, #5390, #5393) timed out on trivial single-schema reads: an `IN` list of ancestor
paths (`path:"A/B/C"|"A/B"|"A"`). #5345 timed out on a different query, the catalogue listing
`namespace:Plugins nodeType:Package`. In every case the provider did not starve. The **process**
starved, and the provider was where the fan-in saw it. All four occurred before
[NodeType compiles moved off the ThreadPool](../CompileOffTheThreadPool) (#5327, merged 20:27Z that
day), and the CompileWatcher burst next to #5345 is that defect's signature.

**The provider set in the message is set by the query shape, not by the fault.** #5315 names
`StorageAdapterMeshQueryProvider` *and* `PostgreSqlPartitionedMeshQuery`, while #5390/#5393 name only
the latter. `StorageAdapterMeshQueryProvider.DefersToNativeProvider` returns *false* for a path with a
`_`-prefixed segment (`_Access`, `_Entitlements`, `_Activity`, `_Install`: every #5315 path) and
*true* for a plain primary path (`Admin/Build/Radzen`, `Pricing/Guideline`). A deferring provider
answers with an empty Initial at once, so only the leg that really reads Postgres is left to be named.

**A faulted grain activation is the designed outcome.** The activation resolves its node by
longest-prefix match over the ancestor list. A partial answer would bind the hub to the wrong ancestor
or report a present node as absent. So the activation faults, `TryDeactivateOnIdle` discards it, and
the next access resolves again from scratch. Access consumers read the same fault as `Unavailable`,
fail-closed (see above).

**An HTTP route needs one more step: it must map the fault.** The plugin-bundle routes let it escape
as an unhandled exception, so ASP.NET answered a bare **500** and logged the stall as a defect of the
endpoint (#5345). The routes now answer **503 + `Retry-After`** through
`InstanceAuthResponses.UnavailableOnAStalledRead`. That is the instance-key 503 convention, and the
fault classifier is `AreaErrorClassifier.IsStorageUnavailable`, the same one the GUI uses. Any other
fault still escapes, so a real defect still surfaces.

**Not established from these readings.** No per-pod CPU, GC or Npgsql pool metrics for the windows
were read (the control instance's `Logs`/`Sample` were unavailable), so a GC pause and a
connection-pool wait cannot be excluded as contributors. The portal's Npgsql pool is `MaxPoolSize = 50`
while the query leaves run on the 256-slot `FileSystem` IoPool and bypass the per-adapter
`pg-read:` cap, so connection waits under a burst are *possible*; nothing here shows they happened.
Whether #5327 alone ends the starvation episodes is the post-roll question: read
`MeshWeaver.Hosting.Orleans.MessageHubGrain` `QueryProviderStalledException` lines on `memex-cloud`
over a window that includes a roll of an image carrying #5327.

## What the Initial queues behind: one full re-read per change (2026-09-23)

The terminal fired again on 2026-09-23, on images that carry #5327. Two issues recorded it:

- #5315: 12 `[ACTIVATE]` faults, the latest at 13:26:31Z on `5f95fb94dc-t9222`.
- #5344: 2,109 CompileWatcher faults, including 13:26–13:34Z on the `5f95fb94dc` pods and 14:45Z on
  `777677694-hjkc8`.

So moving compiles off the shared ThreadPool (#5327) did not remove the stall. What is left is where
the Initial's 15 s goes. The clock starts at Subscribe (see *Where the terminal is armed*), so the
budget covers the SQL and also every queue the read waits in before it runs.

**Where a Postgres read actually waits.** An anchored query is served by the per-schema
`PostgreSqlMeshQuery`. Its leaf runs on the `FileSystem` pool (256 slots). Inside that slot it calls
`PostgreSqlStorageAdapter.QueryNodesAsync`, which takes a slot on `pg-read:Postgres` through
`ReadPooled`. That pool is **one per process**, with a cap of 16, shared by every per-schema adapter.

This corrects the sentence above that said the leaves "bypass the per-adapter `pg-read:` cap". They do
not bypass it. They hold a `FileSystem` slot while they wait for a `pg-read` slot. The one reading of
that pool's queue ([Controlled IO Pooling](../ControlledIoPooling), memex.systemorph.com, 828 minutes):

- 31.9 M admissions, about 640 per second;
- mean wait **342 ms**;
- 48,122 waits over a second.

In steady state the gate every Initial must pass is already saturated.

**What fills it: the live pipelines queued one full read PER change notification.** Both per-schema
providers re-ran a live query with `changeBuffer.Select(_ => RunQuery()).Concat()`:

- `StorageAdapterMeshQueryProvider` (core). On partitioned Postgres it serves every path with a `_`
  segment: `_Access`, `_Activity`, `_Install`, and so on.
- `PostgreSqlMeshQuery` (Plugins). It serves every anchored query.

So a burst of N writes under one live query's scope cost N back-to-back full reads. The queue had no
bound, and it grows fastest exactly when the pool is contended. Under contention each read waits
longer, more notifications pile up behind it, and every one of them is another full read.

A pod start is the extreme case. Hundreds of Initials are issued together (grain activations, one
sources watcher per NodeType space), each fault re-establishes a second later, and writes from every
replica keep arriving through the merged change feed. Plugins#2328 measured the same shape on the
fan-out provider: one home load ran the same union about 35 times in 11 s.

**The fix removes the queue, not the bound.** Both providers now use
`CoalesceWhileRunning` (`MeshWeaver.Reactive.CoalesceWhileRunningExtensions`; the Plugins fan-out
provider has used an identical internal copy since #2328):

- only one re-query runs at a time;
- any number of triggers that arrive while it runs fold into **one** follow-up;
- the follow-up starts after the last of them arrived, so it reads every write they announced.

This is not a debounce. There is no timer, and an idle query re-runs at once, so the race behind the
old 100 ms `Buffer` does not come back. The answer is the queue's last answer, without the reads in
between.

Both halves are pinned by a test that holds a re-query in flight, delivers a burst of 12, and counts
the reads:

| test | with the fix | per-change `Concat` (negative control) |
|---|---|---|
| `LiveRequeryCoalescingTest` (core, in-memory store) | 2 walks | 12 walks |
| `LiveRequeryCoalescingPgTests` (Plugins, real Postgres) | 2 re-queries | more than 2 within 3 s |

The 15 s budget is unchanged (policy `query-fanin-stall-terminal`).

**Not established.**

- The share of the ~640 reads per second that per-change re-queries account for. No per-query read
  census exists, and the control instance's `Logs`/`Sample` were unavailable.
- Whether coalescing alone keeps a pod start inside 15 s. The boot wave of Initials, and the watcher
  re-establish storm, are other contributors this change does not touch.
- The post-roll reading that decides both: on a roll of an image carrying both halves, count
  `QueryProviderStalledException` lines within 15 minutes of pod start, at `[ACTIVATE]` and at
  `Sources watcher faulted`, against the 2026-09-23 counts above.

## One request of many exact paths: one read, not one per query (2026-09-25)

The coalescing above was live on memex-cloud (image `3.0.0-ci.9291`, core #5615 + Plugins#2337) and
the terminal fired again: 12 lines on pod `589ff8f895-ghgjt`, 2026-09-24 14:17:44Z–14:30:51Z, in
four bursts of three, each naming only `StorageAdapterMeshQueryProvider` and a query of the shape
`path:AppleMaps/_Entitlements/{user} select:path` for a different user (`iser.steinmetz`,
`kalpesh.patel`, `beat`, `rbuergi`, `goedel`, …), run as `system-security`.

**Who issues it.** The bursts of three are `StandardPacks.MaxUserConcurrency` (3): the Store's
all-users standard-pack sweep (`StandardPacks.EnsureForAllUsers`, MeshWeaver.Plugins). Its per-user
`Prefetch` asks ONE `MeshQueryRequest` for the viewer's entitlement records over every free pack —
`Entitlements.BatchQueries`, two exact `path:` queries per pack (the `_Entitlements` marker and the
`_Access` grant). With about fifteen free packs that is about thirty queries in one request. The
fan-in's message names only `request.Query`, the FIRST of them, and `AppleMaps` sorts first.

**Why it misses 15 s.** `StorageAdapterMeshQueryProvider.CollectMatched` ran the request's queries
one after the other (`Concat`), and each exact query issued its OWN `ReadMany`. So one request of
thirty exact paths was thirty SEQUENTIAL storage round-trips. On partitioned Postgres each one first
waits for a slot on the process-wide `pg-read:Postgres` pool (cap 16; mean queue wait 342 ms in the
one reading of it, see *What the Initial queues behind*). Thirty waits in series is the 15 s budget,
with nothing stuck. The same pod logged the load around it: routing back-pressure at 14:44:12Z with
63 legs waiting for a pool slot, the latest dispatch targets `{plugin}/_Entitlements/{user}` and
`{user}/_Install/{pack}` — the per-pack owner reads the sweep falls back to when its prefetch fails.
A failed prefetch therefore made the sweep MORE expensive, which is the wrong direction for a fault
caused by load.

**A second defect in the same read.** The provider applied its result exclusions
(`IsExcludedFromResults`: satellite rows, partition roots) once, over the union, judged against the
FIRST query's parse. The first query targets `_Entitlements`, which is not a configured satellite
segment, so every `_Access` row the second half of the request asked for was excluded as if nobody
had asked. The prefetch read every viewer's grants as absent, so a pack entitled by grant alone was
never settled from the listing and always took the per-pack owner reads.

**The fix (core, `StorageAdapterMeshQueryProvider`).**

- `CollectMatched` collects the exact-path probes of EVERY query in the request and reads their
  union with ONE `ReadMany` (`ReadProbes`). Each query then picks the paths it asked for out of that
  one read and applies its own filter. Walk scopes are unchanged.
- The exclusions run per query, against the query that found the node.

`ExactProbeBatchingTest` (`test/MeshWeaver.Hosting.Test`) pins both. Thirty exact queries reach the
store as one `ReadMany` carrying all thirty paths, and the Initial holds every record that exists,
grants included. Negative control, run against the unfixed provider: the Initial lacks all three
grants, and a two-query request makes 2 `ReadMany` calls where the fix makes 1. A second case pins
per-query attribution: a node probed by a query whose `nodeType:` does not match stays out.

The 15 s budget is unchanged (policy `query-fanin-stall-terminal`).

**Not established.**

- The count of free packs on memex-cloud, and so the exact query count per prefetch, is inferred
  from the `…/_Entitlements/morenocaro` writes one onboarding logged, not read from the catalogue.
- On Postgres the one `ReadMany` still becomes one pooled point read per path, in parallel:
  `PostgreSqlPathRoutingAdapter` (MeshWeaver.Plugins) has no `ReadMany` override, so the interface
  default fans out to `Read`. The per-schema adapter's batched `ReadMany` (one statement per table)
  is not reached. The core change turns thirty serial round-trips into thirty parallel ones; a
  routing override would make them one per schema and table.
- Whether this ends the terminal on memex-cloud. A `Logs` reading over every pod
  (`|~ "did not emit an Initial"`, 885 minutes to 05:05Z on 2026-09-25,
  `Ops/logs-memexcloud-20260925-entstall-f-allstall`) read 18 lines, none after 14:30:51Z on
  2026-09-24. The sweep that stalls runs rarely, so a quiet window says little; the next reading has
  to cover a sweep on an image carrying this change.

## See also

- [Access Control](../AccessControl) → "The fold can produce NO answer, and that is a third outcome",
  "The convergence contract", "The stall is now that error"
- [Hub Initialization Failure](../HubInitializationFailure) → "Resolving the node is a READ" (#1186 —
  the consumer-side half: a floor snapshot is not a resolution)
- [Policy Not Prose](../PolicyNotProse) → `query-fanin-stall-terminal`
- [CQRS and Content Access](../CqrsAndContentAccess) — what a query may and may not be asked
