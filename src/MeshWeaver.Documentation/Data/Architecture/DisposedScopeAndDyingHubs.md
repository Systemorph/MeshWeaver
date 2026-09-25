---
NodeType: Markdown
Name: "Disposed Scopes and Dying Hubs — one symptom, five roots"
Abstract: "Every ObjectDisposedException whose top frame is Autofac's LifetimeScope.ThrowDisposedException looks the same in a log and comes from one of five unrelated roots: the host container going down ahead of the mesh, a continuation resolving from a scope its own pipeline closed, a hub teardown that wedges, a shutdown drain budget expiring over live work, and Orleans dropping deliveries at silo stop. This page separates them, records which are fixed, carries the swept inventory of deferred resolve sites, and explains why a dying hub is retired from its registry only at ShutDown and why every removal a teardown makes must be value-matched."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#4527a0'/><path d='M7 7l10 10M17 7L7 17' fill='none' stroke='white' stroke-width='1.8' stroke-linecap='round'/></svg>"
Thumbnail: "images/DataMesh.svg"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Disposal"
  - "Lifecycle"
  - "Operations"
---

# Disposed Scopes and Dying Hubs — one symptom, five roots

> **The rule.** `ObjectDisposedException: Instances cannot be resolved … from this LifetimeScope`
> with `Autofac.Core.Lifetime.LifetimeScope.ThrowDisposedException()` on top is a **symptom, not a
> diagnosis**. Five unrelated roots produce a byte-identical top frame, and the fix for one is a
> no-op for the others. Before touching code, read **which scope closed, who closed it, and
> whether the work that faulted was still needed** — those three answers pick the root.
>
> The layered model is in [Teardown Layers](../TeardownLayers); the hub-level mechanics in
> [Hub Disposal Model](../HubDisposalModel); the stall verdicts in
> [Reading a Disposal Stall Verdict](../DisposalStallVerdicts). This page is the *triage* page that
> sits in front of them.

## The five roots

| Root | What closed the scope | The tell in the log | Status |
|---|---|---|---|
| **R1 — the host container went down ahead of the mesh** | the generic host disposed the root provider while the mesh pump, Orleans directory and hosted-hub creation were still live | a burst across several pods inside one rolling deploy; frames in *upstream* code (Orleans `PlacementDirectorResolver`, `CodecProvider`) as often as in ours | **fixed** — `MeshTeardownHostedService` |
| **R2 — a dying hub is handed out as the live hub** | nothing; the hub is alive but past accepting work | the same hub address answering `Hub … is shutting down — cannot register new response subject` to *unrelated* callers minutes apart, on a pod that is otherwise serving | **fixed** — a hub at `ShutDown` is retired from the registry and a successor minted; every removal is value-matched (see below) |
| **R3 — a continuation resolved from a scope its own pipeline closed** | the operation itself | an ordinary-hours failure with no deploy nearby; the frame is `System.Reactive.Linq.ObservableImpl.Do`/`SelectMany` calling a lambda in our code | **partly fixed** — see the inventory |
| **R4 — a drain budget expired over live work** | the drain, deliberately, after saying so | `did not finish within 00:00:30 — … releasing over live work` / `route leg(s) did not land` | **instrumented, subject unidentified** |
| **R5 — Orleans dropped a delivery at silo stop** | nothing — the silo refused placement | `SiloUnavailableException: Silo '…' is shutting down`, `ForwardCount=1` | **upstream behaviour** |

The discriminators that actually separate them:

- **Is there a deploy or pod restart within a minute of the burst?** No ⇒ never R1.
- **Is the faulting frame in upstream code?** Yes ⇒ R1 or R5; we cannot patch the frame, only the
  ordering around it.
- **Did the same address fault repeatedly over tens of minutes on a live pod?** ⇒ R2. A pod drain
  cannot last that long; the Kubernetes grace ceiling ends it.
- **Did the operation the continuation belonged to itself dispose something?** ⇒ R3.

## R1 — the host container ahead of the mesh (fixed)

Disposing a hub is reactive and returns immediately; the action blocks, pooled `IIoPool` work and
the `AsyncDisposeQueue` drain *afterwards*. If the host tears the root provider down while any of
that is in flight, every late continuation resolves from a dead scope.

