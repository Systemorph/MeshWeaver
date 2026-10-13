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
| `ModuleLoadContext` (`MeshWeaver.Mesh.Contract`) | Collectible context for ONE generation of ONE module, named `module:<Name>#<n>`. Resolves **platform first** (anything the default context can bind — one `MeshNodeProviderAttribute`, one `IMessageHub` in the process; a bundled platform copy is never used), then **another module** (the current generation of a module whose entry assembly is asked for, or an assembly a module it depends on already holds — each edge recorded as `DependsOn`), then the generation's **own directory**. Natives resolve through the same candidates as `ModuleNativeAssets`. Marked `IPlatformLoadContext`, so the impersonation guard classifies module code with the platform. Purges Autofac's and System.Text.Json's process-static caches on `Unloading`, as a NodeType context does. |
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
| Re-appliable seams (`MeshBuilder`, `StaticMeshNodeListProvider`) | A module's nodes are served from its CURRENT generation in the seed tier, at the position its boot nodes held (same precedence as before). Its every-per-node-hub configuration goes through one indirection read when a hub is built. `InstalledModuleAssembly` for a module in its own context is TRANSIENT and answers the current generation, so the compile reference set (`MeshNodeCompilationService`, now keyed by the module MVIDs) and `InstalledModulesFingerprint` (now read live) follow a swap — and every build stamped with the old fingerprint reads as stale and rebuilds against N+1 on its next activation. 🚨 None of these registrations captures a module's boot `Assembly` strongly: one did, and it rooted generation N for the life of the process (`ModuleLiveSwapTest`'s collection assertion caught it). |
| `ModuleLiveUpdater` (`MeshWeaver.Graph`) | The swap: load N+1 → refuse (N untouched) on any blocker of N, N+1 or a dependent, or a load / materialisation failure → load N+1 and re-load every dependent into ONE `ModuleSwapStage` (a staged context binds the STAGED N+1, so the dependents re-bind before anything is current) → only when all of them loaded, materialised and prepared, `CommitAll` makes the whole set current in one step; a failure before that unloads the staged set, which no request was ever routed to, and N never stopped serving (#6128 review: committing N+1 first published it to every reader of the generations while a dependent could still fail) → recycle this process's per-node hubs bound to the module's NodeTypes or nodes (every per-node hub when a generation configures every hub, or when an in-mesh build here is linked against a swapped module) → once those hubs are DEAD, retire the old generations. Serial (a subject and `Concat`, no gate), so a second update that arrives mid-swap is applied after it. Outcomes are an open vocabulary (`ModuleSwapKind`: `Live`, `UpToDate`, `RestartRequired`, `Failed`, `NotHeld`); `NeedsRestart` is the one question a caller asks. **`Swap(path)` is `internal`** (granted to `MeshWeaver.PluginCatalog` only): it runs the bytes at a path as platform-classified code, so a public method on a mesh singleton would let any code that resolves it name the bytes or roll a module back to an older directory. The only way in is `ModuleLiveActivation.ActivatePending`, which takes no path and swaps only the pinned copy of what the landing service landed. A generation's contributions are recorded **before** it is made current, so no reader ever sees a current generation whose contributions are null. |
| `ModuleLiveActivation` (`MeshWeaver.PluginCatalog`) | For every module `PendingModuleActivations` reports landed-but-not-serving in THIS process, swap the PINNED copy of its landed generation in live. A module this process does not hold in its own context is `NotHeld`. A pass that cannot read the state keeps the restart. |
| `SelfUpdateHostedService.ConsiderRestart` | 🚨 **Live first.** Where a pending activation used to mean a restart, the check now calls `ModuleLiveActivation`; only when something did not go live does it take — or hand to the control lane — the automatic restart, whose announcement now names the modules and reasons. The verdict `ActivatedLive` records a pass that needed no restart. A host without the plugin catalog keeps the old path. |

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

## What is shipped (slice 4 — root services, and only boot-time may restart)

**Root services are converted.** The most common blocker — a module registering ROOT services
(`WithGlobalServiceRegistry`; 21 of the 37 measured) — no longer forces a restart:

| Piece | What it does |
|---|---|
| `ModuleServices` (`MeshWeaver.Mesh.Contract`) | The module's registration delegates run against a COPY of the root collection as it stands when the module installs, so every `TryAdd` decision is the boot one; what they ADDED is the module's set, routed (`ModuleServiceRoute`, open vocabulary): a platform **interface** → `Proxy`, a platform **class** → `Current`, `IHostedService` → `Hosted`, a type the module declares itself → `ModuleOwned`, an open generic or infrastructure → `Private`. |
| Root forwarders (`ModuleServiceForwarding`) | The root gets exactly the platform-typed registrations it would have had — as forwarders that never name a module type. `Proxy` is ONE stable `DispatchProxy` per registration that forwards every call to the CURRENT generation, so a platform singleton that cached it follows a swap. `Hosted` is started at boot and, on a swap, the old generation's instance is STOPPED and the same registration STARTED from the new one. |
| `ModuleServiceProvider` (`MeshWeaver.ServiceProvider`) | The module's services live in an Autofac container of their OWN, which reaches the root only for types that do not name the module — through ONE stand-in **per root registration**, so `IEnumerable<T>` in the module is the root's whole list in the root's order and a single resolve still answers the root's LAST. Each stand-in resolves in a root CHILD scope owned by the asking module scope (`RootResolutionScope`), never in the root itself: a scoped root service built in the root is shared there for the life of the process, under the root's lock — the silo-wide wedge of [Reading a Silo Eviction](../ReadingASiloEviction) (MeshWeaver#6432); a closed generic over a module type (`IOptions<ItsOptions>`, `ILogger<ItsType>`) is closed in the module's container from the root's open-generic registration. |
| Module types, wherever asked | A type a module DECLARES is answered from the modules wherever it is asked — `ModuleServices.RegistrationsElsewhere`: the declaring module's registrations (`ModuleServiceProvider.ResolveAt`, one per registration) plus every **contribution** another module made of it (`ModuleServiceRoute.Contributed`). The ROOT serves it through `ModuleOwnedRootSource` (so the mesh hub and every per-node hub, which are scopes under the root, see it), and another module's container through `ModuleFallbackSource`. |
| Contributions | A registration of a type ANOTHER module declares (`ModuleServiceRoute.Contributed` — every AI provider registering MeshWeaver.AI's `LanguageModelCatalogSource` and `IChatClientFactory`) is never put in the root: a root forwarder would pin the declaring module, and a `DispatchProxy` cannot be built over a collectible interface at all ("A non-collectible assembly may not reference a collectible assembly"). It is served to the declaring module's container after that module's own registrations, and to the root's list. |
| The swap | `ModuleContexts.PrepareServices` re-runs N+1's delegates against the SAME boot prefix and requires the same routes and shape — the root's forwarders were laid out at boot; a changed shape is refused by name and N keeps serving. |

🚨 **A "copy of the root collection" copies the DESCRIPTORS, so a registry that registration code writes
into must be copied too.** `AddHttpClient<T>()` records "client name → `T`" in the `HttpClientMappingRegistry`
INSTANCE the collection holds and refuses a second type under the same name — and the name is derived
from the type name alone. Shared by reference, generation N's `AddHttpClient<WebSearchPlugin>()` wrote into
the ROOT's one registry (pinning N's collectible context), and N+1's `WebSearchPlugin` — the same name, a
different `Type` from a new load context — was refused, failing the live swap of MeshWeaver.AI
(MeshWeaver.Plugins#3518). `ModuleServices.Probe` now hands each probe its own copy of that registry, seeded
with the root's entries, so a module colliding with a PLATFORM client is still refused exactly as at boot.
`ALiveSwapReRegistersATypedHttpClientTest` pins the N→N+1 probe and that root-collision control.

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
singletons, into the root's registrations for every type that names no collectible assembly; a root
singleton ends the walk). Neither is a path the lock order needs: a root singleton that takes a scoped
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
which every container MeshWeaver builds registers **externally owned** (`PopulateMeshWeaver`). An
ordinary factory registration is owned by the scope that resolves it, so the forwarder used to hand
ownership of a module's instance to whoever asked: the first circuit to end disposed a module
SINGLETON under every other user, and disposed a module SCOPED instance a second time after the module
scope that circuit owned had already disposed it; a `Proxy` over an `IDisposable` interface forwarded
the same disposal. Ownership stays where the instance was built — the generation's container for a
singleton, the caller-owned module scope for a scoped one. The forwarder is registered at its own
position in the collection while `Populate` walks it, so the default registration of a service and the
order of an `IEnumerable` of it are unchanged (`ANotOwnedForwarderKeepsItsPositionTest`).
`AModuleForwarderOwnsNothingItReturnsTest` measures one circuit ending on the test mesh, on an
`IHostApplicationBuilder` host and on a legacy `IHostBuilder` host: the singleton serves the other
circuit undisposed, each scoped instance (class and proxy route) is disposed exactly once.

**One lock owner per caller.** Autofac builds a scope's shared instances under that scope's one
shared-instance lock. A caller-owned module scope is a scope of the MODULE container, and its root
resolutions are made in the caller's own scope, so the two locks were taken in both orders: a
caller-scoped ROOT service whose construction resolves a module's scoped service (caller, then module
scope), and that module service's construction taking a caller-scoped root service not built yet
(module scope, then caller) — two first builds overlapping in those orders deadlocked. So a
caller-owned module scope, and every scope begun from it, takes its CALLER's lock as its own
(`CallerSharedInstanceLock`): one monitor for the caller and everything it owns, re-entered by the
thread that already holds it, so there is no order to invert — whichever road reaches the module scope,
including a module service that keeps its `IServiceProvider` and resolves later. Autofac offers no API
to hand a scope its lock, so the lock field is set before the scope is handed out; Autofac is pinned, and
a version without that field fails loudly at the first caller rather than silently bringing the
deadlock back. `CallerAndModuleScopedServicesBuildInOneLockTest` forces the overlap; its negative
control gives the same module scope its own lock back and deadlocks.

**Open: a withdrawn module's `InstalledModuleAssembly` (MeshWeaver#6557).** A module withdrawn at boot
keeps the transient `InstalledModuleAssembly` registration it was given while the mesh was configured,
and once its boot assembly is collected that registration throws "no longer held by this mesh's module
registry" from inside every `GetServices<InstalledModuleAssembly>()`. That is how the Radzen
withdrawal above refused the pod's readiness: the pre-warm sweep enumerates installed modules. With
circuit-bound services no longer built at boot, Radzen is not withdrawn; a module whose root-reachable
singleton genuinely throws still is, and still meets this. Moving the registration into an Autofac
registration source was tried and rejected: hosts that build the collection with MS DI's own
`BuildServiceProvider()` ignore sources and lost every held module's `InstalledModuleAssembly`
(`ConfiguredModuleActivationTest`).

**Why.** Autofac holds a scope's shared-instance lock for the whole build of a shared instance, and
that one lock covers every shared instance of the scope; an instance that exists is answered without
the lock. The root reaches a module's services from inside its own builds — `ModuleOwnedRootSource`'s
stand-ins, the `Current` route's transient forwarder, `ModuleServiceProxy.Invoke` (a module's
`IConfigureOptions<T>` run when a root singleton reads `IOptions<T>.Value`), and the
`ModuleHealthCheckOptionsForwarder` run from `DefaultHealthCheckService`'s constructor — while a module
singleton's constructor takes root singletons. Two FIRST builds in those two orders deadlocked (root
lock then module lock, against module lock then root lock). With every module instance built before
publication, the first order never waits, so there is no cycle.
`RootAndModuleSingletonsBuildInOneLockOrderTest` forces the overlap; its negative control publishes
the same container unbuilt and deadlocks.

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
The residual: a shared instance that only a registration SOURCE makes on demand and that no
constructor takes — a closed generic over a module type (`ILogger<ItsType>`, `IOptions<ItsOptions>`)
resolved later from inside a factory or a method rather than taken by a constructor. Its first build
still takes the module container's lock while the root may hold its own. Such a type is built with
the instance that takes it in its constructor, which is the shape to prefer.

