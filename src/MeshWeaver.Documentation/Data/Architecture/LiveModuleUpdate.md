---
Name: Live Module Update
Category: Architecture
Description: A module update goes live in the running process by default — each module in its own collectible load context, the old generation swapped out and unloaded — and only a declared or failed live swap falls back to an automatic restart. What is shipped, what is owed, and how each module is classified.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-3-6.7"/><path d="M21 4v5h-5"/><path d="M12 8v4l3 2"/></svg>
---

# Live Module Update

> **The rule (policy `module-live-update-default`, [register](../PolicyNotProse)).** A module update
> goes live **in the running process**: generation N+1 loads into a fresh collectible load context,
> the hubs bound to N are disposed and re-instantiate on N+1, and N's context is unloaded. That is
> THE update path for every module — no flag, no opt-in, no governed activity, no approval. A
> restart is the exception, and it is taken only when (a) a module **declares** `restartRequired`
> with a reason, or (b) the live swap **fails at runtime** — and then it is automatic, with N
> serving until it happens. A module updates **independently of the platform**: it never needs a
> new image, a platform roll or a seal for the running platform's identity.

## Why this replaces restart-as-activation

Until this change every landed module was `Assembly.LoadFrom`-ed into the **default** load context
([Modules](../Modules), "Activation is restart-based"). The default context cannot unload an assembly
and holds one copy per simple name ([Module Generation Substitution](../ModuleGenerationSubstitution)),
so no module could change inside a running process: every update raised `pending_module_activation`,
announced `self-update-restart-pending`, and waited for a restart. On 2026-10-05 that wait was the
incident — AI 1.21.0 landed at 05:30Z while the process ran 1.20.4, and every NodeType compiled
against the new member failed with `CS0117 'ThreadPreparation' does not contain a definition for
'Group'` until somebody filed a restart by hand.

## The four governing rules

1. **Live by default.** Every module the image does not bind runs in its own collectible
   `ModuleLoadContext`; an update swaps the generation in place. Nothing has to be enabled.
2. **A restart is DECLARED, never assumed — and only for boot-time infrastructure.** A module that
   cannot be swapped in-process carries `[assembly: ModuleRestartRequired(ModuleBootCategory.X, "<why>")]` — on its own assembly, so the declaration travels
   with the bytes it describes — and `ModuleLiveUpdateGuard` fails any module that blocks a live
   swap without it (or declares it with a blank reason). The runtime honours the declaration even
   where the measurement sees nothing (process-wide state, an owned thread). *(Guard shipped; running
   it over every module a satellite ships is slice 3.)*
3. **Try live first; fall back only on a measured failure.** When the swap fails at runtime — the new
   context will not load, a contribution cannot be re-applied, a hub cannot re-instantiate, the old
   context does not unload inside its bound — generation N keeps serving (never a half-swapped
   state), the reason is recorded by name, and an automatic restart loads N+1. The restart is the
   self-update lane's routed Restart, which needs no confirmation and no approval (MeshWeaver#4607:
   the routed action carries `origin: self-update` and is accepted only on a node created by
   `system-security`).
4. **Modules are independent of the platform.** A module bundle resolves every platform contract
   from the running platform and never bundles one; it is admitted by its declared floor against the
   running platform (`PlatformFloor`, policy `package-min-mesh-version`), never by framework-identity
   equality; an image's baked module copy is only a boot baseline that a newer published generation
   supersedes live. A platform roll is needed only for a platform change.

## What is shipped (slice 1 — the load contexts)