The correction is **ordering, not guarding**: `MeshTeardownHostedService` drains the mesh root hub
in `StoppedAsync` — after every other hosted service's `StopAsync` has returned, the web host's
included, and still strictly before the host disposes the root provider. `StopAsync` was the wrong
hook and the reason matters: hosted services stop in *reverse registration order*, but
`GenericWebHostService` is registered by `WebApplication.CreateBuilder`, i.e. before anything
`MeshHostApplicationBuilder` can register — so Kestrel, with every live HTTP request and Blazor
circuit, stops *after* us.

That one change is the root of a whole family of separately-filed incidents. Pinned by
`MeshHostBuilderTeardownOrderingTest` and `MeshTeardownRunsAfterEveryOtherHostedServiceTest`.

🚨 **It does not cover an aborted startup.** When `Host.StartAsync` throws, `RunAsync`'s `finally`
goes straight to `host.DisposeAsync()` — no `StopAsync`, no `StoppedAsync` — and the root provider
is disposed with the silo still stopping. So an R1-shaped fault on a pod that never finished
booting is a *different* condition, and the ordering fix is not the answer to it.

### 🚨 Disposing a container is never how a mesh learns it is shutting down (Plugins#2362)

Autofac marks a lifetime scope disposed **before** it disposes anything in it
(`Disposable.Dispose` sets the flag, then runs `Dispose(true)`; even `CurrentScopeEnding` fires on
an already-dead scope). So there is no instant, inside a container's disposal, at which a hub it
roots can learn "shut down" while its resolves still work. The shutdown signal — `IsShuttingDown`,
`ShuttingDown`, and the `HostedHubsCollection.CloseCreation` cascade that carries it through every
hosted descendant — starts at `mesh.Dispose()`. **Whoever owns a container that roots a mesh must
drive the mesh's teardown to `DisposalCompleted` (and the I/O drain) first, and dispose the container
second.** Production does it in `MeshTeardownHostedService.StoppedAsync`; tests do it through
`MeshTeardownExtensions.TeardownAsync`.

The test harness had a path that skipped it. A `MonolithMeshTestBase` class that opts into
`ShareMeshAcrossTests` has its provider held by a collection-scoped `SharedMeshProvider`, which
disposed the provider with the mesh still **live** — no hosted-service stop, no activity quiesce, no
`Dispose()`, no drain. Measured with `EveryHubShutsDownBeforeItsContainerTest` (Graph.Test): at the
first instant the container was dead, the mesh, a hosted child, a hosted grandchild and a
router-minted per-node hub all read `IsShuttingDown=False, RunLevel=Started`. That is the whole of
Plugins#2362's "10 of 11 callbacks read `IsShuttingDown = false`" — 33 `MeshWeaver.AI.Test` classes
share their mesh, and their thread hubs and mesh hubs had their scope killed under them without ever
being told. The cascade was never missing; it was never **started**. The holder now stops the hosted
services it started, runs `TeardownAsync`, traces the report (`DISPOSE_SHARED_MESH_DONE` /
`DISPOSE_SHARED_DIRTY_TEARDOWN` / `DISPOSE_SHARED_MESH_ERROR`) and only then disposes the provider.
The same change starts a shared mesh's hosted services ONCE (the first case that joins it) — the
old path called `StartAsync` on the same singletons once per `[Fact]` and never stopped any of them.

Negative control: with only the `MonolithMeshTestBase` change reverted, the shared-mesh case fails
with all four readings `False/Started`; the per-test case (which always disposed the mesh first)
passes both ways.