**A staged generation binds its swap.** Its container is built before the commit, so it sees the
other generations of the SAME swap in place of the ones they replace (`ModuleServices.Stage`, the
stage-aware lookups in `ModuleContexts`) — the AI module's catalog is built with its re-loaded
providers' contributions, not the retiring ones'. A sealed stage answers nothing and forgets its
list, so from the commit on every lookup is the current generations, and no stage pins a generation
a later swap retires.

**Three pins found and fixed on the way — each caught by the collection assertion failing, then read off
a heap dump (`dotnet-dump` `gcroot`, ClrMD for the referrers):**

1. An Autofac CHILD scope of the root — isolated and load-context scopes included — caches every service
   it asks its parent about in the PARENT's registered-services tracker, by type, forever
   (`TypedService(IOptions<GreeterOptions>)` in the root registry). Hence the module's own container.
2. Autofac.Extensions.DependencyInjection's `FromKeyedServicesUsageCache` registers with the weakly-held
   `ReflectionCacheSet.Shared` once; after `Shared` is re-created, a `Clear` never reaches it — and its
   keys (`OptionsFactory<ModuleOptions>`) name only `Microsoft.Extensions.Options`, so an
   assembly-only predicate never matches. `ReflectionCacheEviction` now clears it directly and matches
   on generic arguments; a test pins that the cache stays reachable.
