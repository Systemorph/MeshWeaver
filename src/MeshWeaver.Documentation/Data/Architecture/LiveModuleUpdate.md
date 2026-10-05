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
| `ModuleContexts` | The mesh's registry (a mesh singleton, disposed with the mesh): `Load` a generation into a fresh context, `Commit` it as current (returning the one it replaces), `Retire` the old one — unloaded on a POSITIVE quiescence signal from its `AlcLeaseRegistry`, never on a timer, and recorded on `CollectibleContextUnloads` so "really collected" is observable. `Resolve(name)` is the explicit lookup NodeType builds and script sessions use; `DependentsOf(name)` is the set a swap has to take with it. |
| `MeshBuilder.InstallModules` | Loads every module the image does not bind into its own context and commits it only after its contributions materialised. A generation whose contributions throw is unloaded and the serving one stays current. Because a failed generation never takes its name in the default context, the previous generation and the image copy stay reachable for the fallback (#3649, #3735). |
| `NodeAssemblyLoadContext`, the kernel `ScriptSession` | Bind a module through `ModuleContexts.Resolve` — its CURRENT generation. |

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
| `ModuleServiceProvider` (`MeshWeaver.ServiceProvider`) | The module's services live in an Autofac container of their OWN, which reaches the root only for types that do not name the module; a closed generic over a module type (`IOptions<ItsOptions>`, `ILogger<ItsType>`) is closed in the module's container from the root's open-generic registration. |
| Per-node hubs | Every per-node hub's scope gets forwarders for the module-owned types (and their options) of each module's CURRENT generation, so module code resolving its own service from `hub.ServiceProvider` finds it; a swap recycles the hubs. |
| The swap | `ModuleContexts.PrepareServices` re-runs N+1's delegates against the SAME boot prefix and requires the same routes and shape — the root's forwarders were laid out at boot; a changed shape is refused by name and N keeps serving. |

What still blocks root services (measured, named): a delegate that removes or replaces a registration it
did not add, a keyed registration, a class-typed platform service the module implements, an interface
implementation that also implements another platform interface (a proxy would hide it), an open generic
the module implements.

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
| `ModuleEndpointDataSource` (`MeshWeaver.Hosting.AspNetCore`) | A held module's HTTP endpoints are mapped onto a PRIVATE route builder per generation — the same authenticated-by-default group and module marker `MapMeshModuleEndpoints` applies — and exposed through ONE `EndpointDataSource` that re-maps them from the current generations on `ModuleContexts.VersionChanged` and fires its change token, so ASP.NET Core routing rebuilds its matcher. A re-map whose routes collide with another endpoint is NOT published (the previous endpoints keep serving; logged Critical, naming both). Image-bound modules map as before. Endpoints are no longer a blocker. |
| `MeshNodeProviderAttribute.Views` + `IViewContributionSource` (`MeshWeaver.Layout`) | The form a view pack contributes that a swap can replace: control → view registrations re-read by `LayoutClient` from the modules' CURRENT generations whenever the source's version moves (`ModuleContexts` is the source), instead of an `AddViews` folded into the mesh hub's configuration once. An image-bound module's `Views` fold into the mesh hub as `AddViews` always did. `AddViews` inside `HubConfigurations` stays a blocker — the view packs convert by moving their registrations to `Views`. |
| Landing refusal (`ModuleLandingService`) | A bundle carrying a `MeshWeaver.*` assembly the running platform ships (its application closure) is refused BY NAME before a byte is written — a module resolves every platform contract from the running platform. Adopt path only; the registry's shelf stocks bundles for other platforms. |

**Tests:** `ModuleEndpointsSwapLiveTest` (real ASP.NET Core routing on a TestServer): the route answers
from N+1 after the swap with no restart and N is collected; a generation that ADDS endpoints is mapped
live even when boot mapped none; a held module whose boot route collides with the host is refused at
startup; the negative control: a colliding re-map is not published and the previous route keeps serving.
`ModuleViewsSwapTest`: the SAME layout client resolves the control to N+1's view after the swap; the
negative control: views folded through `HubConfigurations`' `AddViews` are a named blocker.
`ModulesUpdateIndependentlyOfThePlatformTest`: the platform stays fixed while M goes N → N+1 → N+2 live
through the real landing path — N+2 recorded against an older platform build and a floor below the
platform, so no identity-equality gate, no seal, no roll. An N+3 whose floor is above the platform is
declined by name by the reconciler's own decision function (`ModuleUpdateDecision`, called directly —
the decline happens BEFORE anything lands, so no above-floor bundle reaches the landing path), and the
next activation pass then takes only the sibling's landed update while M keeps serving N+2; the
reconciler's own wiring of that decision is not exercised here. A bundle carrying a platform assembly is
refused naming it, and the same bundle without it lands.

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
   the new one's, whatever its size.
2. **The content-type registry let go of nothing.** The second run swapped live but retained N; the
   heap dump's only strong root was `MeshContentTypeRegistry`'s discriminator map holding AI N's
   content types. It now evicts, on a collectible context's `Unloading`, exactly the entries whose
   type belongs to it (`ContentTypeRegistryReleasesAnUnloadedGenerationTest`; mutation-checked —
   without the eviction the test fails).
3. **Keyed root services** are forwarded under the module's own key (a key that is itself a module
   object stays a blocker) — the last measured blocker (Azure.Blob's keyed `IStreamProviderFactory`).

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