What this does not change: a hub's own gate. A callback that can outlive its hub still needs the
`IsShuttingDown || IsServiceScopeDisposed()` probe (see R3 and Plugins' `TeardownSafeCallback`) —
an aborted startup (above) still disposes the root provider with the mesh live, and nothing on
that path can signal first.

### 🚨 A one-shot signal must outlive the container (#5557)

The aborted-startup path has a second casualty that is not an Autofac frame at all:
`ObjectDisposedException` at `AsyncSubject.ThrowDisposed` ← `AsyncSubject.Subscribe` ←
`SubscribeSafe`, reported by the default install as *"First-startup plugin provisioning failed; no
retry is attempted."* The install hops to the thread pool before it subscribes to the bake barrier
(`PreWarmCompletion.Settled`). On an aborted startup no `StopAsync` runs, the container disposes its
singletons in reverse creation order, and the barrier — first resolved from the installer's own
`StartAsync`, so created after it — is disposed first. A queued subscribe that lands before the
installer's own disposal meets a disposed subject, and the installer reports the teardown as its
own failure on a process that is already exiting.

The rule that closes it: **a one-shot replay signal is never disposed.** An `AsyncSubject` owns no
resource; disposing it only converts every later reader into an error. So `PreWarmCompletion` is not
disposable, and `InstanceAutoRegistrationService.Completed` and
`RegistryUpdateReconciler.BootReconciled` are left readable when their owners stop — their owners
dispose their own *subscriptions*, never the signal other services sequence on. A late reader then
sees exactly what an early one sees: the value, or a wait that its own owner's teardown ends. The
module-GC and volume-reading services already followed this rule; pinned by
`AOneShotSignalOutlivesTeardownTest`.

### 🚨 The one site that CANNOT be hoisted, and the stack overflow that proves it

The obvious hardening is to hoist the delivery path's own resolve, and the mesh router looks like
the ideal candidate: `MeshBuilder.Register`'s route handler resolves `IRoutingService` from
`routes.Hub.ServiceProvider` **per delivery**, on the single hottest path in the mesh, and a
delivery still in the pipeline when the provider goes down is exactly R1's frame
(`Unhandled exception in delivery pipeline for hub mesh/{id}`). Route lambdas run in the
`HierarchicalRouting` constructor, so hoisting the resolve there *seems* to put it safely inside hub
construction.

**It does not. That change crashes the process at startup.** Measured: `MeshWeaver.Graph.Test` went
from 27 of 27 passing to `Zero tests ran`, exit **134**, `Stack overflow` with a repeating Autofac
`ResolvePipeline` frame stack. The cycle is structural and closed:

```
mesh hub singleton activating
  → MessageService ctor
    → new HierarchicalRouting(hub, parent)
      → invokes the route lambdas
        → GetRequiredService<IRoutingService>()
          → activates MonolithRoutingService(IMessageHub hub, …)   ← needs the hub
            → re-enters the mesh hub singleton, still activating   ← and round again
```

Both implementations take `IMessageHub` in their constructor (`MonolithRoutingService`,
`OrleansRoutingService`), so **the per-delivery laziness is load-bearing**: it is what breaks a
genuine construction cycle, not an oversight. The resolve stays where it is.

The general lesson, and the reason this section exists: *hoist the resolve* is the correct
correction for a **continuation**, but it is only safe when the service's own construction does not
depend on the thing being constructed at the point you hoist it to. Check the service's
constructor before moving a resolve into a configuration or construction lambda. Hoisting into a
**handler entry** — where the hub already exists — never has this problem, which is why every fix
in the inventory below lands there and none of them lands in a `WithRoutes` or `WithInitialization`
body.

## R2 — a dying hub handed out as the live hub (fixed)

`HostedHubsCollection.GetHubWithOutcome` answered a registry hit with
`HostedHubOutcome.Available`, unconditionally. A hosted hub leaves that registry through a
`RegisterForDisposal` callback that runs inside `MessageHub.DisposeImpl` — several phases *after*
`Dispose()` set `IsDisposing`, and several **statements** after the hub stops being able to serve:
the ShutDown phase flips `RunLevel`, then runs `CancelCallbacks`, then the reactive dispose
actions, and only then the registrant walk that carries the removal. From the flip on, the hub
refuses every delivery at intake and a direct `Observe(...)` faults synchronously with
`ObjectDisposedException` (`GetOrAddResponseSubject`, `RunLevel >= ShutDown`). A teardown that
wedges anywhere between the flip and the removal — a registered cleanup blocking on a lock, a
response continuation that never returns — never reaches the callback at all, so the window was
**not bounded**.

Measured shape: the mesh's single node-operation execution hub `portal/nodeops-{meshId}` —
resolved through `NodeOperationExecutionHub` with `HostedHubCreation.Always` — was handed to **four**
webhook deliveries at three distinct times spanning **55 minutes** (09:01, 09:49, 09:56) on one pod
that was healthy and serving other traffic throughout, each throwing out of the HTTP endpoint as a
500. A pod drain cannot account for that span — the Kubernetes grace ceiling ends one long before —
so the mesh was not tearing down; one hosted hub had stopped working and was never evicted.

### The fix: retire the corpse, mint a successor, keep the corpse in the join

An `Always` lookup that finds a registered hub at `RunLevel >= ShutDown` no longer answers it. It
**retires** the corpse — a value-matched removal from the registry, so a successor a concurrent
lookup has already registered is never evicted — and falls through to the ordinary single-flight
creation, which mints a fresh hub under the address. The retired hub moves into a second,
reference-keyed set the collection still OWNS: `Hubs` enumerates it, the owner's stall detector
still sees it, and `DisposeHubsReactive` **joins on it** exactly as on the registered hubs, because
its Autofac scope is a child of the owner's and an owner that finished ahead of it would close that
scope under a hub still walking its registrants — R1's straggler class, manufactured locally. It
leaves that set on its own `DisposalCompleted`. The retirement is reported at **Warning**
(`[HOSTED-RETIRE]`): the ordinary path never reaches it — a lookup landing in the microseconds
between the flip and the removal is the only benign way there — and every other way is a teardown
that has stopped making progress inside its ShutDown phase, which this line is now the first to
name. Pinned by `AHubAtShutDownIsNotHandedOutTest`, which parks a hub's ShutDown turn inside a
reactive dispose action (before the registrant walk), asserts the corpse is still registered, and
measures both halves: the lookup answers a successor, and the owner's teardown does not complete
until the corpse is released.

🚨 **The bound is `ShutDown`, deliberately, and not `IsDisposing`.** Between `Dispose()` and
`ShutDown` the hub is still a live poster and a live router draining the work it *accepted* —
`Quiescing`, then its children — and the intake gate already answers a new request there with a
typed, transient `ShuttingDown` NACK the caller re-asks on after `DisposalCompleted`. That is the
documented recycle shape ([Hub Disposal Model](../HubDisposalModel), *the creation window*), and
it holds because a re-ask that lands on the still-dying instance costs one bounded retry. Minting a
successor in those phases would put **two activations on one address with accepted writes still in
flight on the first** — a successor loading a node the predecessor is about to persist — which is
a lost-update race the platform has never had. At `ShutDown` nothing accepted remains: the
callbacks are cancelled, the children are dead, and the only thing left is the corpse's own
registrant walk — the same overlap the ordinary path has always had between that walk and `Dead`,
now merely longer when the walk wedges. `Never` probes keep finding the corpse: they are the
router's, and a delivery into it is refused with the transient NACK — the right answer for a
*message*, and the wrong one for a *caller holding the reference*, which is what `Always` means.

### The three removals, all value-matched

Retire-and-replace is only safe once a predecessor's teardown cannot erase its successor, and a
hosted hub's teardown removes itself from **three** registries:

1. the hosted-hub registry — `Track` arms `TryRemove(KeyValuePair(address, hub))` on every insert
   (**#4741**);
2. the routing service's local route — `RegisterStream`'s disposal removes what it registered, in
   `OrleansRoutingService` and `MonolithRoutingService` alike, `subscriptionReady` included
   (**#5159**). The Monolith's route-creation path also armed a *second*, key-only removal
   (`UnregisterStream(address)`) on top of the hub's own registration; it is gone — it ran in the
   same registrant walk and would have erased the successor's live route, taking the address dark
   on that host with nothing to grep;
3. the **pod-hub cluster claim** — `IPodHubGrain.Detach` stamped a *terminal* `Released` tombstone
   on the address for ten minutes regardless of who held it, which the router reads as
   `NotFound` + `TargetUnserved`, the verdict that triggers owner-side stream eviction. A
   predecessor's `Detach` landing after a successor's `Attach` would have evicted the **live** hub
   as a corpse: strictly worse than the 500 it set out to fix. The discriminator is the silo's own
   route table: `RegisterStream`'s disposal removes its route *before* it releases the claim, so a
   live local route present when the grain processes a `Detach` can only be a successor's, and the
   grain — not `[Reentrant]`, so the read is ordered against the successor's `Attach` and every
   `Deliver` — leaves the claim as the successor left it. No grain-contract change, so nothing to
   roll in two releases. Pinned by `PodHubStaleReleaseTest` on the real two-silo cluster, both
   sides: a stale release lands on a live successor (the delivery is forwarded and the pin stays
   at `MaxValue`), and a release with no live route still tombstones (the #2426 remedy stands).

The `podHubClaimSettled` test seam followed the same rule while it was open.

### Why refusing the dying hub is still the wrong fix

Answering `null` + `HostShuttingDown` reads correct and is not: **25** call sites (measured over
`src/` and `memex/`, comments and declarations excluded) use the two-argument `GetHostedHub`
overload, which is `null`-forgiving — it forwards to the three-argument form with `Always` and a
`!` — and is dereferenced accordingly. Several assign the result straight into a non-nullable field
or return it from a method whose return type is non-nullable (`Activity`, `PortalApplication`,
`SessionHubFactory`, `PartitionStorageRouter`, `MeshNodeStreamCache`, `StaticRepoImporter`).
Refusing would convert an attributable `ObjectDisposedException` into an unattributable
`NullReferenceException` in code that works today. That is also why the lookup keeps answering the
corpse while its *own collection* is disposing: no successor can be minted there, and the
attributable throw is the honest answer.

### What this does NOT fix

The wedge itself. A hub that sits at `ShutDown` for 55 minutes is **#4883**'s shape — a registered
cleanup blocking the ShutDown turn — and retiring it lets callers past it; it does not free the
turn. The `[HOSTED-RETIRE]` line names the address and the phase, so the next occurrence points at
the wedged hub instead of at whoever asked for it.

### 🚨 And the other half: retire-and-replace acts on the ADDRESS, so it cannot reach a CACHED reference

Retire-and-replace fixes the LOOKUP. It takes the corpse out from under its address so the next
caller to *ask* gets a successor — which does nothing at all for a caller that asked once and put
the answer in a field. That caller keeps posting into the corpse, and a hub past `Started` serves
nothing: its intake refuses every delivery and a direct `Observe(...)` faults synchronously. The
line it produces is

```
Hub portal/nodeops-{meshId} is shutting down — cannot register new response subject for {id}.
Object name: 'MessageHub'.
```

and it repeats for as long as the holder lives, with nothing short of a process restart to recover
it. Measured on the control instance: a node-operation hub wedged then shutting down, after which
every `Create` was answered that way for ten minutes and more.

**The discriminator is what the cached value IS.**

| cached value | safe to cache outright? | why |
|---|---|---|
| an **Address** (`MeshService.NodeOperationTarget`) | **yes** | the address is what is stable; the successor is minted at the same one |
| a hub from an **issuing seam** (`NodeOperationIssuingHub`, `ReadIssuingHub`, `MeshReadHub`, `StreamSubscribingHub`, `GetHostedHub`) | **no** | it resolves-or-creates at an address, so its answer can be SUPERSEDED — and nothing tells the holder |
| the **parent** hub (`MessageHubConfiguration.ParentHub`) | **yes** | a hub's parent is never replaced underneath it: the parent's lifetime strictly CONTAINS the child's, since the child is a hosted hub the parent tears down in its own `DisposeHostedHubs` phase. There is no successor to pick up, and re-resolving once the parent winds down would call `GetService` on a scope that may already be disposed — the fault that property's own remarks warn about |

So a cached seam answer is **revalidated at the read**: return it while its `RunLevel <= Started`
and it is not `IsDisposing`, resolve again otherwise. Below `ShutDown` that deliberately resolves
the SAME hub — the registry answers an existing-hub lookup with it, because it is still draining
accepted work — so the caller gets the documented transient `ShuttingDown` NACK exactly as before,
and only past `ShutDown` does it get the successor. Nothing re-posts and nothing polls: it is a
cache-validity check, O(1), at the one place the reference is read.

Both sites in `src/` now do that — `MeshService.IssuingHub` / `MeshService.UsableIssuingHub`, and
`MeshOperations.ReadHub` / `MeshOperations.UsableReadHub` —
and `CachedHubReferenceGuard` holds the tree at zero — keyed on the right-hand side being a seam
call, with its detector asserted in both directions (it fires on the two pre-fix lines verbatim, and
stays silent on the address cache, on the revalidating form, and on `ParentHub`). The guard's first
draft keyed on the field TYPE alone and flagged `ParentHub`, which is the row above: that is why the
rule is about a seam's answer and not about every hub-typed field.

*(An earlier revision of this page carried a lead that `HostedHubsCollection.Add` was unreferenced
in `src/`, which would have meant a hosted hub never left the registry at all. It was settled by
#4741: `Add` is called from `MessageHubConfiguration.Build`, the first statement after the hub is
constructed. The general lesson stands — a grep over `src/` never sees in-mesh source or a
NodeType's `configuration` lambda inside its JSON, so "unreferenced in `src/`" is strictly weaker
than "unreferenced".)*
## R3 — a continuation resolving from a scope its own pipeline closed

This one has nothing to do with shutdown. A handler resolves a service **inside** a reactive
continuation — a `.Do`, `.SelectMany`, `.Subscribe`, `.Catch` or `Observable.Defer` body — and by
the time that body runs, the hub scope it resolves from has been closed, often by the very
operation the continuation belongs to.

The recursive delete is the worked example. `HandleDeleteNodeRequest` runs on the node's own hub;
its commit stage disposes the per-node hubs of the paths it removes; and continuations after that
resolved `IoPoolRegistry`, `IMeshChangeFeed` and `IMeshNodeStreamCache` from those scopes. The
throw lands at **resolution**, which is outside every per-item `.Catch` in the pipeline, so the
whole delete aborted — observed in production as `[DeleteNode] unexpected path=… partial-deleted=0`
for three separate subtrees, each a silently failed delete.

**The correction is to hoist, never to guard.** These services are mesh-lifetime singletons: only
the child scope used to look them up is short-lived, so the captured instance is the same object
the continuation would have resolved and stays valid for the whole operation. Guarding the
resolution instead (`?.` on a null service) silently skips the side effect — here, the change
publish and the stream-cache invalidation, which is how a deleted node keeps being served from a
`Replay(1)` entry. The repo already states the rule in `MeshTeardownExtensions`: *capture
mesh-scoped services while the scope is still alive — never resolve DI once disposal has begun.*

🚨 **The hoist is COSMETIC when the service's own lifetime is keyed to the thing being torn down, and
that case is invisible to this root's sweep.** Hoisting changes **when** the container is asked, never
**which** container answers. For a mesh-lifetime singleton those are the same question, which is why
the correction above works. For a service registered **scoped per hub** they are not: the hoisted
instance *is* the short-lived thing — it was constructed with that hub and reaches back through it on
every later call — so the throw simply moves from the continuation into the service's own method body,
where no call-site hoist can reach it.

Measured on [#5099](https://github.com/Systemorph/MeshWeaver/issues/5099): the recycle cascade's
`DependencyNetwork` **already** resolved its services eagerly in its synchronous prologue — this root's
prescribed correction, applied before this page existed — and still threw. `IMeshService` is
`AddScoped` (`PersistenceExtensions.cs:720`, `:797`), and `MeshService.StampViewer` →
`CaptureContext()` does `hub.ServiceProvider.GetService<AccessService>()` on **every** `Query<T>`.

So the test is **not** *"is this resolve inside a continuation"* and **not** *"is it hoisted"* — it is:

> **What is this service's lifetime keyed to, and does this pipeline outlive it?**

Where the answer is "the thing being torn down", the remedy is not to move the resolve earlier but to
move the **work** to something that outlives the teardown — for a read, the mesh's read-issuing hub
(`MeshExtensions.ReadIssuingHub`). That is the same shape as the established rule that a recycle's
caller must outlive its target, applied to reads instead of posts; worked through in
[Enumerating from a Survivor](../EnumeratingFromASurvivor).

🚨 **This is why the inventory below cannot triage it.** The sweep grades by CALL-SITE SHAPE, which is
lexical; service lifetime is a property of a registration in another file, so no grep of a continuation
can see it. Measured on `src/`: **12** types are registered `AddScoped`/`TryAddScoped`
(`IWorkspace`, `IMeshService`, `IContentService`, `IUiControlService`, `IChatCompletionOrchestrator`,
`IDataValidator`, `INodeValidator`, `IAutocompleteProvider`, `IAutocompletePrefixRegistry`,
`IContentCollectionConfigProvider`, `Data.IFileContentProvider`, `SyncStreamActivationLedger`), with
about **142** resolve sites between them — **118** of those `IMeshService`, the one already proven to
reach back through its hub. How many fall inside the 204 graded sites is **not yet established**; the
cross-reference is the open question. Until it is done, a site "fixed" by the hoist alone can pass
review, pass a hoisting-shaped guard, and still throw in production.

A second, independent correction belongs with it: **a diagnostic must never be able to prevent the
cleanup it describes.** `ReleaseNodeTypeLease`'s error arm resolved a logger *before* releasing a
collectible-ALC lease, and that arm is reached only when `meshHub.IsDisposing` is already true — so
the resolve threw, the Rx error handler rethrew, and the lease was held for the process lifetime:
exactly the load-context leak the mechanism exists to bound. The release now happens first.

🚨 **And that site is the counter-example to the hoist**, which is why it is worth stating as its own
rule. Hoisting its resolve to the top of the method — the correction applied everywhere else above —
makes it *worse*: the same possible throw then sits ahead of the release on **every** path, including
the normal-completion path that resolves nothing today, turning a lost log line into a guaranteed
leak. So the test is not "is this resolve inside a continuation" but **"what does this resolve stand
in front of"**. Where it stands in front of a cleanup, reorder rather than hoist, and let the
diagnostic be the thing that is allowed to fail. `HostedHubsCollection.ReadOwnerCause` is the
established precedent: a best-effort teardown diagnostic, read where it is needed, never permitted to
fault the teardown it describes.

### The swept inventory

A lexical sweep of `src/` and `memex/` (scope-aware: comments and every string form stripped, a
frame-stack walk, zero brace-balance failures over ~2,700 files) found **1,182** textual
`ServiceProvider.Get*` hits, **264** lexically inside a lambda, and **204** in a real
reactive/callback continuation. Those 204 grade as:

| Tier | Count | Definition |
|---|---|---|
| **T1** | 32 | on a teardown / delete / recycle path, or in an error / `Catch` / timeout arm, or on a hub the surrounding code already says may be gone |
| **T2** | ~104 | same shape on a per-node, layout-area or probe hub — plausibly reachable after the scope closes |
| **T3** | ~68 | lexically deferred but structurally safe: hub-construction and configuration lambdas, delivery-pipeline and message-handler lambdas, and receivers that are the process-lifetime root hub |

Fixed so far: the three delete-pipeline sites and the lease-release arm. **The rest is known,
sized, mechanical work, not a mystery** — the fix is the same hoist at every site. Three adjacent
classes the lexical sweep deliberately does not cover, and which a future pass must:

- **Lazy accessors** — ~60 expression-bodied members and `Func` fields whose body resolves from a
  hub provider, so any call *from* a continuation is a deferred resolve that no grep of the
  continuation can see.
- **Resolves inside `Dispose()` bodies** — ~12. Eager at method entry, so outside the lexical
  definition, but they resolve *during* teardown, which is the same throw.
- **Non-lexical deferral** — method groups passed as continuations, local functions called from
  lambdas, and helpers that resolve at their own entry but are only ever invoked from a
  continuation. Finding these needs a call-graph pass.

## R4 — a drain budget expiring over live work

Two participants bound the silo stop, and both report honestly when they give up:
`RoutingQuiescenceSiloParticipant` (stage `Active`, stops first) waits 30 s for in-flight route
legs; `IoPoolSiloTeardown` (stage `First`, stops last) waits 30 s for pooled I/O leaves. On expiry
each says so at `Error` and names what it can.

🚨 **Neither budget may be widened.** Both log sites already say it, and it is the whole point: a
leg that cannot land in 30 s is *stuck*, and a leaf that does not settle on its `CancellationToken`
ignores it. The diagnostic halves are built — the quiescence report names up to ten stuck legs, and
the pool report prints the residuals that did not report — so the next occurrence is a pointer
rather than a reconstruction. **What is not established is the subject**: no occurrence so far
carried enough to name the offending leg or leaf, and guessing one would be worse than waiting for
a report that names it.

### A named leg still needs its AGE

The route side has named its legs since #2843, and three occurrences on
[#2833](https://github.com/Systemorph/MeshWeaver/issues/2833) printed populated lists
(`stream-routed → cache/…`, `dispatch → RiskTransfer`, five × `dispatch → Ops/Status/partnerre`) —
and still could not be read, because **every leg's own terminal bound is at least the 30 s hold
budget**: path resolution (`RoutingGrain.ResolveTimeout`, 30 s), an Orleans grain call's response
timeout (30 s per attempt), a memory-stream post (`StreamPostTimeout`, 60 s). A label at expiry is
therefore one of two opposite facts:

| each leg's printed age | what it means | where to look |
|---|---|---|
| about the budget (30–40 s) | accepted just before the stop, still inside its own bound | why that leg's bound is not shorter than the stop — the grain it waits on (an activation still resolving its node), or the stream post it waits on |
| minutes or more | a slot that stopped coming back long before the stop began | the leg itself: its label is the defect, the same leak the saturation report's `oldest leg` names at run time |

So `RoutingQuiescence.InFlightSample` prints each leg as `"{label}, in flight {s}s"` and **oldest
first**: the sample is capped at ten, and a leaked leg is by definition the oldest, so an unordered
sample could hide exactly the one that matters behind `(+N more)`. It is the same reading
[Reading a Routing Saturation Report](../ReadingARoutingSaturationReport) takes from
`OldestInFlight` at run time, now carried by the shutdown residual too.

### What a STATIC sweep can settle, and what it cannot

The pool side of R4 has one property the log side does not: *"a leaf that ignores its cancellation
token"* is a **lexical** property of a call site, so the class can be swept without waiting for an
occurrence. `Invoke` is the entry point with no projection of its own — and `IoPoolExtensions.Run`
composes straight onto it, so it inherits that — while `InvokeObservable` waits through
`ObserveCompletion(…, ct)`, `InvokeStream` enumerates `.WithCancellation(ct)` and `InvokeBlocking`
holds no permit. The sweep of `src/` for a lambda on either unprojected entry point that never
references its own token parameter returned **exactly one** site: `OrleansRoutingService`'s stream
teardown, `ioPool.Invoke(_ => subscription.UnsubscribeAsync())` on the `RoutingStream` pool —
Orleans' `UnsubscribeAsync` takes no token, so nothing the drain cancels could ever settle it. It
now projects the token onto the wait and reports the abandonment, and
`PooledLeafObservesItsTokenGuard` holds the tree at zero. The token-less-API clause is in
[Controlled I/O Pooling](../ControlledIoPooling).

🚨 **That does NOT name the subject of the production occurrences, and must not be read as having
named it.** The sweep is static and every occurrence so far is the bare line with no pool, no site
and no stack, so the two can only be joined by a reading — not by argument. What the fix changes is
that the next occurrence becomes a **discriminator** rather than a repetition: `Did NOT report:`
either names `RoutingStream` (this was it, and the fix is on the image or it is not), or names a
different pool and call site (the subject is elsewhere, and now it is named), or is empty (a defect
in `IoPoolRegistry.Dispose`'s `Zip`, not a leaf at all). One fixed instance of a class is not a
measurement of the class's population in a running portal.

## R5 — Orleans dropping deliveries at silo stop

`PlacementService` refuses to address a message to a silo that is shutting down, after Orleans has
already forwarded once. This is upstream behaviour during a rolling deploy, not a MeshWeaver
defect. The MeshWeaver-side question it raises is a real one and is separate: whether a dropped
`IMessageHubGrain.DeliverMessage` is surfaced to its sender or silently lost.

## Reading a burst before you change anything

1. **Correlate with pod lifecycle.** Distinct pods inside one deploy window ⇒ R1. One pod over tens
   of minutes while it serves other traffic ⇒ R2. No deploy anywhere near ⇒ R3.
2. **Read the frame below the Autofac frames.** Upstream code ⇒ R1/R5. A lambda of ours under an
   `ObservableImpl` frame ⇒ R3.
3. **Ask whether the faulted work was still needed.** A teardown race that loses nothing is a
   *classification* problem, and the fix is the log level plus a named outcome — never a swallow.
   `MessageHubGrain` does exactly this: `HostedHubOutcome.HostShuttingDown` reports at `Debug`,
   everything else — `Unclassified` included, because unknown is not a shutdown — stays at `Error`.
   That is legitimate only because the condition is *measured*: `IsHostContainerDisposed()` asks the
   container directly whether it can still resolve, and if the probe itself throws the CLR treats
   the filter as false and the loud branch runs. A classification that *assumes* the race is benign
   is a swallow wearing a log level.
4. **Never widen a budget, and never wrap the resolve in a catch.** Both convert a diagnosable
   failure into a silent one.

## See also

- [Enumerating from a Survivor](../EnumeratingFromASurvivor) — R3's companion clause: why the hoist is
  cosmetic for a per-hub SCOPED service, and why that case is invisible to R3's lexical sweep
- [Teardown Layers](../TeardownLayers) — the two-layer model and the stall verdicts
- [Hub Disposal Model](../HubDisposalModel) — phases, gates, and what a `DisposeRequest` does
- [Reading a Disposal Stall Verdict](../DisposalStallVerdicts) — the M1/M2/M3 discriminators
- [Mesh Lifecycle](../MeshLifecycle) — the mesh-level drain order
- [Controlled I/O Pooling](../ControlledIoPooling) — the token contract a pooled leaf owes
- [Stale State Until Recycle](../StaleStateUntilRecycle) — why an address is re-activated, not re-read
