---
Name: Stream Liveness and the Hub Reference
Category: Architecture
Description: A synchronization stream outlived the hub it held, and ISynchronizationStream.Hub is declared non-nullable — so a corpse answered it and 54 call sites dereferenced it. Why the obvious fix (null the field) is the one that already caused a production NRE, and the three-step order that did not repeat it. All three steps have landed.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M22 12h-4l-3 9L9 3l-3 9H2"/></svg>
---

# Stream Liveness and the Hub Reference

**A `SynchronizationStream` holds `IMessageHub Hub`, the interface declares it NON-NULLABLE, and a
stream whose owner had been torn down kept answering it. So the corpse was indistinguishable from a
live stream at every one of the 54 sites that dereference it — and the reference kept the dead hub's
whole resolved graph alive.**

That is two defects wearing one shape: a **retention** leak, and a **contract** that lies. They have
to be fixed in that order backwards — the contract first — and this page exists because the obvious
order was already tried in production.

**All three steps have landed** (#3380, #3386, #3321). The stream now releases the reference from
both ends — its own disposal, and its hub's death underneath it — and the sections below say why
that could not simply be done, and what each of the 54 sites answers now.

## What the field costs while it is held

Closing a DI scope does not null the fields on an object that outlives it. The retained graph is
`stream → dead hub → its own resolved state` (`TypeRegistry`, and whatever else was resolved onto the
hub at construction). `MessageHub` deliberately does not dispose its own `ServiceProvider` —
`HostedHubsCollection.Add` closes the scope instead, being *"the only place that both knows the scope
exists and can act strictly after this hub is terminally down"* — and it does its job: 1 495 of 1 496
hubs were removed. The bytes stay reachable anyway, because reachability is about references, not
scopes. The only way to reclaim them is to **drop the reference**.

`Dispose()` sets `isDisposed`, completes the store, clears `sharedReduceCache` and disposes
`streamDisposables` — but it never cleared `Hub`. So even the streams that *were* properly disposed
kept their hub reachable. 🚨 And that is the smaller half: only **11** of the 1 496 corpses sat under
a disposed stream. See "What step 3 did" below.

## Why "just null the field" was never available on its own

It was already done once, and it is recorded in `SynchronizationStream`'s own constructor refusal path.
The predecessor built a DEAD stream — `isDisposed`, store completed, **`Hub = null!`** — and
documented that *"every code path that touches Hub goes through `TryGetActiveHub`"*.

**No consumer honoured that, and none could.** `TryGetActiveHub` was private to that one file, while
~96 sites dereferenced `ISynchronizationStream.Hub`, which the interface declares non-nullable. A
consumer could not even detect the corpse: the contract exposed no liveness member at all.

The result was an NRE inside `LayoutAreaHost`'s constructor (`Stream.Hub.ServiceProvider…`) during
the overlay self-heal recycle window, which escaped to the subscriber as a **terminal
`DeliveryFailure`**. A page that subscribed mid-recycle was told *"this failed forever"* instead of
*"ask again"*.

So the in-file discipline is not what failed. The **contract** failed: it promised non-null to
callers who had no way to ask.

## Detection was never the missing piece

`StreamLiveness.IsUsable` already computes the whole answer, and has since #1455 / #2387:

- it treats `current.Hub is not { } hub` as dead;
- it checks `RunLevel > Started` and `MessageHub { IsDisposing: true }`, because hub shutdown is
  asynchronous and RunLevel still reads `Started` immediately after `Dispose()`;
- it walks the **reduce chain** through `IStreamLivenessSource.Source`, because a reduced stream is
  its parent's SIBLING — `WorkspaceStreams.CreateReducedStream` hosts its `sync/{id}` sub-hub under
  the parent's `Host` and only registers it for disposal ON the parent — so a child happily mirrors
  a source that is already dead;
- it counts FAULTED exactly as much as DISPOSED, because a store is a `ReplaySubject` and a terminal
  `OnError` is permanent under the Rx grammar.

Every cache already refuses to *serve* a corpse. What was missing is letting it **go** — and, before
that, letting a consumer **ask**.

## The three steps, in the only order that works

1. ✅ **Expose the predicate.** `SynchronizationStreamLiveness.IsUsable(stream)` and
   `stream.TryGetHub()` make the existing answer reachable from other assemblies. (Step 3 adds
   `HubIfHeld()` and `RequireHub()` beside them — the PRESENCE question, which is not the same
   question; see below.)
2. ✅ **Migrate the dereference sites** onto `TryGetHub()`, which can answer `null`.
3. ✅ **Only then** release `Hub` — at which point an absent hub is a state the contract admits
   rather than a lie.

Doing 3 before 2 is the production NRE, in the order it happened. Doing 2 before 1 is impossible.

### What step 2 actually migrated, and what it deliberately did not

Measured on the branch that did it: **54** occurrences in `src/` where the receiver of `.Hub` is an
`ISynchronizationStream` (excluding comments, and excluding `SynchronizationStream`'s own bare
`Hub` field reads). The header's "47" is the earlier count and is superseded. Of those 54, **28**
were migrated and **26** were deliberately left.

**Every migrated site answers with the "no" its own signature already modelled** — that is the rule,
not a style preference. `GetPatch` returns `null` (its callers already test for it); `GetStream<T>`
returns `Observable.Empty<T>()` (a dead stream's completed store would produce exactly that);
`GetDataBoundValue<T>` returns `default` (the answer it already gives twice for an absent value);
`SubmitModel` returns an `ActivityLog` carrying `activity.dataUpdate.streamClosed`, the same shape
as its existing no-route branch; `ToDataChanged` returns `null`, its declared "nothing to forward".
Where a surface has no absent value, the refusal goes out the channel it does have:
`WorkspaceOperations.UpdateStream` throws its existing `DataException`, and `LayoutAreaHost`'s
constructor and `MarkdownExecutionExtensions.Execute` throw `HubDisposingException` — an
`ObjectDisposedException`, so it classifies as `ErrorType.ShuttingDown`, the transient
"retry, the address may reactivate" answer instead of the terminal `DeliveryFailure` the NRE
produced. **No site swallows.**

The 26 left fall in three groups, and the reason differs per group:

- **Already answerable, or already a liveness check (3).** `LayoutClientExtensions`'
  conversion-failure logger is already written `stream?.Hub?.…`. `Workspace`'s cache probe reads
  `existing.Stream.Hub is { RunLevel: <= Started }` — a null-safe pattern that IS a liveness check;
  replacing it with `IsUsable` would TIGHTEN it (chain walk, fault check) and so change behaviour
  for a live stream, which step 2 may not do. `Workspace`'s eviction path calls
  `kv.Value.Stream.Hub.Dispose()` inside a try/catch that already logs the failure — and guarding it
  would SKIP a disposal, leaking the hub of a reduced stream whose parent died first.
  (`SynchronizationStream`'s own write paths funnel through the private `TryGetActiveHub`, and
  `DeliverMessage` / `OnNext` / `RegisterForDisposal` carry `isDisposed || Hub is null`. Those are
  bare `Hub` field reads with no stream receiver, so they are outside the 54 by construction.)
- **Hub-turn confined (3).** The three `Stream.Hub.Version` reads inside `LayoutAreaHost`'s
  `Stream.Update(…)` lambdas. `Update` already refuses on a dead stream via
  `SignalDisposedToProducer`, and the lambda itself runs inside the sync hub's
  `UpdateStreamRequest` handler — a hub that is executing a turn is by construction alive.
- **Owner-side, no way to say "no" (20).** `StandardReducers` (10), `MeshDataSource`'s patch
  reducer (2), and the stream-creation paths in `WorkspaceStreams` (2),
  `PartitionedHubDataSource` (2), `VirtualDataSource` (1), `GenericUnpartitionedDataSource` (1) and
  `JsonSynchronizationStream`'s `reduced.Hub.Register` (2). A reducer's signature is
  `… → ChangeItem<T>`: it MUST return a change, so
  there is no absent value to hand back and no error channel to use. Forcing one would mean
  inventing a failure mode rather than migrating onto an existing one — so they are named here
  instead. **They are step 3's remaining work**: before `Hub` can be cleared on disposal, each of
  these needs a decision about what a reducer does when its stream has died, and that is a design
  question, not a mechanical substitution. (Step 3 resolved them — plus five more this list did not
  cover. See "How the sites step 2 could not migrate were resolved" below.)

One more constraint the migration follows: **a guarded read inside a per-emission lambda re-reads
`TryGetHub()` rather than capturing the hub resolved at pipeline-construction time.** Capturing
would pin the hub's whole resolved graph for the lifetime of every subscription — the exact
retention this issue exists to release.

## What step 3 did

### 🚨 `Dispose()` alone would have reclaimed 11 of 1 496

The step is usually described as "`SynchronizationStream.Dispose()` clears `Hub`". Read against the
dump, that description is a **sixth of the fix**:

```
SynchronizationStream total=8461 disposed=11
    1485  <MeshNode>     hub RunLevel=Dead, stream NOT disposed
      11  <JsonElement>  hub RunLevel=Dead, stream DISPOSED
```

A `sync/` hub is a HOSTED hub. Its parent disposes it during the parent's own teardown — a Blazor
circuit ending, a `DisposeRequest`, a recycle — while the stream that created it is owned by a
workspace somewhere else and **is never told**. So the stream keeps `isDisposed == false`, keeps a
strong reference to the corpse, and keeps being handed out as usable. Clearing the field in
`Dispose()` never runs for those 1 485.

Step 3 therefore releases from **both ends**, through one private `ReleaseHub()` (idempotent, CAS'd,
callable from any thread — it disposes nothing, it only drops a reference):

| End | Where | Covers |
|---|---|---|
| The stream is disposed | `Dispose()`, after `Hub.Dispose()` | the 11 |
| The hub dies underneath it | `syncHub.RegisterForDisposal(_ => ReleaseHub())`, from the constructor | the 1 485 |

The constructor's hook is registered **after** `RegisterForDisposal(resyncSubscription)`, and that
ordering is load-bearing: that call is what hooks the stream's own `streamDisposables` onto the hub
(`RegisterForDisposal`'s one-shot `hubDisposalHooked`), and a hub's composite disposes in
registration order — so the stream's registrants run first and still see a bound `Hub`, and the
release runs after them. A hub already past accepting registrants disposes the hook inline, so the
constructor re-checks and refuses with `HubDisposingException` exactly as its `syncHub is null`
branch does: a stream with no hub is not a stream.

Nothing new appears in the liveness vocabulary. `StreamLiveness.IsUsable` has always treated
`current.Hub is not { } hub` as dead, so a released stream reports itself unusable through the same
predicate every cache already consults.

### How the sites step 2 could not migrate were resolved

A fresh sweep of `src/` — every `.Hub` whose receiver resolves to an `ISynchronizationStream` —
found **29 occurrences on 27 lines**. Four stay untouched and must: two ARE the predicate
(`StreamLiveness.IsUsable`'s chain walk, `TryGetHub`'s own read), one is already written `stream?.Hub?.…`,
and one is `Workspace`'s cache probe, which is itself a liveness check. **The other 25 are step 3's
work** — the 20 step 2 named, plus five it did not:

- the **three** `LayoutAreaHost` reads step 2 classified "hub-turn confined". That classification
  rested on *a hub executing a turn is by construction alive*, which is a statement about the HUB.
  Step 3 makes the FIELD independently clearable, so a queued `UpdateStreamRequest` can now run
  after the release — the premise expired with the change that needed it;
- `Workspace.EvictClientSubscriptions`, which step 2 placed in "already answerable" for the right
  reason (guarding it with `IsUsable` would skip a disposal) — a null check is still needed;
- `WorkspaceExtensions.ApplyChanges`, which step 2's list **missed** (`stream!.Hub.Version`, behind
  a null-forgiving `!`).

The principle is unchanged from step 2: **answer with the "no" the surface already models; where
there is none, use the channel it does have.**

| Group | Sites | Resolution |
|---|---|---|
| **Patch reducers** — `StandardReducers` (10), `MeshDataSource.PatchMeshNode` (2) | 12 | `RequireHub()`, absorbed by their single gateway |
| **A creation-path `Subscribe` callback** — `WorkspaceStreams` (2), `PartitionedHubDataSource` (2) | 4 | `HubIfHeld()` once at the top; **skip the emission** |
| **Inside a `stream.Update(…)` lambda** — `LayoutAreaHost` (3), `VirtualDataSource` (1) | 4 | `HubIfHeld()`, then `return null` — `Update`'s documented no-op |
| `JsonSynchronizationStream`'s `reduced.Hub.Register` | 2 | `HubIfHeld()` once; **don't register** |
| `GenericUnpartitionedDataSource.Initialized` | 1 | a released stream contributes **no task to wait for** |
| `WorkspaceExtensions.ApplyChanges` | 1 | `RequireHub()` — same shape as a reducer |
| `Workspace.EvictClientSubscriptions` | 1 | `Hub is { }` — deliberately not a liveness check |
| | **25** | |

**The reducers are the whole design problem, and the gateway is the answer.** A reducer's signature
is `… → ChangeItem<T>`: it MUST return a change, so it has no absent value to hand back. But every
patch reducer in the codebase is invoked from exactly ONE place —
`JsonSynchronizationStream.ToChangeItem`, whose `PatchFunction?.Invoke` is the only call site of
`ReduceManager.PatchFunction` — and `null` is what `ToChangeItem` **already returns** for a stream
with no `PatchFunction` registered. So:

- the reducers read the hub once through `SynchronizationStreamLiveness.RequireHub()`, which refuses
  with the TRANSIENT `HubDisposingException` (an `ObjectDisposedException`, classified
  `ErrorType.ShuttingDown` — the "ask again" answer, never the terminal `DeliveryFailure` the
  predecessor's raw NRE produced);
- `ToChangeItem` catches **only that type** and answers `null`, which both of its callers already
  handle: `BuildChangeItem` falls through to its type-registry path, `UpdateStream` falls back to
  deserializing the patched JSON through `Host.JsonSerializerOptions`.

That is not a swallow, and it is not a `catch (NullReferenceException)`. It converts one specific,
meaningful refusal into an absence the signature already carries; every other exception a reducer
throws still propagates.

### 🚨 PRESENCE is not LIVENESS, and using the wrong one is a behaviour change — measured

The first draft of this step wrote every owner-side guard as `TryGetHub()`, on the reasoning that
step 2's rule was "migrate onto `TryGetHub()`". That is wrong, and one existing test proved it before
the change left the branch.

`DataSource.Initialized` is `Task.WhenAll(streams.Select(s => s.Hub.Started))`. Guarded with
`TryGetHub()`, a stream whose **initial load faulted** is excluded — because it is not `IsUsable`,
which is exactly what `IsFaulted` is for. So the WhenAll no longer contained the task carrying the
fault, `Initialized` completed **successfully**, and a hub whose data source had thrown went on
answering requests as though nothing had happened. `DataContextFaultedInitBeforeStreamHubBoundTest`
went red on precisely that (*"a hub whose data-source init threw must answer requests with an error,
but no exception was thrown"*) — the #2625 wedge, re-introduced by a guard that looked like a
tightening of safety.

So step 3 uses **two different accessors, for two different questions**:

| Accessor | Question | Where |
|---|---|---|
| `TryGetHub()` (`IsUsable`) | *should I use this stream?* — walks the reduce chain, counts a faulted store, refuses a winding-down hub | a CONSUMER, a cache, a read — step 2's 28 sites |
| `HubIfHeld()` / `RequireHub()` | *is the reference still there?* — one field read, nothing else | an OWNER writing into its own stream, where a stricter answer changes what the code does |

**The rule for an owner-side guard is: match the guard the site's own gate already applies.** Every
one of them ends in `stream.OnNext` or `stream.Update`, whose refusal is `isDisposed || Hub is null`
— so a presence check refuses in exactly the cases the write would have been dropped anyway, and
never one more. `TryGetHub()` there would be a silent policy change smuggled in under a null-safety
fix, which is the shape step 2 already declined at `Workspace`'s cache probe.

`RequireHub()` and `HubIfHeld()` therefore ask the same narrow question and one is written in terms
of the other, so there is one definition of presence and one of liveness — never a fourth
hand-copied predicate (#1455). The same reasoning governs `Workspace.EvictClientSubscriptions`:
guarding its `Stream.Hub.Dispose()` with `IsUsable` would SKIP a disposal — a reduced stream whose
*parent* died first is unusable while its own hub is very much alive — so it is guarded with
`Hub is { }`, which only skips when the reference is already gone, i.e. when the hub is already
disposed.

### 🚨 The dependent half — and why no gate could have told you

`ISynchronizationStream.Hub` is a **public** member of a **public** interface that these assemblies
ship as packages, and step 3 changes what it ANSWERS without changing its signature. That is shape 7
of the seven cross-repo break shapes, and it is the one **no surface detector can see by
construction** — the `Cross-repo pair (public surface)` gate resolves removed types and removed
members, and this diff removes zero of each. So the sweep has to be done by hand, on the receiver
type, across every satellite checkout.

Measured on 2026-09-06, all six satellites, every file (not just `.cs` — in-mesh C# lives inside
`.json` node strings too):

| Repo | stream-receiver `.Hub` | raw `.Hub` lines classified |
|---|---:|---:|
| **MeshWeaver.Plugins** | **22** (11 production, 11 test) | 2 051 |
| education | 0 | 483 |
| MeshWeaver.Education | 0 | 11 |
| MeshWeaver.Reinsurance | 0 | 152 |
| MeshWeaver.SocialMedia | 0 | 73 |
| MeshWeaver.Manufacturing | 0 | 15 |

The four content satellites are clean *structurally*, not merely by token match:
`ISynchronizationStream` appears in ZERO files in four of them, and in exactly one file in
Reinsurance where the stream is only `.Update(…)`d. Every `.json` node carrying `.Hub` inside a C#
`configuration` string resolves to `IWorkspace.Hub` / `LayoutAreaHost.Hub` / an `IMessageHub` local.

**All 11 production dereferences are one cluster in `MeshWeaver.Plugins`** — the Blazor view layer's
inherited `ISynchronizationStream<JsonElement>? Stream` (`BlazorView.razor.cs`): `OnClick`,
`OnBlur`, both `DialogView` close handlers, and seven `JsonSerializerOptions` reads across
`ViewModelExtensions`, `FormComponentBase`, `DataGridView`, `RadzenChartView` and
`RadzenPivotGridView`.

🚨 **The `!` in `Stream!.Hub.Something` is on `Stream`, not on `.Hub`** — so the NRE moves one level
right, past the operator that suppresses the warning, and the compiler stays silent. One of them
(`RadzenChartView.razor:171`) sits inside a bare `catch { }`, so it would have been invisible at
runtime too: a wrong chart rather than an exception.

**Order: the dependent lands FIRST.** Its guards are no-ops on a live stream, so they are safe
against the old core; core's release is not safe against an unguarded dependent. The core PR was
held as a draft until the Plugins counterpart merged.

### And every read inside the stream now resolves the field once

Step 2 excluded `SynchronizationStream`'s own bare `Hub` reads as "already guarded". They were —
against a field that could not change. Once `Dispose()` can null it, a guard followed by a *re-read*
is check-then-act across threads, so every read below a guard now uses the local the guard resolved:
`OwnerVersion(hub)`, `BuildChangeItem(hub, …)`, `BuildFullChangeItem(hub, …)`, and the locals in
`RegisterForDisposal` / `DeliverMessage` / `OnNext` / `OnError`. `Update`'s and `SetFull`'s value
transforms resolve through `TryGetActiveHub` on the turn and return `null` when it answers no.

### The measurement that says these tests could fail

`StreamReleasesItsHubTest` (6 cases; the numbers below were taken at 5, before the accessor-parity
case was added) and the step-2 suite were re-run with **only** the two
`ReleaseHub()` call sites disabled — everything else, including `RequireHub` and every guard, left
in place, so the experiment isolates exactly what step 3 does:

| Suite | release disabled | release enabled |
|---|---|---|
| `StreamReleasesItsHubTest` (`MeshWeaver.Data.Test`) | **4 failed, 1 passed** | **5 passed** |
| `TornDownStreamCallSitesTest` (`MeshWeaver.Layout.Test`) | **4 failed, 1 passed** | **5 passed** |

The live control passed in both runs of both suites — a release that fired unconditionally, or a
guard that answered "dead" always, would satisfy every negative at once, and the controls are what
rule that out.

🚨 The flagship assertion is a `WeakReference`, not `Hub is null`. "The field reads null" is a
statement about one field; **"the hub is unreachable"** is the statement this issue is about, and it
is the only one that goes red on the defect for the right reason. It passes: with the release in
place, the hub is collected.

(A *full* `src/` revert does not compile the tests, because `RequireHub` is itself part of step 3 —
which is why the falsification reverts the release rather than the whole change.)

### Why step 1 is an extension, not an interface member

`StreamLiveness` records both reasons and neither has expired:

- **Adding a member to a public interface breaks every downstream implementer**, and these
  assemblies ship as packages.
- **`IStreamLivenessSource.Source` is not a consumer-facing concept.** It exists so the reduce chain
  is walked in exactly ONE place; a consumer walking it by hand is the ad-hoc predicate that whole
  design removed.

An extension method satisfies both: the predicate becomes reachable, the interface it reads stays
internal, and no implementer is touched. The public methods delegate and add no logic of their own —
`StreamLiveness` exists *because* three hand-copied liveness predicates diverged, and a public
wrapper that re-implemented the check would be the fourth. The name stays `IsUsable` for the same
reason: a dozen comments across `Workspace`, `JsonSynchronizationStream` and
`StreamNotConvergingException` already point readers at "StreamLiveness.IsUsable", and a consumer
following one of them must find the thing it names.

### The guard that keeps step 1 from silently reverting

`MeshWeaver.Data` grants `InternalsVisibleTo("MeshWeaver.Data.Test")`, so a test that merely *called*
`stream.IsUsable()` would compile and pass just as happily if the surface reverted to `internal` —
which is the exact state this step exists to leave behind. So
`StreamLivenessIsConsumerReachableTest.TheSurfaceIsGenuinelyPublic` reads accessibility by
**reflection**, which `InternalsVisibleTo` does not alter. Measured: with the class flipped to
`internal` the project still builds and the other three tests still pass — only that one goes red.

## Related

- `Doc/Architecture/RemovingHandWovenGates` — the sibling rule for gates the actor model cannot hold
- `Doc/Architecture/AsynchronousCalls` — why every hub-reachable path is `IObservable<T>`
- Systemorph/MeshWeaver#3321 (this program) · #1455, #2387 (why `StreamLiveness` is one predicate)