3. The static-node query catalog snapshotted the boot generation's nodes and was re-taken only on the
   next query, so a swapped-out generation stayed referenced until something asked. It is now dropped on
   `ModuleContexts.VersionChanged`, and a module's nodes enter the boot node list only as SLOTS (content,
   hub configuration and service delegates stripped) — the provider serves the current generation's nodes
   in their place.

**Only boot-time infrastructure may be restart-required.** `[ModuleRestartRequired(category, reason)]`
takes a `ModuleBootCategory` — `StorageDriver`, `Orleans`, `Authentication`, `Host` — a deliberately
closed list; the guard fails any other category ("a declaration whose reason is not boot-time
infrastructure is a defect to remove") and names the conversions an undeclared blocked module owes.

**Tests:** `ModuleRootServicesSwapTest` (3): a module registering a platform interface, options over its
own type and a hosted service swaps LIVE — a platform singleton that cached the interface answers from
N+1, the hosted service is stopped and restarted in order, and N is really collected; the negative
control: an N+1 that changes the forwarded shape is refused with N serving; a class-typed root service
the module implements is a blocker the guard names. `ModuleLiveUpdateGuardTest` (+1): a declared
non-boot-time category fails. `ReflectionCacheEvictionReachesAutofacsKeyedServicesCacheTest` (1).

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