| Piece | What it does |
|---|---|
| `ModuleLoadContext` (`MeshWeaver.Mesh.Contract`) | Collectible context for ONE generation of ONE module, named `module:<Name>#<n>`. Resolves **platform first** (anything the default context can bind — one `MeshNodeProviderAttribute`, one `IMessageHub` in the process; a bundled platform copy is never used), then **another module** (the current generation of a module whose entry assembly is asked for, or an assembly a module it depends on already holds — each edge recorded as `DependsOn`), then the generation's **own directory**. Natives resolve through the same candidates as `ModuleNativeAssets`. Marked `IPlatformLoadContext`, so the impersonation guard classifies module code with the platform. Purges System.Text.Json's process-static cache on `Unloading`, as a NodeType context does (the DI container keeps none). |
| `ModuleContexts` | The mesh's registry (a mesh singleton, disposed with the mesh): `Load` a generation into a fresh context, `Commit` it as current (returning the one it replaces), `Retire` the old one — unloaded on a POSITIVE quiescence signal from its `AlcLeaseRegistry`, never on a timer, and recorded on `CollectibleContextUnloads` so "really collected" is observable. `Resolve(name)` is the explicit lookup a NodeType build uses (a module's entry assembly); `ResolveDependency(assemblyName, among)` is the one a kernel script session uses — the entry assembly, or one of a module's private dependencies bound through that module's context and returned with a lease on it (see *Kernel scripts and a module private closure* below); `DependentsOf(name)` is the set a swap has to take with it. |
| `MeshBuilder.InstallModules` | Loads every module the image does not bind into its own context and commits it only after its contributions materialised. A generation whose contributions throw is unloaded and the serving one stays current. Because a failed generation never takes its name in the default context, the previous generation and the image copy stay reachable for the fallback (#3649, #3735). |
| `NodeAssemblyLoadContext` | Binds a module's entry assembly through `ModuleContexts.Resolve` — its CURRENT generation. |
| The kernel `ScriptSession` (`ModuleScriptBindings`) | Pins the module generations current when the session builds its reference set, compiles against them, binds a module's entry assembly or private dependency from them through `ModuleContexts.ResolveDependency`, and keeps every module context it binds into leased until the session ends. |

🚨 **Never a `Default.Resolving` handler that hands out a module assembly.** The default context
caches a binding for the life of the process, so the first generation it saw would be pinned forever
and no swap could take effect — and the runtime refuses a non-collectible assembly binding a
collectible one in any case. Every context that needs a module asks the registry explicitly.

**What stays in the default context.** A module whose entry assembly is in the application's own
closure (`TRUSTED_PLATFORM_ASSEMBLIES` — `ModuleContexts.IsImageBound`) is bound by name for every
platform assembly that references it; a second copy in its own context would split its identity. It
loads into the default context exactly as before, and it is what a live update cannot reach until the
image stops shipping it in its closure.

**Tests** (`MeshWeaver.Compiler.Pipeline.Test` → `ModulesRunInTheirOwnContextTest`, real Roslyn emits
and real collections): an installed module runs in a collectible `ModuleLoadContext` and still binds
the one platform; two generations coexist and the retired one is really collected — and its
**negative control** holds one instance of the retired generation and must see it reported RETAINED
by context name (that control caught a first version of the retirement sentinel that reported
"collected" the moment the unload started, because `Unload()` swaps the `Unloading` delegate out); a
dependent binds its dependency's current generation and is recorded as its dependent; a generation
whose contributions throw leaves the serving one current; and a NodeType compiled against a member
only N+1 has fails to bind while N serves (the incident's shape, the negative half) and binds once
N+1 is current — in the running process.

### Kernel scripts and a module private closure

A module ships its private closure beside its entry DLL in its generation directory —
`MeshWeaver.Mail.MicrosoftGraph` carries Microsoft.Graph and Kiota, which the image does not. A kernel
script that uses a type from that closure is resolved twice, and both halves must give the same
answer: **whatever compiles must load.**

| Half | Where | How a module's private dependency resolves |
|---|---|---|
| Compile | `SharedScriptMetadataResolver` → `KernelScriptReferences.TryResolveByIdentity` | The session PINS the module generations current when it builds its reference set (`ModuleScriptBindings.PinCurrent`), and each pinned generation's entry DLL is a session reference. A dependency it references is found, in order, as a **non-collectible** loaded assembly of that name (the platform), else as the **file beside the referencing DLL** — the module's generation directory — else by Roslyn's own resolver. Collectible assemblies are never taken from the live list (a superseded generation would be indistinguishable from the current one). |
| Run | `ScriptSession`'s load context → `ModuleScriptBindings.Bind` → `ModuleContexts.ResolveDependency(name, pinned)` | After the session's own submissions and its declared cell-surface packs, from the PINNED generations only: (1) a module's **entry assembly**; (2) an assembly a module's context **already holds**; (3) a **file of that name in the module's generation directory**, loaded through **that module's context** — whose own order puts the platform first, so a name the default context can bind comes back as the platform's copy and the session falls through to the default context for it. Modules are visited in name order; a candidate whose version is lower than the one requested is skipped. Anything else falls through to the default context. |

Because the run half loads the dependency through the module's own context, the script and the
module's code share ONE identity for its types: a value the script builds can be handed to the
module, and back. The default context never sees the module's directory, and must not — no
`Default.Resolving` hook hands out module code (see above).

**One generation for compile and run.** Pinning is what keeps the two halves on the same code: a
live swap between building the reference set and the first run-time bind would otherwise make a
submission compile against N and run against N+1. A pinned generation is used only while it is still
CURRENT: one a swap displaced before the session bound it may already be unloading, so the session
does not bind it (the load fails, and a new session binds the new generation). The pinned set is held
weakly, so pinning never keeps a retired generation loaded.

**The lease.** `ResolveDependency` leases a candidate generation on the registry's `AlcLeaseRegistry`
BEFORE it loads anything from it, and re-checks that it is still current after taking the lease.
`Retire` removes a generation from the current set before it waits for quiescence, so a lease that
still sees it current is one the retirement waits for; nothing is ever loaded from a context that has
begun unloading. `ModuleScriptBindings` keeps one lease per bound context until the kernel session
ends (the executor hub is disposed), because what a submission leaves behind — a subscription, a
rendered control, a stored delegate — can call into the module after the submission returned. A swap
of a bound module therefore retires the old generation only once the session has ended; a session
that outlives the swap's retire budget leaves the old generation loaded rather than unloaded under it
(the leak-over-crash rule of `AlcLeaseRegistry`).

**Tests:** `KernelScriptRunsAgainstAModulesPrivateDependencyTest` (`MeshWeaver.Compiler.Pipeline.Test`)
installs a module whose generation directory ships a private dependency at version 6.2.0.0, runs a
Code cell through `ExecuteScriptRequest`, and requires the Activity to succeed with the module's own
code having received the script's value of the dependency's type. `ModulesRunInTheirOwnContextTest`
pins `ResolveDependency` (entry assembly, private dependency through the module's context with the
module's identity, each with its lease; nothing, and no lease left behind, for the platform, an
unknown name or a higher version), the session lease (held while bound, released at the session's
end, nothing bound after it), and the pinning (a session pinned to N binds neither N+1 nor a
displaced N it had not leased; one that bound N keeps N leased across the swap; a new session binds
N+1).

## What is shipped (slice 2 — the swap, and live-first activation)

| Piece | What it does |
|---|---|
| `ModuleContributions` (`MeshWeaver.Mesh.Contract`) | One generation's materialised contributions, and `LiveUpdateBlockers()` — the declared `[ModuleRestartRequired]` reason plus what the platform MEASURES it cannot re-apply in-process: root services through `WithGlobalServiceRegistry`, the mesh hub's configuration (`HubConfigurations`), address types, the builder hook, HTTP endpoints. Nodes and every-per-node-hub configuration ARE re-appliable. |
| Re-appliable seams (`MeshBuilder`, `StaticMeshNodeListProvider`) | A module's nodes are served from its CURRENT generation in the seed tier, at the position its boot nodes held (same precedence as before). Its every-per-node-hub configuration goes through one indirection read when a hub is built. `InstalledModuleAssembly` for a module in its own context is answered from the `InstalledModules` registry at every read with the current generation (absent once withdrawn or retired), so the compile reference set (`MeshNodeCompilationService`, now keyed by the module MVIDs) and `InstalledModulesFingerprint` (now read live) follow a swap — and every build stamped with the old fingerprint reads as stale and rebuilds against N+1 on its next activation. 🚨 None of these registrations captures a module's boot `Assembly` strongly: one did, and it rooted generation N for the life of the process (`ModuleLiveSwapTest`'s collection assertion caught it). |
| `ModuleLiveUpdater` (`MeshWeaver.Graph`) | The swap: load N+1 → refuse (N untouched) on any blocker of N, N+1 or a dependent, or a load / materialisation failure → load N+1 and re-load every dependent into ONE `ModuleSwapStage` (a staged context binds the STAGED N+1, so the dependents re-bind before anything is current) → only when all of them loaded, materialised and prepared, `CommitAll` makes the whole set current in one step; a failure before that unloads the staged set, which no request was ever routed to, and N never stopped serving (#6128 review: committing N+1 first published it to every reader of the generations while a dependent could still fail) → recycle this process's per-node hubs bound to the module's NodeTypes or nodes (every per-node hub when a generation configures every hub, or when an in-mesh build here is linked against a swapped module) → once those hubs are DEAD, retire the old generations. Serial (a subject and `Concat`, no gate), so a second update that arrives mid-swap is applied after it. Outcomes are an open vocabulary (`ModuleSwapKind`: `Live`, `UpToDate`, `RestartRequired`, `Failed`, `NotHeld`); `NeedsRestart` is the one question a caller asks. **`Swap(path)` is `internal`** (granted to `MeshWeaver.PluginCatalog` only): it runs the bytes at a path as platform-classified code, so a public method on a mesh singleton would let any code that resolves it name the bytes or roll a module back to an older directory. The only way in is `ModuleLiveActivation.ActivatePending`, which takes no path and swaps only the pinned copy of what the landing service landed. A generation's contributions are recorded **before** it is made current, so no reader ever sees a current generation whose contributions are null. |
| `ModuleLiveActivation` (`MeshWeaver.PluginCatalog`) | For every module `PendingModuleActivations` reports landed-but-not-serving in THIS process, swap the PINNED copy of its landed generation in live — all of them as ONE swap (`SwapAll`), so the pass recycles each hub once (see *One landing, one recycle wave*). A module this process does not hold in its own context is `NotHeld`. A pass that cannot read the state keeps the restart. |
| `SelfUpdateHostedService.ConsiderRestart` | 🚨 **Live first.** Where a pending activation used to mean a restart, the check now calls `ModuleLiveActivation`; only when something did not go live does it take — or hand to the control lane — the automatic restart, whose announcement now names the modules and reasons. The verdict `ActivatedLive` records a pass that SWAPPED something in and needed no restart; a pass that found the generation marked while nothing is pending in this process is `NothingToActivate` and carries the reader's own line — declined in favour of the image's copy, quarantined, a previous generation — never "went LIVE" (MeshWeaver#6620: that sentence was reported while the old assembly kept serving). A host without the plugin catalog keeps the old path. |

**Tests.** `ModuleLiveSwapTest` (`MeshWeaver.Compiler.Pipeline.Test`, a running monolith mesh): N→N+1
goes live and N's context is really collected (negative control: hold one reference into N → reported
RETAINED by name); a second update mid-swap is applied after it and the newest serves; a read in
flight across the swap is answered; a mesh-hub-configuring N+1 is refused with N serving; an N+1 whose
contributions throw is a `Failed` swap with N serving; a declared `[ModuleRestartRequired]` is never
swapped and its reason is the answer; and a NodeType written against a member only N+1 has fails to
compile on N (CS0117 — the incident, and the negative half) and compiles after the swap, in the same
process. `ModuleLiveUpdateGuardTest`: nodes-only passes; an undeclared blocker FAILS naming the module
and the measurement (the guard's negative control); a declared one passes; a blank reason fails.
`ModuleUpdatesGoLiveTest` (`Memex.Portal.Shared.Test`, the real landing path and the real self-update
decision): a live-updatable update goes live with NO restart; a restart-required update and an
injected live failure each schedule EXACTLY ONE automatic restart with N serving and the reason
recorded; two updates before the restart are one restart and the record names the newest; an update
during an in-flight restart schedules no second one; and the negative control — no check runs, and
the guard names the stuck landed-not-loaded module. Mutation check: forcing the old restart path makes
the live test fail (`Restarts` 1, expected 0).

### What is pending is read off the volume's LISTINGS, never its directory timestamps (MeshWeaver#6829)

Every decision above starts from `PendingModuleActivations`, and that reader memoises its walk of the
module volume (the activation sidecar, `activation.d/`, the set index, one existence probe per
landed DLL) so a probe does not pay it per call (#3664). The memo key used to be the last-write
times of `modules/`, `modules/activation.d/` and `modules/sets/`, on the premise that a rename into a
directory moves its timestamp everywhere. On the Azure Files (SMB) module share that premise does not
hold, and the snapshot froze: a fresh closed-type-set instance took its reading mid-landing (one
module landed, set 1 empty), the boot wave then landed about 33 modules and proposed set 2, and every
later reading in that process still said *nothing pending, 1 module in NO proposed module set*. The
self-updater answered `NothingToActivate` on its proposal check and its safety net alike, and the
restart that adopts set 2 never came.

The key is now what the three directories LIST: each file's name, length and own last-write time,
and each subdirectory's name — and, under `activation.d/`, each module's own record directory file
by file, because a record (a landing, an uninstall tombstone) lands by a rename into that directory
and leaves `activation.d/`'s own listing unchanged
(`ARecordOnlyChange_InsideAModulesRecordDirectory_IsSeen`). Module payload directories stay
name-only: nothing the reading derives lives in them. That is what the reading is derived from, and no filesystem leaves it
unchanged across a create, rename, replace or delete. A directory that cannot be listed yields no key
at all, so the reading is retaken and reports the fault instead of serving the old snapshot. The old
`DiskFingerprint` was kept briefly as an `[Obsolete]` type and has since been deleted; nothing outside
`PendingModuleActivations` ever bound it.
`PendingModuleActivationsCostTest.ALandingIsSeen_WhenTheVolumeDoesNotMoveDirectoryTimestamps`
reproduces the share by restoring the three directories' timestamps after a second wave: it reads 1
pending module where 3 are pending without the fix and 3 with it, while an unchanged volume is still
read once.

### A platform hand-over does not skip the live swap (MeshWeaver#6829)

The module half of a check follows the platform half, and `SelfUpdateVerdict.MayRestartAfter`
decides whether a restart may follow it. A release HANDED to the control lane is not followed by a
restart: the routed Roll it opens re-creates the pods, and a second request would only race it.
Until this change the same rule also skipped the LIVE swap, which is not a restart — it happens in
this process and races nothing. On an install that sees a newer platform build on nearly every check
(each `BuildCompletion` is one), that meant a proposed module set waited for the next roll even where
it could go live at once, and waited indefinitely behind a roll that waits for an approval.

Measured on memex.systemorph.com, 2026-10-16, with the listing fix above already running: three
replicas booted 08:19–08:26Z onto module set 1986; set 1987 was proposed at 08:38:57Z; every later
check on those replicas was a `BuildCompletion` answering *update available — handed to the control
lane*; `/health` read *20 module(s) are landed but not yet loaded* on all three until the handed-over
roll's first pod booted at 08:56Z — and that pod read no pending activation. The roll did activate the
set; nothing before it tried.

Now `SelfUpdateVerdict.MayActivateLiveAfter` admits a hand-over, and `ConsiderRestart` takes the live
half alone there (`LiveOnly`): what can go live goes live, what needs a restart is left to the roll,
and the verdict keeps its `HandedOver` outcome and says which. No restart is announced in either case.
`ModulesGoLiveWhileARollIsHandedOverTest` (`Memex.Portal.Shared.Test`, the real landing path and the
real decision, a control-lane install, a newer platform build in the registry): a live-updatable
module goes live and only the release event reaches the inbox; a restart-required module keeps N
serving, no restart event is announced, and the verdict names the roll; and the negative control —
nothing newer, nothing handed over — takes the module half as before. Mutation check: reverting
`ConsiderRestart` makes the first test fail with N still serving.

The activation marker outlives a successful live swap until the next boot, so every LATER
handed-over check still takes the live half and finds nothing pending in this process. That pass
says so — *nothing landed is pending in this process* — and keeps the `HandedOver` outcome; it never
reports an activation as still pending (`ALaterHandedOverCheck_AfterTheLiveSwap_SaysNothingIsPendingHere`).

**Not addressed here:** a module that cannot go live waits for the handed-over roll, and if that roll
waits for an approval, so does the module. Whether the control plane should then run the unattended
Restart in its place is the control plane's decision (it sees the Roll it opened), not this
instance's.

### One landing, one recycle wave (MeshWeaver#6729)

**A landing recycles each owner address ONCE.** `ModuleLiveActivation` hands every pending module of a
pass to `ModuleLiveUpdater.SwapAll` as ONE swap. Each module is still loaded, refused or committed on
its own, in order, with exactly the checks a single swap applies. What they share is the recycle: every
hub bound to any committed generation is torn down once, after the last commit, and every old
generation is retired after those hubs are dead.

**The wave does not cascade.** Each `DisposeRequest` of the wave carries `module:{name}[,{name}…]` in
`DisposeRequest.CascadedFrom`, and `MessageHub.HandleDispose` never runs a `RecycleCascade` for a
request that carries a value there. The wave already names every hub of its process that is bound to
the swapped generations, or every per-node hub when a generation reaches them all, so for a swap the
wave IS the dependency network. Hubs of other processes are not this wave's to recycle, because each
process swaps its own module contexts and recycles its own hubs.

**One module's fault never costs the others their wave.** Because the swap collects every module before
it recycles, the preparation of one module never faults the swap. A fault before that module commits is
its own `Failed` outcome, and its running generation keeps serving. A hand-over fault after it commits
keeps the module in the wave, because its new generation is current, and its outcome names the fault.
The wave also posts ONE `DisposeRequest` per address. Each committed module's hub set is a separate
snapshot of the live registry, so a hub that an unrelated recycle replaced between two snapshots shows
up as two activations of one path. Both are waited for, but a second request to that address would tear
down the successor.

**Why both rules exist.** It was measured on memex-cloud on 2026-10-15 at 00:49Z, on pods already
running the #6756 fix. One landing swapped `MeshWeaver.OgCard` and `MeshWeaver.Markdown.Export`, and
both pods' sync streams gave up on "4 DISTINCT owner activations" for unrelated owners
(`Hosting/PlatformBuilds`, `Hosting/TriageRule`, `Ops/Issue/*`, …), 120 such lines between 00:01Z and
06:07Z. The owner's refusals, read through a governed `Logs` action
(`Ops/Actions/iw-6729-memexcloud-preburst-0047-0049-20261015`), named a fresh activation each time:
`Hosting/PlatformBuilds` died as `#00AA2EE2` (*RecycleCascade from 'Hosting/InstanceAction': ModuleLiveUpdate:
MeshWeaver.OgCard …*), as `#025E3E68` (*… from 'Hosting/Build' …*), and as `#0045ECAE` (*… from
'Store/Publishing': ModuleLiveUpdate: MeshWeaver.Markdown.Export …*), each time still deferring its
first `SubscribeRequest` behind its initialisation gates. The cause was the wave itself. The swap
disposed every per-node hub, NodeType definition hubs included. Each definition then enumerated the
mesh-wide index and, seconds later, posted a second request to every instance of its network. Those
were addresses the wave had already torn down and their subscribers had already re-activated. A second
module's own wave did the same once more. The subscriber's give-up was therefore CORRECT: the address
really was recycling in a loop, and the fix belongs to the recycler, as the give-up's own text says.

**Tests.** `AModuleLandingRecyclesEachOwnerOnceTest` (`MeshWeaver.Compiler.Pipeline.Test`, a running
monolith mesh, the framework's own `RecycleCascade` seam as the instrument):

- A swap's wave does not run the cascade.
- A direct recycle of the same kind of hub does. This is the negative control that the instrument
  fires.
- Two modules landing together, with a live reader holding the probe open, tear the probe down exactly
  once. Every probe activation is recorded as the registry adds it (`HostedHubsCollection.HubAdded`),
  so a successor that is created and torn down quickly cannot be missed.
- A second module that faults while it is prepared leaves the first one `Live`, recycled onto its new
  generation, and is reported `Failed` itself. With the fault left to escape, both modules are reported
  `Failed` and the probe is never recycled.

### A hub with a step in flight is recycled when the step ends (MeshWeaver#7324)

**The wave never tears a hub down in the middle of a step it owns.** A step is any work a hub's
activation runs under `hub.BeginAsyncOperation()`, for example each step of a module reload request
(`ModuleReloadExecutor`) or an agent round. That call marks the hub's ONE in-flight marker,
`HubStepsInFlight`. Every hub carries one, created with the hub, on every host: in-process the mark
is the whole hold, and on Orleans the call also keeps the grain alive. The call marks the hub AND
every ancestor up its parent chain, so a step held on a hosted child (a thread's execution hub, a
layout area) defers its per-node owner, which is the hub the wave targets. The mark is released by the
scope's `Dispose`, which the step holds in an `Observable.Using` or a `using`, so a faulted step
releases it as well.

The wave reads the marker per address:

- **Idle:** the hub's `DisposeRequest` is posted at once, as before.
- **Step in flight:** the hub is DEFERRED. The wave subscribes to `HubStepsInFlight.Idle()` and posts
  the same request (same `Reason`, plus "deferred until its in-flight step ended", and the same
  `CascadedFrom`) when the step ends. There is no poll and no clock.

**The wave does not wait for a deferred hub, and does not retire under it.** It answers once the
OTHER hubs are dead. The outcome counts the deferred ones (`ModuleSwapOutcome.Deferred`) and names
them in its reason. The old generations are retired only after EVERY hub of the wave, deferred ones
included, has shut down, because a step's hold is not a module-context lease and the deferred hub
still runs the old generation's code and services. That retire belongs to the updater and has no
bound. A step that never ends keeps its hub, and the old generation, for exactly as long as it runs.

**One deferral per activation.** A second wave that meets an activation already deferred adds its
reason to that deferral, and the ONE request carries every reason. Two requests would be two
address-targeted disposals, and the second would reach the activation the first made room for.

**A hub that starts to shut down for another reason gets nothing.** The deferral ends at the first
instant of the shutdown (`IMessageHub.ShuttingDown`, which replays). That is before the teardown
disposes the step and fires the idle signal, so no request is posted. A request posted then would
reach the address's next activation, which already binds the current generation. Disposing the
updater cancels every pending deferral and every pending retire.

**The request names its activation.** The deferred `DisposeRequest` carries
`ForActivation` (the hub instance's activation token). If the activation is replaced after the post
and before delivery, the successor ignores the request instead of being torn down by it. The
issuing hub is resolved when the step ends, never captured when the swap began, because that
off-router hub can be recycled on its own while a step runs. Every captured activation of a deferred
address joins the retire, and a finished retire leaves the updater, so a process-lifetime updater
does not accumulate one entry per deferred swap.

**A step that starts just as the deferred recycle is posted** can still be torn down by it. That is the
ordinary case every step is built for: steps are idempotent and resume on the next activation. What
the deferral removes is the wave landing in the MIDDLE of a long step, every time.

**Why.** It was measured on memex on 2026-10-31. The all-modules reload
`Admin/_ModuleReload/auto-644261f5d8f67ec6` started Landing at 08:34:45Z and started it again at
08:36:41Z. The executor's teardown warning, read through a governed `Logs` action
(`Ops/Actions/c04-7324-memex-reload-1031`), named the restarter: *routed DisposeRequest; why:
ModuleLiveUpdate: … 19 modules swapped live … — self-update: a landed module generation is pending
activation*. The swap had recycled every per-node hub, because the swapped generations reached them
all, and that included the reload request's own hub. The hold on that step deferred only idle
retirement. The next activation re-ran the landing, about 12 minutes for every module. The swap
activated modules this reload's own landing had landed, so the reload's work set off the wave that
dropped it. That is the shape behind a request re-entering Landing over and over.

**Test.** `ASwapWaveDefersAHubWithAStepInFlightTest` (`MeshWeaver.Compiler.Pipeline.Test`, a running
monolith mesh, real Roslyn-emitted modules). A module whose next generation reaches every per-node
hub is swapped while one probe hub holds a step:

- The swap answers `Live` with the held hub still `Started` and not disposing, and with
  `Deferred == 1`.
- When the step ends, the held hub is recycled with the wave's own reason.
- The idle probe in the same wave is the negative control: it is recycled by the time the swap
  answers.
- The old generation is not retired while the held hub runs, and is retired once it is gone.
- Two swaps during one held step post ONE deferred recycle, carrying both waves' reasons.
- A held hub torn down by an unrelated recycle, whose teardown releases the step, gets no deferred
  recycle at all.
- A step held on a hosted child defers its per-node owner.
- A request for another activation is ignored on delivery; the one for this activation recycles it.
- An issuing hub recycled during the step does not strand the deferred recycle.

Red before the fix: the swap answered only after every hub of its wave was dead, the held one
included.

Against the pre-fix updater (one swap per module, no `CascadedFrom`), the first and third cases are red.

**Not established.** `ModuleReloadExecutor.SwapThenReport` still asks `IModuleLiveActivation.Swap` once
per module. No implementation of that seam is registered today, so it recycles nothing. An
implementation must swap a reload's modules as one wave, through `SwapAll`.

## What is shipped (slice 4 — root services, and only boot-time may restart)

**Root services are converted.** The most common blocker — a module registering ROOT services
(`WithGlobalServiceRegistry`; 21 of the 37 measured) — no longer forces a restart:

| Piece | What it does |
|---|---|
| `ModuleServices` (`MeshWeaver.Mesh.Contract`) | The module's registration delegates run against a COPY of the root collection as it stands when the module installs, so every `TryAdd` decision is the boot one; what they ADDED is the module's set, routed (`ModuleServiceRoute`, open vocabulary): a platform **interface** → `Proxy`, a platform **class** → `Current`, `IHostedService` → `Hosted`, a type the module declares itself → `ModuleOwned`, an open generic or infrastructure → `Private`. |
| Root forwarders (`ModuleServiceForwarding`) | The root gets exactly the platform-typed registrations it would have had — as forwarders that never name a module type. `Proxy` is ONE stable `DispatchProxy` per registration that forwards every call to the CURRENT generation, so a platform singleton that cached it follows a swap. `Hosted` is started at boot and, on a swap, the old generation's instance is STOPPED and the same registration STARTED from the new one. |
| `ModuleServiceProvider` (`MeshWeaver.ServiceProvider`) | The module's services live in a `MeshServiceProvider` container of their OWN ([Hub Service Layers](../HubServiceLayers)), which reaches the root only for types that do not name the module — through ONE stand-in **per root registration**, so `IEnumerable<T>` in the module is the root's whole list in the root's order and a single resolve still answers the root's LAST. Each stand-in resolves in a root CHILD scope owned by the asking module scope (`RootResolutionScope`), never in the root itself: a scoped root service built in the root is shared there for the life of the process — and, under the scope-wide lock of the container that preceded `MeshServiceProvider`, was built under the root's lock: the silo-wide wedge of [Reading a Silo Eviction](../ReadingASiloEviction) (MeshWeaver#6432); a closed generic over a module type (`IOptions<ItsOptions>`, `ILogger<ItsType>`) is closed in the module's container from the root's open-generic registration. |
| Module types, wherever asked | A type a module DECLARES is answered from the modules wherever it is asked — `ModuleServices.RegistrationsElsewhere`: the declaring module's registrations (`ModuleServiceProvider.ResolveAt`, one per registration) plus every **contribution** another module made of it (`ModuleServiceRoute.Contributed`). The ROOT serves it through `ModuleOwnedRootSource` (so the mesh hub and every per-node hub, which are scopes under the root, see it), and another module's container through `ModuleFallbackSource`. |
| Contributions | A registration of a type ANOTHER module declares (`ModuleServiceRoute.Contributed` — every AI provider registering MeshWeaver.AI's `LanguageModelCatalogSource` and `IChatClientFactory`) is never put in the root: a root forwarder would pin the declaring module, and a `DispatchProxy` cannot be built over a collectible interface at all ("A non-collectible assembly may not reference a collectible assembly"). It is served to the declaring module's container after that module's own registrations, and to the root's list. |
| The swap | `ModuleContexts.PrepareServices` re-runs N+1's delegates against the SAME boot prefix and requires the same routes and shape — the root's forwarders were laid out at boot; a changed shape is refused by name and N keeps serving. 🚨 The shape includes each forwarded registration's INDEX: every forwarder resolves the boot generation's index in the current generation, so an N+1 that inserts any registration (module-owned, private, hosted) ahead of a forwarded one is restart-required, never live (MeshWeaver#6847 — see below). |

🚨 **A "copy of the root collection" copies the DESCRIPTORS, so a registry that registration code writes
into must be copied too.** `AddHttpClient<T>()` records "client name → `T`" in the `HttpClientMappingRegistry`
INSTANCE the collection holds and refuses a second type under the same name — and the name is derived
from the type name alone. Shared by reference, generation N's `AddHttpClient<WebSearchPlugin>()` wrote into
the ROOT's one registry (pinning N's collectible context), and N+1's `WebSearchPlugin` — the same name, a
different `Type` from a new load context — was refused, failing the live swap of MeshWeaver.AI
(MeshWeaver.Plugins#3518). `ModuleServices.Probe` now hands each probe its own copy of that registry, seeded
with the root's entries, so a module colliding with a PLATFORM client is still refused exactly as at boot.
`ALiveSwapReRegistersATypedHttpClientTest` pins the N→N+1 probe and that root-collision control.

🚨 **A module's NAMED options reach the root only through a NAMED forwarder, and a swap evicts the root's
options cache.** `services.Configure<T>(name, …)`, `AddOptions<T>(name).Configure(…)` and every
`AddHttpClient(name, …)` / `AddHttpClient<TClient>(…)` register a `ConfigureNamedOptions<T>` as an
`IConfigureOptions<T>` — and the options factory applies a setup that is not ALSO an
`IConfigureNamedOptions<T>` to the default name only. Behind the root's `DispatchProxy` (which implements the one
interface it was created for) every named configuration a module made was therefore silently dropped: the
root `IHttpClientFactory` handed out a module's typed client with none of its headers, base address or timeout,
at boot, with nothing logged. The root now holds a `ModuleConfigureOptionsForwarder<T>` for every forwarded
`IConfigureOptions<T>`; it applies the CURRENT generation's setup exactly as `OptionsFactory<T>` would — by name
when it is named, to the default name otherwise. `HealthCheckServiceOptions` keeps its own forwarder
(`ModuleHealthCheckOptionsForwarder`, which also rebinds each added check to activate in the module's scope), and
that forwarder is named in the same way, so a module's `Configure<HealthCheckServiceOptions>("name", …)` reaches
`IOptionsMonitor.Get("name")` (MeshWeaver#6749). And because `IOptionsMonitor<T>` builds a named instance once
and caches it, `ModuleContexts.CommitAll` clears the root's `IOptionsMonitorCache<T>` for every options type the
swapped generations configure (configure, post-configure or validate), after they are current — otherwise N+1's
client kept N's configuration, and the cached delegates kept N's collectible context reachable. Not reached:
`IOptions<T>.Value`, which a root singleton caches for the life of the container, and a consumer that copied an
options instance into its own state. `ALiveSwapRefreshesATypedHttpClientsConfigurationTest` pins both halves,
each with its own negative control. A forwarder's factory lambda captures values (the lifetime, the index),
never the loop's `registration`: the first draft did, which hoisted it into the closure every forwarder of that
iteration shares, and the descriptor's implementation type pinned generation N in the root —
`ModuleRootServicesSwapTest`'s retention check caught it.

🚨 **A list is served as a list.** The first images of this slice collapsed every multi-registration
service to its LAST element in two places — the module container's fallback answered a platform type with
one `root.GetRequiredService(type)` stand-in, and the per-node forwarders were one per TYPE. Measured on
the three portals running core `7585f2a6`: MCP listed ONE tool of 37 (`restore_from_point_in_time`, the
last declared — the MCP server takes `IEnumerable<McpServerTool>` inside the AI module) while a portal on an
older image listed all 37; by the same mechanism every per-node hub resolves only the LAST `IHarness` the
AI module registers, which is how the home-page chat lost the harness it was bound to.
`ModuleServicesKeepEveryRegistrationTest` pins both, with the collapsed answers as its negative control.

🚨 **A module's lists include what its DEPENDENTS contribute, and a dependent resolves its dependency's
services.** The first images of this slice served a module type only inside its own container: on the
control instance every AI provider's catalog sources and chat-client factories stayed in the provider's
container (the Provider import listed only the platform nodes, `Provider/OpenRouterEU` had 0 models,
"No IChatClientFactory is registered", every pull-request review stopped), and the provider factories —
which read MeshWeaver.AI's `ChatClientCredentialResolver` off the mesh hub — found nothing.
`ModuleContributionsCrossContainersTest` pins all four directions (the declaring module's list, the mesh
hub, a per-node hub, a dependent resolving its dependency), every one red on the code before it. A
"find the shared singleton in the collection and mutate it" registration cannot cross containers by
construction — contribute a list element instead (MeshWeaver.AI's catalog is built from every
`LanguageModelCatalogSource` registration). A dependent module's service that cached a contribution keeps
that generation's instance until the hubs holding it are recycled — the same rule as any swap.

What still blocks root services (measured, named): a delegate that removes or replaces a registration it
did not add, a keyed registration, a class-typed platform service the module implements, an interface
implementation that also implements another platform interface (a proxy would hide it), an open generic
the module implements.

### A module's health check follows the swap (MeshWeaver#6766)

A module's `AddHealthChecks().AddCheck<TCheck>()` reaches the root as `ModuleHealthCheckOptionsForwarder`,
and the root's `DefaultHealthCheckService` activates the check. The root reads
`IOptions<HealthCheckServiceOptions>` ONCE per process. The forwarder used to copy the registration that
the boot generation's configuration added, and to rebind only its PROVIDER to the module's current
scope. The factory it kept was therefore generation N's `GetServiceOrCreateInstance<TCheck>`: `TCheck`,
and every type its constructor takes, were N's types. After a live swap, N+1's container was asked for N's
dependency type, which it does not hold. Measured on a portal: every `/health` threw `Unable to resolve
service for type 'MeshWeaver.AI.ProviderCatalogConvergence' while attempting to activate
'MeshWeaver.AI.ProviderCatalogHealthCheck'` out of the health middleware. The kept factory also pinned
generation N's collectible context.

The root's registration now names no module type. It keeps the check's name, tags, failure status,
timeout, delay and period. At every activation its factory takes ONE snapshot of the module's current
generation, runs that generation's own factory for the check of that name, and activates it in that
generation's container. Each generation's checks are read once, from its own configuration run against an
empty options instance, and are held in a table keyed weakly by the generation, so a retired generation is
never pinned. A module that is no longer held (retired by an uninstall, or withdrawn at boot), or a current
generation that no longer declares the check, gives a NAMED `Degraded` result for that check, never an
exception: the feature is absent, and the probe says which feature and why.
`ModuleHealthCheckActivatesInModuleScopeTest` swaps a module live and retires it, running the check
through the root's `HealthCheckService` each time. On the base, the swap case throws the measured shape
(`Unable to resolve service for type '…Beat' while attempting to activate '…ProbeCheck'`) and the
retire case throws `Module … holds no service scope`.

### Built before published (MeshWeaver#6542)

🚨 **The root only ever READS a module's instances.** Every PROCESS-LIFETIME instance the root can
reach in a module's container — each SINGLETON behind one of the four paths below (a `Proxy` or
`Current` forwarder, a `ModuleOwned` or `Contributed` stand-in), and what its constructor takes — is
built before the container can be reached from the root: **at boot**, once the root container is
built and before the root provider is handed to anyone (`IRootContainerBuilt`,
`ModuleContexts.BuildAllBeforePublish`, a module after the modules it depends on); **on a live swap**,
after every staged generation has loaded, materialised and prepared its services and before
`CommitAll` (`ModuleServices.BuildBeforePublish`, `ModuleServiceProvider.BuildRootReachableSingletons`).

**What belongs to a circuit is never built here (MeshWeaver#6556).** A SCOPED registration is one
instance per scope — a Blazor circuit, a request — so the one a boot would build is an instance no
scope asked for, made against scoped state no scope initialised. The first version of this section
built scoped registrations too, and on image `3.1.10611` that withdrew the Radzen view pack:
Radzen's `DialogService` (scoped) subscribes to a `NavigationManager` in its constructor, and a
boot-built one threw `'RemoteNavigationManager' has not been initialized`. So a scoped registration is
left to its first resolve, and so is a singleton whose constructor graph reaches a scoped registration
— read STRUCTURALLY off the registrations (`ScopedDependencyWalk`: every public constructor's
parameters of a reflection-activated registration, through transients and the module's own
singletons, into the root's registrations for every type that names no collectible assembly, and —
for a type a module DECLARES — into the other module containers that register it, the same
registrations `ModuleFallbackSource` resolves it from; a root singleton ends the walk). Without that
last leg a singleton taking another module's scoped type looked like it took nothing, was built at boot
with no caller, and withdrew its module (`ASingletonOverAnotherModulesScopedTypeIsNotBuiltAtBootTest`). Neither is a path the lock order needs: a root singleton that takes a scoped
service is a captive dependency, not a way the root reaches a module. A module's PRIVATE registration
is built only when a built one takes it. A delegate-activated singleton names no dependencies and is
built. `AModuleWithCircuitBoundServicesLoadsAtBootTest` and
`ModuleRootServicesSwapTest.AnUpdateWithCircuitBoundServices_SwapsLive` carry the Radzen shape, against
a root-scoped stand-in that throws exactly as `RemoteNavigationManager` does.

### A module's scoped service is the caller's (MeshWeaver#6584)

Not building a scoped registration at boot only moved its construction to its first resolve — and
every resolve of a module service used to land in the module container's OWN root: one instance per
generation, shared by every circuit, built against a root child scope no circuit initialised. Radzen's
`ThemeService` was one instance for every user (one user's theme was the next user's), and
`DialogService` threw `'RemoteNavigationManager' has not been initialized` on its first real resolve.

So a resolve now carries its **caller** — the root-side scope it is made in (a Blazor circuit's, a
request's, a hub's; null for the root container itself, i.e. a singleton or a resolve off the root
provider) — down every road the root reaches a module by: `ModuleOwnedRootSource`'s stand-ins, the
`Current` forwarder, a `Proxy` over a scoped or transient registration (one proxy per caller scope,
forwarding for it; a singleton keeps its one stable proxy), and a module type asked from ANOTHER
module's container (`ModuleFallbackSource`, which passes on the caller its own scope serves). A
registration that is not a singleton is then resolved in a module scope **owned by that caller**
(`ModuleServiceProvider.InCallerScope`, `ModuleCallerScopes`): begun on first use, tagged with the
caller, and handed to the caller's own disposer, so it ends when the caller ends — asynchronously, as a
circuit's scope is disposed. Its `RootResolutionScope` is the caller's own scope, so every root
registration it takes (`NavigationManager`, `IJSRuntime`) is that caller's instance. A singleton is
still the generation's one instance, built before published as above.

**Which ends first.** A caller can outlive a generation (a hub scope across a live swap): the
generation's container, as it ends, ends every caller's module scope FIRST — before its own singletons
— and lets go of it, so the caller's disposer holds nothing of the retired generation; the caller's own
end afterwards finds nothing to do (`ACallersModuleScopeEndsWithTheGenerationTest`).
`AModulesScopedServicesAreTheCallersTest` measures two circuits through both the module-declared and
the platform-class road; its negative control resolves the same registration with no caller and gets
the uninitialised-state throw.

**A forwarder owns nothing it returns (MeshWeaver#6588).** Every root forwarder of a module's
platform-typed service — `Current`, and `Proxy` at every lifetime — is a `NotOwnedServiceDescriptor`,
which every container MeshWeaver builds treats as **externally owned** (`MeshServiceProvider`). An
ordinary factory registration is owned by the scope that resolves it, so the forwarder used to hand
ownership of a module's instance to whoever asked: the first circuit to end disposed a module
SINGLETON under every other user, and disposed a module SCOPED instance a second time after the module
scope that circuit owned had already disposed it; a `Proxy` over an `IDisposable` interface forwarded
the same disposal. Ownership stays where the instance was built — the generation's container for a
singleton, the caller-owned module scope for a scoped one. The forwarder keeps its own position in the
collection, so the default registration of a service and the order of an `IEnumerable` of it are
unchanged (`ANotOwnedForwarderKeepsItsPositionTest`).
`AModuleForwarderOwnsNothingItReturnsTest` measures one circuit ending on the test mesh, on an
`IHostApplicationBuilder` host and on a legacy `IHostBuilder` host: the singleton serves the other
circuit undisposed, each scoped instance (class and proxy route) is disposed exactly once.

**No lock spans a caller and its module scope.** The container that preceded `MeshServiceProvider`
(Autofac, removed in MeshWeaver#7145) built a scope's shared instances under that scope's ONE lock. A
caller-owned module scope is a scope of the MODULE container, and its root resolutions are made in the
caller's own scope, so the two locks were taken in both orders: a caller-scoped ROOT service whose
construction resolves a module's scoped service (caller, then module scope), and that module service's
construction taking a caller-scoped root service not built yet (module scope, then caller) — two first
builds overlapping in those orders deadlocked, until the module scope borrowed its caller's lock through
a private field. `MeshServiceProvider` builds each shared instance under that instance's own lock, so
the two builds wait only for the one instance each really needs, and nothing has to be borrowed
([Hub Service Layers](../HubServiceLayers) → *Threads, cycles and faults*).
`CallerAndModuleScopedServicesBuildInOneLockTest` forces the overlap and asserts both constructions
were inside their builds at once — the interleaving that deadlocked.

**Ownership diagnostics: the line each defect would write (MeshWeaver#6584, MeshWeaver#6588).** Neither
defect wrote a log line while it was live. A shared `ThemeService` throws nothing, and a disposed
singleton fails only in whichever circuit next touches it. So a production log that is quiet after the
fixes cannot tell "fixed" from "never logs". The production log readings on both issues found exactly
that. The diagnostics below make each defect visible. Every line is a **Warning** under the category
`MeshWeaver.ServiceProvider.ModuleScopeOwnership`, starts with the marker `[ModuleScopeOwnership]`,
names its issue, and is written **once per registration**, so a defect that repeats on every circuit
cannot flood the log. The correct route writes nothing, so a line in production is a defect and never
noise. No existing log level changed.

| Line | Written when | Where |
|---|---|---|
| `… is being built in the module container's root scope, which no caller owns … (MeshWeaver#6584)` | a module's SCOPED registration is activated in the module container's own root. That is the defect's shape: one instance per generation, shared by every caller and built against none. The line is written as the activation is ATTEMPTED, so the throwing shape (`DialogService` over an uninitialised `NavigationManager`) writes it too. | `ModuleScopeDiagnostics`, an activation observer on every shared registration of a module container (`ModuleServiceProvider.CreateForCallers`) |
| `… built in the module scope owned by A was served to B … (MeshWeaver#6584)` | a serving point hands a module's scoped instance to a caller that does not own the module scope it was built in | `ModuleServiceProvider.ReportIfServedAcrossCallers`, called by `ResolveAt(…, caller)` and `ModuleServices.Resolve(…, caller)` |
| `Scope S is ending and will dispose T, which X owns … (MeshWeaver#6588)` | a registration owned by the resolving scope, activated by a factory, returned an instance a module owns. The line is written as that scope begins to end, just before its disposer runs. | `OwnershipTransferObserver`, attached to every MeshWeaver container and hub level (`ServiceProviderExtensions.SetupModules`) |

Who owns what is recorded in ONE registry per mesh, `ModuleInstanceOwners`. `MeshBuilder` registers it
in the root as an instance. Its entries are weak (a `ConditionalWeakTable`), so it never pins a retired
generation. A module container records every disposable shared instance it builds, together with the
scope that owns it. A `ModuleServiceProxy` over a disposable interface is recorded as the module's, so a
scope disposing it is reported too. The diagnostics never RESOLVE anything while a resolve is running.
The registry is captured when a container is built, and its logger is bound once the root exists
(`SetupModules`). A first draft resolved the registry through the resolve context. Inside a module
container that resolve went through the module's fallback into `RootResolutionScope`, the registration
being checked, and the container reported a circular dependency on the first module service resolved. Only
factory-activated registrations owned by the resolving scope are watched, because only a factory can
return an instance something else built. Reflection-activated and provided-instance registrations
cannot transfer ownership.

Two rules keep the readings honest:
- **The first record wins.** The container that built an instance records it before any factory that
  later returns the same object can, because the build completes inside that factory's resolve. A
  factory that re-recorded it would take over the owner and hide the disposal. A scoped factory that
  answers one shared object to callers A and then B keeps A as the owner, so B is reported.
- **The #6588 line is claimed when a scope ends**, keyed by the registration alone. Claimed at resolve,
  the first scope to resolve took the report, and a scope that ended before it disposed silently. Keyed
  by owner, every caller tag was a new key. Once a registration is reported, later activations subscribe
  nothing, so a live defect costs no handler per circuit.

**Cost and retention.** Nothing in the diagnostics is static. The registry and each module container's
diagnostics are instances. Both maps are `ConditionalWeakTable`s, and owners are held through
`WeakReference`s, so no entry outlives its instance or keeps a scope alive. The set of reported keys
grows only when a defect fires, bounded by the registrations. On the happy path the work per NEW
activation of a watched registration is an `IDisposable` type check plus a lock-free table read. The
work per new shared module instance is one table insert. The work per scoped module service served
through a forwarder is one lookup.

`ModuleScopeOwnershipDiagnosticsTest` re-introduces each defect deliberately and shows that its line
fires (the negative control):
- for #6584, the scoped registration is resolved off the module container with no caller, the route
  before #6586;
- for #6584, circuit A's instance is handed to circuit B;
- for #6588, the root forwarders are ordinary owned factories, the route before #6591. The test asserts
  that the singleton really was disposed and the scoped instance disposed twice, so the control is live.

The same test shows that the correct route writes nothing, both on real MeshWeaver containers and through
the full wiring of a generic host with a module installed in its own load context.

**Reading it in production.** Use governed `Logs` InstanceActions on the control instance, over each
portal that runs the image, with the query `|= "[ModuleScopeOwnership]"`. A zero is evidence only when
two things hold in the same window and with the same selector:
1. the running image is shown to carry this change (`Sample`, or `/api/version`);
2. a **positive control** returns lines, for example the `[ModuleLoad]` lines of the same pods. That
   shows the query reads that portal's logs and was not cut.

A zero over a window in which a chart or pivot page was rendered by two sessions is the closing reading
for #6584. For #6588 the window must contain one session ending while another works on a module
service.

**The first reading named a ROOT singleton holding scoped validators (MeshWeaver#6584).** The image that
carried the diagnostics wrote the first line twice per pod on both public portals, for two module
`INodeValidator` write guards — the Hosting module's `InstanceActionRequestWriteGuard` and the AI
module's `ModelCeilingWriteGuard` — and the second line never. The route: the mesh's query providers
(`StorageAdapterMeshQueryProvider`, `StaticNodeQueryProvider`) are ROOT singletons, and their Read filter
took the validator set from the root container (`IEnumerable<Lazy<INodeValidator>>`). Every
`INodeValidator` is registered SCOPED — it runs on the hub that validates the write — so that was a
captive dependency: a module's scoped validator, forwarded for no caller, was built in the module
container's own root scope, once per generation, only for the filter to read its `SupportedOperations`
and discard it as a write-only validator. Registering the guards as singletons would have hidden the
line and changed what they validate against (each reads the VALIDATING hub's configuration); the
consumer was the defect. Now a singleton consumer takes the set from `SingletonNodeValidators`, a root
singleton that begins a scope of its own and resolves the set there: that scope is the caller, a module's
scoped validator is built in the module scope it owns, and everything the scope built is disposed with
the root. The set it hands out is read afresh on every enumeration and is empty once the holder is
disposed: a consumer can outlive the root (the static-node provider stays subscribed to the module
registry), and holding the validator array itself kept the disposed mesh hub alive through
`StaticNodeQueryProvider → Lazy<INodeValidator>[] → ServicePrincipalAdminGuard → MessageHub`
(`CopyMoveDeadlineReleasesHubTest`). The handed-out set holds the array alone — never the holder,
whose disposed scope still references every service it resolved — and is never a C# iterator method: an
iterator enumerated on the thread that created it is its own enumerator, keeps its last `Current` after
the walk, and so pinned the hub only when the first enumeration happened to run on the constructing
thread (red in CI, green locally; `SingletonNodeValidatorsReleaseOnDisposeTest` makes that thread shape
deterministic). A native backend whose query provider filters reads the same way must take its validators from
the same holder; the Sqlite and Cosmos providers in MeshWeaver.Plugins still take them from the root and
move once this holder is on their platform pin (the public portals run Postgres, whose provider
consults no validator). `AModuleNodeValidatorIsNeverBuiltForNoCallerTest` installs a module with a write-only scoped
validator, reads a stored node as a user through the query providers, and asserts that no build of the
validator had no caller; its control asserts that the query really materialised every validator of the
set the providers hold. Against the old registration it fails with one build in a hub's scope (the
write) and one with no caller (the query).

**A withdrawn module is absent from the installed-module set (MeshWeaver#6557).** A container's
registrations are fixed once it is built; the module set is not. Each held module used to get its own
transient `InstalledModuleAssembly` registration, and a module withdrawn after the container was built
kept it: once its boot assembly was collected, that registration threw "no longer held by this mesh's
module registry" from inside every `GetServices<InstalledModuleAssembly>()`. That is how the Radzen
withdrawal above refused the pod's readiness: the pre-warm sweep enumerates installed modules, so one
broken module refused readiness for the whole pod. Now the set is ONE registry the mesh owns,
`InstalledModules`: ONE ordered sequence of slots, in the order they were declared in the service
collection — order is load-bearing (first-mount-wins static assets, the list-ordered compile
reference surface), so a held module keeps the position it was installed at. A slot is a fixed
`InstalledModuleAssembly` (an image-bound module, anything else that registers one) or a member that
can GO — a held module by NAME, answered from `ModuleContexts` at each read, or an
`InstalledModuleAssembly` a held module registers through its OWN services — and a member whose
module the registry no longer holds is simply absent. The
`IEnumerable<InstalledModuleAssembly>` every consumer resolves is an explicit factory registration over
that registry, which MS DI and `MeshServiceProvider` both answer ahead of the per-descriptor collection,
so a host built with plain `BuildServiceProvider()` reads the same set as a MeshWeaver host. (A
registration source was tried first and rejected for that reason: MS DI ignores sources, and
`ConfiguredModuleActivationTest` lost every held module.) The withdrawal itself is still reported as an
`IncompatibleModule` (stderr, `/health`, the module-status surfaces).
`AWithdrawnModuleIsAbsentFromTheSweepTest` covers both hosts and runs the probe and the warm-up sweep
over a withdrawn module.

The same registry closes the two neighbouring gaps of that class, again on both host kinds:

- **The withdrawal is reported, and happens, on every host.** The `IncompatibleModule` records — the
  declared ones plus every module withdrawn at boot — are served by an explicit
  `IEnumerable<IncompatibleModule>` factory over `InstalledModules`, not by a registration source a
  plain MS DI host ignores. And the boot publication that withdraws a failing module runs from
  the module registry's own singleton registration on its first resolve (`ModuleContexts.PublishOnce`),
  which every host kind calls; every read of the installed or incompatible set resolves the registry
  first. A MeshWeaver root still claims the publication earlier, from `IRootContainerBuilt`, so there it
  runs before the provider is handed to anyone.
  **Residual, plain MS DI only:** MS DI offers no post-build hook, so there the publication runs inside
  whichever resolve first reaches the registry. If that resolve is the constructor of a root singleton
  `A`, and a held module's singleton needs `A`, then MS DI builds `A` a second time on the same thread:
  its singleton lock is re-entrant and its value is not set yet. The module's singleton then holds the
  copy rather than the root's `A`. Nothing deadlocks: MS DI has no container-wide lock, so this is not
  the #6542 inversion. Every MeshWeaver host builds its root with `MessageHubServiceProviderFactory`
  (`MeshServiceProvider`) and claims the publication from `IRootContainerBuilt`, so this shape does not
  arise there.
- **No forwarder of a withdrawn module is enumerated.** A held module that registers further
  `InstalledModuleAssembly` instances itself (MeshWeaver.Testing registers its xunit closure that way)
  used to get a root `Current` forwarder per instance, and a forwarder can only throw once its module is
  withdrawn. Those registrations get no root forwarder: they are recorded in `InstalledModules` by
  (module, registration) when the module installs and answered from its current generation while the
  registry holds it, so they follow a live swap and leave with a withdrawal.

`AWithdrawnModuleLeavesNoForwarderBehindTest` (a MeshWeaver host) and `AWithdrawnModuleOnAPlainMsDiHostTest`
(plain MS DI, no hook called by the test) pin both, with published twins as negative controls.

**A withdrawn module's security rule leaves the rule set.** A module that registers an
`INodeTypeAccessRule` (a platform interface) is forwarded to the root as a `ModuleServiceProxy`, and that
registration stays in the root's `IEnumerable<INodeTypeAccessRule>` after the module is withdrawn.
`NodeTypeAccessRuleSet` read each rule's `NodeType` while it built the security rules, and the dead proxy
threw `Module … holds no service scope in this mesh`. Every write's `RlsNodeValidator` takes that set, so
one withdrawn module made every node write fail, and with it every package install. The set now holds
only members for which `ModuleServiceProxy.Serves` is true: an ordinary instance, or a proxy whose module
the registry still holds. A lookup also skips a rule whose module is retired later. A type whose rule left
falls back to the caller's standard permission check, which is closed by default. Any other consumer that
builds an index over a platform interface a module can register should apply the same predicate.
`AWithdrawnModulesAccessRuleLeavesTheSecurityRulesTest` pins this: the rule set builds, and a node write
succeeds. On the base, both failed with the exception above. Its published twin is the negative control:
there the rule governs its type.

**A withdrawn module's query provider leaves the query fan-out (MeshWeaver#6793).** The same defect, one
interface over, stopped a host from starting at all. A module that registers an `IMeshQueryProvider` is
forwarded to the root the same way, and `MeshQuery` — built by `IMeshService`, which a hosted service takes
at startup — read every provider's `Name` to de-duplicate them. Once a module was withdrawn (on the
instance, a mail sender whose constructor rejected the tenant id the configuration carried), that read
threw `Module … holds no service scope in this mesh`, `Host.StartAsync` failed, and the pod crash-looped
on every roll while the module was reported only as contributing nothing. `MeshQuery` now binds a module's provider under
ONE lease (`ModuleServiceProxy.TryLease`): the name is read from the generation's own instance, a proxy
that cannot be leased is left out, and the fan-out skips a provider whose module is retired later.
`AWithdrawnModulesQueryProviderLeavesTheQueryFanOutTest` pins it: `IMeshService` builds and a query
answers. On the base, the test mesh did not start. Its published twin is the negative control: there the
module's provider answers in the fan-out.

🚨 **Building the fan-out constructs no module provider (MeshWeaver#7218).** `MeshQuery` first read each
module provider's name under a lease taken while the fan-out was BUILT, and every `IMeshService` builds a
fan-out. A module provider whose own construction builds an `IMeshService` was therefore leased again by
that service, on the thread already building it. On the portal the mail module's Teams chats provider does
exactly that: its chat rooms take the member directory, which takes the user directory
(`UserIdentityCache`), a root singleton over `IMeshService`. The container refused the re-entry as
*"A circular dependency was detected for the service of type 'IMeshQueryProvider': building it asked for
itself again"*, and every pod logged registration #36 as not constructible. The fan-out now holds a module's
provider under its forwarder's label and reads the provider's real name from the instance each
subscription leases (`QueryMembers`), which also de-duplicates by that name. A duplicate registration is
leased once, by the first subscription, to learn its name; later subscriptions leave it out without
constructing it. A learned name is trusted only while the generation that answered it is current
(`ModuleServiceProxy.CurrentGenerationId`): a same-shape swap may reorder a module's providers, and a
position must never be skipped under the name its neighbour now answers by. The cache holds the
generation's identity (its load context's name), never the generation: a `ModuleGeneration` holds its
collectible context, and the fan-out lives as long as the mesh, so holding one kept every retired
generation loaded. `AModuleProviderReorderedByASwapTest` also proves the retired generation is collected
while the fan-out that learned from it stays alive, with no query after the swap.
`AModuleProviderReorderedByASwapTest` swaps `[A, B]` for `[B, A]` and both still answer; with the name
trusted across generations, A is absent after the swap.
`AModuleProviderNeedingTheMeshServiceTest` pins it with a module provider built over a root singleton that
takes `IMeshService`. Once the provider is built, no fault is reported against its registration, and the
`IMeshService` built during its construction serves it. On the base, the circular dependency was reported.

🚨 **The lease spans the SUBSCRIPTION, not the call.** A provider returns a COLD observable, and the fan-out
subscribes to it later, through its query pool. A lease taken around the call and released when the call
returned left the query running with no lease. A retirement landing in between then disposed the
generation's services under it, and the whole query faulted. So `Matches` (synchronous) is leased for the
call, and `Query`, `Autocomplete` and `Select` go through `ModuleServiceProxy.Leased`, which takes the lease
on subscribe and releases it when the subscription terminates or is disposed. A retirement that starts
mid-query waits for that release. `ModuleQueryProviderLeasedAcrossRetirementTest` retires the generation at
the moment the module's query is subscribed. The query answers from live services and the unload follows.
With the lease taken around the call only, the query errors with the fence's `ObjectDisposedException`.

**A module service is LEASED, never checked and then called (MeshWeaver#6715).** "Does its module still
serve?" followed by a call through the proxy can be split by a retirement: `ModuleContexts.Retire` removes
the generation and disposes its services between the two, and the call throws `Module … holds no service
scope`. Consumers that must not fault use one atomic operation instead.
`ModuleServiceProxy.TryLease(service)` reads the module's current generation ONCE. It leases that
generation's load context (`AlcLeaseRegistry`, as `ResolveDependency` does) and its services, re-checks
that the generation is still current, and hands back the generation's own instance. It returns null when
the module is gone or already retiring. `ModuleServices` counts its leases in one word changed only by
compare-and-swap. A `Dispose` that arrives while a lease is held only marks the services retiring: no new
lease is granted, and the LAST release disposes the scope. Nothing waits and nothing blocks, and the
retirement's unload already waits for the context lease. `ModuleServiceProxy.Leased(service, use, what)`
holds one lease across the subscription of an observable call, and fails with a named
`ModuleRetiredException` when the module is already gone.

`NodeTypeAccessRuleSet` reads every rule's metadata under a lease: `NodeType` when it builds, and the
supported operations or permissions in `Find` and `FindPermissionRule`. A rule whose module cannot be
leased is skipped for the registration before it. For a module's rule, the set hands out a rule whose
`HasAccess`/`HasPermission` run under a lease for their whole evaluation, so an uninstall that lands
mid-decision is deferred to the end of the decision. The two steps are still two leases. A retirement
that falls BETWEEN the selection and the evaluation gives a named `ModuleRetiredException`, which
`NodeTypeAccessRuleGate.Evaluate` reports as `Undetermined` (fail closed, with the type named). Asking
again decides without the retired rule. `ModuleRuleLeasedAcrossRetirementTest` retires the generation
from inside the rule, through a controlled switch, at each of the three points: under a lease, inside
the evaluation, and inside `Find`'s metadata read. On the base, the first two fault on the disposed
generation and the third throws out of `Find`.

**Who holds a lease, and what a lease left at teardown means.** Every lease on a module context is
recorded under the KIND of holder that took it (`AlcLeaseRegistry.Enter(context, holder)`,
`AlcLeaseRegistry.Holders`): a service lease under the platform interface the consumer leased and its
registration (`IMeshQueryProvider (registration 0)` is the query fan-out, `INodeTypeAccessRule` the
access-rule set, `IConfigureOptions<T>` the options pipeline, `the forwarder health scan` the readiness
probe), and a script session's binding under the assembly it bound. A generation that `Retire` keeps
loaded, and one `ModuleContexts.Dispose` keeps loaded at mesh teardown, are reported with those holders
(`NOT unloading module generation … still held, by IMeshQueryProvider (registration 0) ×1`), so the line
names whose lease it was rather than only how many. At teardown, the line follows a lease whose holder
was still alive when the container was disposed. The mesh's drain releases every POOLED query
subscription (the Query pool's drain disposes it), so on a clean teardown the holders left are those that
end only with their own subscriber: a live `Autocomplete` or `Select`, a kernel session whose hub never
disposed. When the drain itself did not finish (`MeshTeardownHostedService: … did not finish draining`),
the container is disposed over hubs and pooled subscriptions that were never torn down, and their leases
are reported here too. Read the drain verdict from the same instant before reading this line as a leak.
`ModuleLeasesAtMeshTeardownTest` pins both: a live autocomplete held across a real teardown is reported
with its holder, and a live pooled query held the same way is released by the drain and reports nothing.

**Why.** The container that preceded `MeshServiceProvider` (Autofac, removed in MeshWeaver#7145) held a
scope's shared-instance lock for the whole build of a shared instance, and that one lock covered every
shared instance of the scope; an instance that existed was answered without the lock. The root reaches a module's services from inside its own builds — `ModuleOwnedRootSource`'s
stand-ins, the `Current` route's transient forwarder, `ModuleServiceProxy.Invoke` (a module's
`IConfigureOptions<T>` run when a root singleton reads `IOptions<T>.Value`), and the
`ModuleHealthCheckOptionsForwarder` run from `DefaultHealthCheckService`'s constructor — while a module
singleton's constructor takes root singletons. Two FIRST builds in those two orders deadlocked (root
lock then module lock, against module lock then root lock). With every module instance built before
publication, the first order never waited, so there was no cycle. `MeshServiceProvider` removed the
scope-wide lock itself — each shared instance has its own — so the inversion no longer exists even for
a container published unbuilt; built-before-published stays for what it also gives: a module
constructor that throws fails the publication, never a first caller (below).
`RootAndModuleSingletonsBuildInOneLockOrderTest` forces the overlap both ways; the unbuilt shape that
used to be its deadlocking negative control now completes too.

**Why not one lock order.** Making every module build enter the ROOT's lock first cured the repro and
was rejected: a module constructor then ran under the root's lock, so one slow module constructor
stalled every first-time root resolve in the process — the silo-wide wedge of
[Reading a Silo Eviction](../ReadingASiloEviction) (#6432). Built before published runs module
constructors under no lock but their own container's: a slow one delays its own boot step or swap, and
nothing else. `ACreateNeverWaitsOnAModuleContainerUnderConstructionTest` parks a singleton of the
NEXT generation inside its constructor and has a create answered `Ok` meanwhile, from the generation
still serving.

**A constructor that throws fails the publication, never half of it.** On a swap the swap is refused
as `Failed`, naming the constructor's own message; nothing of N+1 was ever reachable, N keeps serving
and N's hosted services are never stopped (`ModuleRootServicesSwapTest`). At boot the module is
WITHDRAWN — no longer current, its container disposed — together with every module that depends on
it, and each is reported as an `IncompatibleModule` (stderr, the log, and the records `/health` and the
module-status surfaces read); the mesh boots without it
(`AModuleWhoseSingletonCannotBeBuiltAtBootTest`, with its negative control). Its load context stays
loaded until the restart: a dependent may already hold its types.

**The cost:** root-reachable module singletons are constructed at boot or at the swap instead of on
first use, so their constructors' side effects — and their failures — move there.

**What it does NOT cover.** A module's HOSTED services are left to their own host: the root never
reaches them, and a hosted service that cannot be built costs its feature, never the module (#2449).
A shared instance that only a registration SOURCE makes on demand and that no constructor takes — a
closed generic over a module type (`ILogger<ItsType>`, `IOptions<ItsOptions>`) resolved later from
inside a factory or a method — is built on its first use; with no scope-wide lock that is no longer a
lock-order residual, only a construction that happens late.

**A staged generation binds its swap.** Its container is built before the commit, so it sees the
other generations of the SAME swap in place of the ones they replace (`ModuleServices.Stage`, the
stage-aware lookups in `ModuleContexts`) — the AI module's catalog is built with its re-loaded
providers' contributions, not the retiring ones'. A sealed stage answers nothing and forgets its
list, so from the commit on every lookup is the current generations, and no stage pins a generation
a later swap retires.

**Three pins found and fixed on the way — each caught by the collection assertion failing, then read off
a heap dump (`dotnet-dump` `gcroot`, ClrMD for the referrers):**

1. A CHILD scope of the root under the container then in use (Autofac, since removed — MeshWeaver#7145)
   — isolated and load-context scopes included — cached every service it asked its parent about in the
   PARENT's registered-services tracker, by type, forever (`TypedService(IOptions<GreeterOptions>)` in
   the root registry). Hence the module's own container, which `MeshServiceProvider` keeps.
2. That container's process-static reflection cache, and a keyed-services cache of its DI adapter that
   the shared set could lose, rooted a swapped-out generation until a hand-maintained eviction cleared
   them on `Unloading`. `MeshServiceProvider` keeps no process-static cache, so the eviction is gone
   with it (`ACollectibleTypeIsNeverHeldByTheRootTest`).
3. The static-node query catalog snapshotted the boot generation's nodes and was re-taken only on the
   next query, so a swapped-out generation stayed referenced until something asked. It is now dropped on
   `ModuleContexts.VersionChanged`, and a module's nodes enter the boot node list only as SLOTS (content,
   hub configuration and service delegates stripped) — the provider serves the current generation's nodes
   in their place. The provider OWNS that subscription and ends it in `Dispose`, which the mesh's
   container calls when the mesh ends; a discarded subscription let the registry's subject keep the
   provider — and through its validators a hub — reachable after its mesh was gone (MeshWeaver#7373,
   `StaticNodeQueryProviderEndsWithItsContainerTest`). The registry also completes `VersionChanged` when it
   is disposed, releasing any subscriber that did not end its own subscription. The other root subscriber,
   `ModuleEndpointDataSource`, already ended its subscription with the host.

**Only boot-time infrastructure may be restart-required.** `[ModuleRestartRequired(category, reason)]`
takes a `ModuleBootCategory` — `StorageDriver`, `Orleans`, `Authentication`, `Host` — a deliberately
closed list; the guard fails any other category ("a declaration whose reason is not boot-time
infrastructure is a defect to remove") and names the conversions an undeclared blocked module owes.

**Tests:** `ModuleRootServicesSwapTest` (3): a module registering a platform interface, options over its
own type and a hosted service swaps LIVE — a platform singleton that cached the interface answers from
N+1, the hosted service is stopped and restarted in order, and N is really collected; the negative
control: an N+1 that changes the forwarded shape is refused with N serving; a class-typed root service
the module implements is a blocker the guard names. `ModuleLiveUpdateGuardTest` (+1): a declared
non-boot-time category fails.

### A stale root stand-in is refused, never recursed (MeshWeaver#5388)

A portal process died of a `StackOverflowException` (image 3.1.11464, still on Autofac). Its dump shows
one thread in about 395 repeats of the same cycle: `ModuleServiceProvider.ResolveAt` → the module
container's `ModuleFallbackSource` stand-in for a ROOT registration → that root registration, a stand-in
that calls `ResolveAt` on the same module container → and round again.

**The shape.** The root holds a stand-in that resolves position `i` of type `T` in a module container,
which is what `ModuleOwnedRootSource` hands the root for a module-declared type. The module container does
NOT register `T` itself. After a live swap, for example, the new generation's container holds the new
generation's `T` and never the old one. Neither `isModuleType` nor `registrationsElsewhere` claims the
old `T`, and the root reports `T` as a service because the stand-in IS a root registration. So the
module's fallback answers `T` with a stand-in back to the root. `ResolveAt` then counted positions over
EVERY registration a lookup reached, sources included, so position `i` was that fallback stand-in, and
the cycle closed. Each hop started a fresh resolve operation, so no per-operation cycle detection saw it.
Autofac also cached a source's answers in the root, which kept the stale stand-in, and `IsService(T)`,
alive after the swap.

**Closed by construction.** `ModuleServiceProvider.RegistrationAt` counts positions among the module
container's OWN registrations only (`OwnRegistrationsOf`), never what its fallback adds. That has been
true since the Autofac replacement, MeshServiceProvider (#7145). A position the module does not register
is refused by name: *"T has no registration at position i in this module container."* The root also
caches nothing a source answers, so after a swap `ModuleOwnedRootSource` finds no owner for the old `T`
and the root reports it as not registered.

**Tests:** `AStaleRootStandInIsRefusedNotRecursedTest` (3) drives the exact shape: a root source standing
in for position 0 of a module container that does not register the type. It is refused by name, with and
without a caller. The control checks that the precondition is armed: the module can reach exactly one
registration of the type, its fallback's stand-in for the root's. The negative control is the same road
into a module that registers the type, which answers the module's instance. Mutating `RegistrationAt` back
to `RegistrationsOf` kills the test host with `Stack overflow.` (exit 134), and the frames repeat the
dump's cycle.

**A cycle made ONLY of transient factory registrations** (every stand-in is one) is refused by the
container as well (MeshWeaver#7400): `ResolveContext` restarts inside a factory, and only a shared
instance's slot (`InstanceSlot.BuildingThread`) caught re-entry, so the mutated run above once recursed
through 1,804 transient builds with no refusal. A transient whose resolve chain restarted is now marked
in flight per (scope, thread, service, cycle identity) — a stand-in keyed by what it stands in for, or
by the level whose source minted it — and a re-entry is refused by name (*"A circular dependency was
detected … building it asked for itself again on the same thread"*). Measured: with that guard, the
same `RegistrationAt` mutation no longer kills the test host. Both stale-stand-in tests fail on the named
refusal instead of the position message. See
[Hub Service Layers](../HubServiceLayers) → "Threads, cycles and faults".

## What is shipped (slice 5 — the builder hook, decomposed; the mesh hub's type registry re-applied)

**The builder hook** (`BuilderConfigurations` — MeshWeaver.AI, Graph.Views, Observability, Publish,
Stripe, Notifications, Hosting.Instance, Indexing, …) is no longer a blanket blocker.
`MeshBuilder.CaptureBuilderHooks` runs it against a CAPTURE builder and decomposes what it did
(`BuilderHookCapture`): the nodes it added, the root services it registered, its mesh-hub and
per-node-hub configuration, the mesh types it registered and its autocomplete exclusions. Each flows
through the same re-appliable seam an attribute contribution does — nodes as slots served from the
current generation, services through `ModuleServices`, per-node-hub configuration through the
current-generation indirection, mesh types through the shared type registry's factory (and, on a swap,
registered on the running registry). Anything else the hook touches is a NAMED blocker:
`ConfigureMesh`, node-type access gates, query routing rules, stream-routed or client-hosted address
types, installing modules itself, or returning a different builder. A hook that does not fully decompose
keeps ONLY its blockers and runs against the real builder as before — its parts never flow through
both paths.

**The mesh hub's configuration** — attribute `HubConfigurations`, `AddressTypes`, and the hook's
`ConfigureHub` — is re-applied to the RUNNING mesh hub on a swap when it MUTATES the configuration it is
handed (its type registry) and returns that same object; a dry run on a throwaway configuration
measures it at load. A delegate that returns a NEW configuration (the view packs' `AddViews`) is a
blocker: the mesh hub is built once per process.

**Measured after this slice** — the guard's own measurement over the 41 shipped module entry
assemblies, built from MeshWeaver.Plugins `origin/main` against this change's core: **25 live** (up
from 4), including **MeshWeaver.AI** (as measured; its full live swap with dependents and the
incident scenario is the next slice), every AI provider but Acp, Graph.Views, Observability, Publish,
Stripe, Notifications, Hosting.Instance, Indexing.PostgreSql, Markdown.Export, Speech, AppleMessages,
SelfUpdate.Aks, Testing, Import, Maps, Northwind, OgCard. **16 still blocked:**

| Blocker | Modules | Conversion owed |
|---|---|---|
| The mesh hub's configuration returns a new configuration (`AddViews`) | Blazor.Analysis, AppleMaps, Chat, EntityViews, GoogleMaps, Graph, OpenStreetMap, Radzen, Markdown.Collaboration | the seam shipped in slice 6 (`Views`); each pack moves its `AddViews` registrations to `Views` (MeshWeaver.Plugins) |
| HTTP endpoints | Courses, Mail.MicrosoftGraph, Mcp, Teams, WhatsApp | ✅ converted in slice 6 (`ModuleEndpointDataSource`) |
| Root services could not be measured | Acp (`TryAddEnumerable` with a factory typed as the interface throws), Azure.Blob, Mcp, Radzen (their dependency DLLs were absent from the measured Debug output — an artefact of the measurement, not of the modules) | Acp: register the harness by implementation type; the others re-measure against a published closure |

**Tests:** `ModuleBuilderHookSwapTest` (2): a module contributing ONLY through its builder hook — a node,
a root service, a mesh-hub type registration, per-node-hub configuration, an autocomplete exclusion —
swaps live: the service answers from N+1, the node is served from N+1, the running mesh hub's type
registry maps the name to N+1's type, and N is collected; the negative control: a hook that adds a
query routing rule is a blocker the guard names.

## What is shipped (slice 6 — endpoints, views, platform independence)

| Piece | What it does |
|---|---|
| `ModuleEndpointDataSource` (`MeshWeaver.Hosting.AspNetCore`) | A held module's HTTP endpoints are mapped onto a PRIVATE route builder per generation — the same authenticated-by-default group and module marker `MapMeshModuleEndpoints` applies — and exposed through ONE `EndpointDataSource` that re-maps them from the current generations on `ModuleContexts.VersionChanged` and fires its change token, so ASP.NET Core routing rebuilds its matcher. The refusal is scoped PER MODULE: each module's endpoints are published with the generation that mapped them, and only a module whose current generation differs is re-mapped. A module whose new map throws, or whose routes collide with another endpoint the host serves (another module's included), keeps serving its previous endpoints; it is logged at Critical with the module and the collision named, and it is retried on every later swap. Its served endpoints hold the previous generation's types, so that generation stays loaded and is not collected — one retained generation per refused module, until a later map is accepted. Every other module's change is still published, so one module's bad generation never freezes endpoint updates for the rest. The pending maps are first decided together, so two modules that exchange routes across swap waves both go live once the exchange completes, rather than each being refused against the other's stale map. Image-bound modules map as before. Endpoints are no longer a blocker. |
| `MeshNodeProviderAttribute.Views` + `IViewContributionSource` (`MeshWeaver.Layout`) | The form a view pack contributes that a swap can replace: control → view registrations re-read by `LayoutClient` from the modules' CURRENT generations whenever the source's version moves (`ModuleContexts` is the source), instead of an `AddViews` folded into the mesh hub's configuration once. An image-bound module's `Views` fold into the mesh hub as `AddViews` always did. `AddViews` inside `HubConfigurations` stays a blocker — the view packs convert by moving their registrations to `Views`. |
| Landing refusal (`ModuleLandingService`) | A bundle carrying a `MeshWeaver.*` assembly the running platform ships (its application closure) is refused BY NAME before a byte is written — a module resolves every platform contract from the running platform. Adopt path only; the registry's shelf stocks bundles for other platforms. |

**Tests:** `ModuleEndpointsSwapLiveTest` (real ASP.NET Core routing on a TestServer): the route answers
from N+1 after the swap with no restart and N is collected; a generation that ADDS endpoints is mapped
live even when boot mapped none; a held module whose boot route collides with the host is refused at
startup; the negative control: a colliding re-map is not published and the previous route keeps serving;
one module's refused swap does not freeze another module's swap, which still goes live; and two modules that exchange routes are both published once the exchange completes.
`ModuleViewsSwapTest`: the SAME layout client resolves the control to N+1's view after the swap; the
negative control: views folded through `HubConfigurations`' `AddViews` are a named blocker.
`ModulesUpdateIndependentlyOfThePlatformTest`: the platform stays fixed while M goes N → N+1 → N+2 live
through the real landing path — N+2 recorded against an older platform build and a floor below the
platform, so no identity-equality gate, no seal, no roll. An N+3 whose floor is above the platform is
declined by name by the reconciler's own decision function (`ModuleUpdateDecision`, called directly —
the decline happens BEFORE anything lands, so no above-floor bundle reaches the landing path), and the
next activation pass then takes only the sibling's landed update while M keeps serving N+2. The
reconciler's own wiring of that decision is exercised end to end elsewhere, from a real registry
serving an above-floor bundle:
- `PackagesAutoUpdateTest.AnIncompatibleFloor_IsDeclinedByName_AndTheRunningVersionKeepsServing`
  (the unattended reconcile pass, `ReconcileNow`);
- `ModuleReloadByRestartTest.ANewerVersionAboveTheFloor_IsDeclinedByName_AndTheRunningVersionKeepsServing`
  (the attended reload);
- `ModuleBundleFloorHoldTest` step 2 (the adopt).

Each asserts the held bundle is not downloaded and the running version keeps serving. A bundle carrying
a platform assembly is refused naming it, and the same bundle without it lands.

## What is shipped (slice 7 — the REAL MeshWeaver.AI update goes live; keyed services; added background services)

**Measured on the actual incident pair.** MeshWeaver.AI was published twice against this core — from
MeshWeaver.Plugins `b7a083d98~1` (no `ThreadPreparation.Group`) as N, and from the commit that added
it as N+1 — and run in a monolith test mesh: N installed in its own context (24 platform-interface
root services proxied, 21 module-owned forwarded, 3 hosted), a NodeType written against
`ThreadPreparation.Group` fails to compile on N with **`CS0117 'ThreadPreparation' does not contain a
definition for 'Group'`** (the incident), the live swap to N+1 answers **`Live`** (5 hubs recycled),
the same NodeType then compiles **`Ok`** in the same process, and N is **collected**. Three changes
made that true, each found by running it:

1. **Added background services are not a shape change.** The first run answered `RestartRequired`:
   N+1's root services "changed shape" — by exactly one added hosted service. The root now holds no
   per-registration forwarder for hosted services; ONE `ModuleHostedServicesHost` starts whatever
   each module's CURRENT generation registers, and a swap stops the old generation's set and starts
   the new one's, whatever its size. Start and stop stay SEQUENTIAL, as the generic host runs root
   hosted services: modules in name order, each module's services in registration order, each once
   the previous start completed; stop is the exact reverse (`ModuleHostedServicesStartInSequenceTest`).
2. **The content-type registry let go of nothing.** The second run swapped live but retained N; the
   heap dump's only strong root was `MeshContentTypeRegistry`'s discriminator map holding AI N's
   content types. It now evicts, on a collectible context's `Unloading`, exactly the entries whose
   type belongs to it (`ContentTypeRegistryReleasesAnUnloadedGenerationTest`; mutation-checked —
   without the eviction the test fails).
3. **Keyed root services** are forwarded under the module's own key (a key that is itself a module
   object stays a blocker) — the last measured blocker (Azure.Blob's keyed `IStreamProviderFactory`).
   A keyed registration of a type the module DECLARES is answered by key from the root too
   (`ModuleOwnedRootSource` → `ModuleServices.KeyedRegistrationsElsewhere`), and never counted among
   the type's unkeyed registrations.

**2026-10-16 — the index is part of the shape (MeshWeaver#6847).** The shape compared only the
forwarded subsequence (route, type, lifetime), while every root forwarder holds the BOOT generation's
absolute registration index. WhatsApp 1.3.2 → 1.4.2 added its inbox registrations, among them the
module-owned `IWhatsAppRecentChats`, which moved the forwarded indices without changing that
subsequence: the swap went live on the company instance's three running pods at 00:15Z, a root forwarder
then resolved `mw-module-service:10` in N+1 — the unkeyed module-owned registration — and every hub
activation leasing it faulted (API-token validation included) until the pods restarted; pods that
BOOTED on 1.4.2 were unaffected. `ModuleServices.Shape` now prefixes each forwarded registration with
its index, so such an N+1 is `RestartRequired` with N serving
(`ModuleRootServicesSwapTest.AnUpdateThatMovesAForwardedRegistrationsIndex_IsRefused_AndNKeepsServing`;
mutation-checked — without the index the swap answers `Live`). An addition AFTER the forwarded
registrations (the hosted-service case above) still swaps live.

**The class is loud now, not only the instance refused.** The swap refusal above is the root fix; what
let the incident run six and a half hours is that nothing READ the broken state — `/health` said
Degraded (the exception quoted inside two unrelated entries), every pod stayed READY, nothing paged.
Three pieces close that, each built on the refusal rather than beside it:

- **The layout is explicit.** `ModuleServices.ForwarderLayout` is the boot generation's forwarded
  registrations (index + shape) and every swapped-in generation INHERITS it
  (`ModuleContexts.PrepareServices`); the swap compares N+1's shape against that layout — the
  forwarders the root actually holds — never merely against the generation it replaces.
- **Readiness reads it.** `ModuleContexts.UnresolvableForwarders` asks each current generation, for
  every forwarder in its layout, whether it can answer: the registration at that index exists, is the
  forwarded one the forwarder was laid out for, and its container holds the scope key. The
  `module_forwarders` health check (`ModuleForwarderHealthCheck`, readiness + roll-gate + census tags
  in `Memex.Portal.ServiceDefaults`, so it holds `/ready` and never fails the startup probe) is Unhealthy on any gap, naming module, generation, key and service,
  and prints its clean census otherwise. One reading is enough — a built container never changes —
  so it cannot flap on a transient lease failure and needs no window
  (`ModuleRootServicesSwapTest.AGenerationWhoseForwarderMoved_FailsReadiness_NamingTheModuleAndTheService`,
  arranged as the pre-refusal swap committed it and mutation-checked; its negative control
  `EveryForwarderResolves_AfterBootAndAfterALiveSwap_SoReadinessHolds`). Each generation is read under
  the same context and services leases a forwarder's resolve takes, currency re-checked, so a live swap
  that retires it mid-scan defers the disposal to the scan's release
  (`ModuleRootServicesSwapTest.ARetirementDuringTheForwarderScan_IsDeferredToTheScansRelease`).
- **The fleet watch alarms on it.** `Hosting/Issue/Source/ModuleLeaseAlarms` (MeshWeaver.Plugins) reads
  every replica's `/health` — unready ones included, since this check is what makes them unready —
  for a `module_forwarders` finding or the raw `mw-module-service:… has not been registered` text an
  older image quotes, and raises `ModuleServiceLease`: a Warning on the first pass, Critical (a platform
  notification to global admins) once it holds on the next.

### An optional module that cannot serve a service costs that feature — never sign-in (MeshWeaver#6847)

The shape check closes the road the incident came by. It does not answer the question the incident
raised: **why could an optional chat module stop people signing in at all?** The chain, read from the
code the 2026-10-16 build ran:

1. A per-node hub activation resolves its node first (`MessageHubGrain` → `PathResolutionService`), and
   that resolution is a query.
2. The query fan-out (`MeshQuery.SelectMatchingProviders`) sends EVERY query to EVERY provider — it
   deliberately does not pre-filter by `Matches`, because storage providers answer `false` for an
   unscoped query they must still serve. WhatsApp's `WhatsAppChatQueryProvider` is one of them, forwarded
   to the root as a `ModuleServiceProxy`.
3. Each module provider's leg takes a lease on subscribe (`ModuleServiceProxy.Leased` →
   `ModuleContexts.TryLeaseModuleService`), and the lease RESOLVES the registration in the module's
   current generation. That resolve threw `mw-module-service:10 (IWhatsAppRecentChats) has not been
   registered`, the lease rethrew, the leg errored, and the merge errors the whole query on any leg's
   error.
4. So node resolution faulted for every hub that activated (the `[ACTIVATE] … activation faulted`
   lines), and `UserIdentityCache`'s directory query (`nodeType:User`, unscoped, as System) faulted with
   it — the "We could not check your account just now" every user saw. (The OAuth authorization code
   itself is written storage-direct by `OAuthCodeStore` and touches no hub or query; what failed around
   it was the identity and every hub-backed read.)

The same lease guards the access-rule set (`NodeTypeAccessRuleSet` takes one per rule) and, since this
change, the options setups a module forwards. **The rule is now that a registration the module's current
generation cannot PRODUCE is ABSENT, never a fault in a consumer that leases it:**

| Where | What changed |
|---|---|
| `ModuleContexts.TryLeaseModuleService` | A resolve that faults answers null, which every leasing consumer already reads as "this module's feature is absent" (the shape a module withdrawn at boot has had since #6542) — except the caller's own scope ending, which propagates: a disposal in the causes (also when the container wrapped it in a resolution exception) AND a caller scope that has actually ended. A constructor that ran into a disposed dependency of its OWN under a live caller is a construction fault like any other. Only a **generation-wide** fault is a durable verdict: a registration the generation does not lay out the way the root forwards it — nothing registered under the forwarded key, or an instance that is not what the forwarder forwards — is recorded in `ModuleServices.Unservable` for THAT generation and reported once (stderr and an error log `[MeshWeaver.Mesh.UnservableModuleService]` naming the module, the registration, the generation and why). A later generation starts clean. Any other construction fault — one caller's scope, one attempt at a singleton — is that lease's alone: nothing is recorded, the next lease resolves afresh, and it is logged once per registration and exception type. |
| `ModuleServiceProxy.Serves` | False for a recorded unservable registration, so a set built over the root's `IEnumerable<T>` leaves it out. |
| `MeshQuery.WithMembers` | A module's provider is leased ONCE when a fan-out is SUBSCRIBED; that lease is its membership and the instance its leg runs on, and it is released when THAT LEG terminates or is unsubscribed — a module leg that answered and completed holds nothing while the merged query stays live on its storage legs, so it never keeps a retirement waiting. A resubscribed leg (the transient-connect retry) takes a lease of its own; a lease no leg took is released with the subscription. A provider that cannot be leased is not in the set whose Initial frames the merge awaits, so not even the first query after the break faults — and a generation change between building a query and subscribing it, or a transient provider whose next construction fails, can no longer err the merged query through a second resolve. Building the fan-out leases no module provider at all (MeshWeaver#7218): each is held under its forwarder's label, its name read when a subscription leases it, so nothing is dropped for the mesh's life; `NodeTypeAccessRuleSet` retains such a rule the same way, reading its node type at lookup. |
| `ModuleConfigureOptionsForwarder` | Leased instead of resolved. The options factory runs every setup of an options type for EVERY name, so a module's `AddHttpClient(name)` forwarder runs when the sign-in's OIDC handler builds its own `HttpClientFactoryOptions`; a forwarder that threw there made one module's broken registration a fault in every named build of that type. A module that is not held, or cannot produce the setup, now configures nothing. A TEMPORARY construction fault also configures nothing for that build — no name's build fails — but the monitor-cache entry that build fills is removed (the cache inserts it before building), so the incomplete options are never cached and the next build of the name carries the module's setup again. Throwing instead would be worse: the monitor cache keeps a build that threw and rethrows it for that name until invalidated. |

A DIRECT call through a forwarder still throws the real error: that call IS the feature, and failing it is
correct. This is not a catch-and-continue: the fault is turned into a recorded, reported verdict about
one generation of one module, and the only thing that stops working is what that module contributed.

`ABrokenOptionalModuleNeverBreaksSignInTest` pins it. A module forwards a query provider and a named
setup of a platform options type; the test then makes the held generation the incident's — a
module-owned registration inserted ahead of both, committed past the shape check — and runs what
sign-in runs: the identity directory query, a node write, a hub-activating read of it, and a named
options build of the type the module also configures. Controls: before the break the module's provider
answers its own node and its setup configures its own name; after it a direct resolve of the broken
registration still throws "has not been registered". On the pre-fix code the identity query errors with
exactly the memex message; with `TryLeaseModuleService` rethrowing the fault, the same. The same class
pins a query BUILT while the module was healthy and SUBSCRIBED after the break.

**Access rules fail CLOSED, not absent.** The isolation above is right for a query provider, an options
setup or a health check — a feature that is missing is the safe reading. It is NOT right for an access
rule: a rule that is skipped makes the validator fall back to the ordinary path permission, so a caller
holding an ordinary Update grant would pass a write the module's rule exists to refuse. The lease
therefore says WHY it answered null (`ModuleLeaseFault`: the module is not held or is retiring — absent;
or it is held and could not produce the registration this time, or in this generation at all — faulted),
and `NodeTypeAccessRuleSet` hands out a rule that DENIES for the type a faulted rule governs. Other types
never meet it. A rule retained because its binding failed when the set was built fails closed for the
type it last named; one that has never been produced in the process is unknown, and fails closed for
every lookup while it faults, because nothing says which type it governs. The lease is held through the
fault's classification and report, so a retirement overlapping the fault waits for it instead of
disposing the container under the classification.

`AModuleConstructionFaultStaysLocalTest` pins the rest with transient module services whose
constructions it fails on demand: a provider constructed once per query subscription; a completed module
leg releasing its lease while the merged identity query stays live, so a retirement unloads the module;
a failing caller scope followed by a healthy one; a constructor's own disposed dependency isolated under
a live caller, while a caller whose scope has ended still sees its `ObjectDisposedException`; a provider
and an access rule whose lease failed while the fan-out and the rule set were built serving afterwards;
a temporary options-setup fault that is never cached as complete options; a duplicate provider
registration run once per query; a provider name that throws after a failed binding releasing its lease;
a denying access rule that cannot be constructed failing closed for its type only; and a construction
fault overlapping a retirement.

**Not established:** a module provider whose OWN `Query` faults (as opposed to one that cannot be
produced) still fails every unscoped query it is asked — including the identity directory — because the
merge errors on any leg's error and the fan-out does not pre-filter. That is the next isolation seam
(scoping the identity and node-resolution reads to the providers that can hold them), not done here.
`/health` does not yet list unservable registrations; the error log line is the report.

**Measured over all 41 shipped modules** (Plugins with its slice converting the view packs and fixing
Acp, built against this core; the three whose Debug output lacks NuGet dependencies measured from a
published closure; measured in an ASP.NET Core test host): **41 live, 0 blocked, 0 declarations
needed.**

**Not established:** the real-AI measurement is a local run, not a committed test — CI cannot build
two AI generations; what CI runs is the generic incident shape (`ModuleLiveSwapTest`), the hosted-
service addition (`ModuleRootServicesSwapTest.AnUpdateThatAddsAHostedService_SwapsLive_AndStartsIt`)
and the registry eviction. That a THREAD then runs end to end on N+1 (a model round) was not run.

## What is owed

- **Across replicas.** A replica swaps on its OWN self-update check (on a landing wave it proposed, and
  on the safety-net cadence on every replica); a replica that is not the lander activates on its next
  check, not instantly. The deployment-wide `PendingRestart` marker is still cleared only by a boot —
  harmless, because each check now decides from what is pending in its own process.
- **A brand-new module** (not installed at boot) is `NotHeld` and still activates by restart: there is
  no boot position for its nodes yet.
- **Slice 3 — the declarations (MeshWeaver.Plugins, branch `feat/module-live-plugins`).** All 37
  blocked modules carry `[assembly: ModuleRestartRequired("<what was measured>")]`; re-measured after
  the change, all 41 pass `ModuleLiveUpdateGuard` (4 live, 37 declared). It compiles only against a
  platform pin that contains the attribute, so it lands after this change reaches that pin — core
  first.
- **Slice 4 — enforcement.** Run the guard where every satellite already passes every module: the
  module pack step (`meshweaver-plugin-build`), so a module that blocks a live swap without declaring
  it fails to pack, fleet-wide. It has to land AFTER slice 3, or it reds every satellite's pack lane
  on modules that have not declared yet.
- **Converting surfaces** so the 37 can drop their declarations: a re-appliable seam for mesh-hub
  view and type registrations, hub-scoped instead of root service registration, and the builder hook
  decomposed into re-appliable hooks. Also: the bundled-platform-assembly refusal at landing, and the
  platform-fixed tests (P fixed while M goes N → N+1 → N+2 live, N+1 built against an older compatible
  platform build, N+3 above its floor declined by name while siblings keep updating).

## How the modules classify today (measured)

Measured by running `ModuleContributions.Of(...).MeasuredLiveUpdateBlockers()` — the guard's own
measurement — over every module the MeshWeaver.Plugins packages ship (41 entry assemblies: every
`*/index.json` with a `content.module`, built from Plugins `origin/main` `f78c46725` against this
change's core). **4 of 41 are live-updatable as the platform stands; 37 measure blocked**, and until
they are converted each falls back to the automatic, approval-free restart:

| Classification | Modules | Why |
|---|---|---|
| **Live** | Import, Maps, Northwind.Application, OgCard | nodes only, or nodes + every-per-node-hub configuration |
| Blocked — root services (`WithGlobalServiceRegistry`) | the AI providers (Acp, Anthropic, AppleIntelligence, AzureFoundry, ClaudeCode, Codex, Copilot, OpenAI, WebSearch), Azure.Blob, Speech, Mcp, Teams, Courses, Mail.MicrosoftGraph, WhatsApp, AppleMessages, Markdown.Export, SelfUpdate.Aks, Blazor.AppleMaps, Blazor.GoogleMaps, Blazor.Radzen | the root container is built once |
| Blocked — the mesh hub's configuration (`HubConfigurations`) | Blazor.Analysis, Blazor.Chat, Blazor.EntityViews, Blazor.Graph, Blazor.OpenStreetMap (+ AppleMaps, GoogleMaps, Radzen), Markdown.Collaboration | the mesh hub's configuration is immutable and folded once (`AddViews` returns a new `MessageHubConfiguration`) |
| Blocked — the builder hook (`BuilderConfigurations`) | **MeshWeaver.AI**, Graph.Views, Hosting.Instance, Indexing.PostgreSql, Notifications.Channels, Observability, Payments.Stripe, Publish, Testing (+ AppleMessages, Mail, Markdown.Collaboration, Markdown.Export, SelfUpdate.Aks, WhatsApp) | arbitrary boot-time configuration |
| Blocked — HTTP endpoints | Courses, Mail.MicrosoftGraph, Mcp, Teams, WhatsApp | the endpoint map is built once |

`MeshWeaver.AI` additionally has dependents bound to it (every AI provider, `Blazor.Chat`, the in-mesh
NodeTypes compiled against it), so even once its own contributions are re-appliable its swap is the
swap of that whole set — the shape `DependentsOf` exists for.

🚨 **This is the measured gap between the rule and the fleet.** Live is the default the platform now
takes, but the modules as written contribute almost entirely through boot-time-only surfaces. Making
"most modules live" true is platform work on those three surfaces — a re-appliable seam for mesh-hub
view and type registrations, hub-scoped instead of root service registration, and decomposing the
builder hook into the re-appliable hooks — and each converted module then drops its blocker. Until
then the guard demands the declaration from all 37, which is what makes the gap visible rather than
silent.

## What is NOT established

- Which of those 41 are IMAGE-BOUND on the running portal (in its `TRUSTED_PLATFORM_ASSEMBLIES`, so
  loaded into the default context and never swappable whatever they contribute): the measurement ran
  in a test process whose TPA is not the portal's. `Memex.Portal.Distributed` references
  `MeshWeaver.Blazor.Views` directly (TPA); the `MeshModuleClosure` seeds (`MeshWeaver.AI`,
  `Blazor.Chat`, `Markdown.Collaboration`, `Mcp`, …) live under `modules/` and are NOT in TPA, so they
  now run in their own contexts.
- Behaviour of Blazor component types and Orleans-serialised payloads from a collectible module
  context in the running portal: no portal-level test of a module in its own context ran here (the
  dev Monolith did not boot in this environment on `main` either — `IMeshService` unresolved — so it
  could not serve as the control).
- Boot ORDER between a module and a sibling it references during attribute materialisation: a
  dependency must be installed before a dependent whose contribution GETTERS touch its types (the
  same constraint the default context had); a dependency touched only at run time resolves lazily.
