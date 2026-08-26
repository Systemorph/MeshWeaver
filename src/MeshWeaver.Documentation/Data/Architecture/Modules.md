---
Name: Modules
Category: Architecture
Description: The module lane end to end — MeshNodeProviderAttribute, the Modules:Assemblies activation list, every module's configuration section, the modules/ publish layout, and the compile-surface + fingerprint story.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3l8 4.5v9L12 21l-8-4.5v-9L12 3z"/><path d="M12 12l8-4.5M12 12v9M12 12L4 7.5"/></svg>
---

A **module** is a compiled MeshWeaver assembly a deployment turns on by LISTING it — no code
change, no recompile of the platform. This page is the operator- and author-facing reference for
the whole lane: how a module declares itself, how a deployment activates and configures it, how
its bits reach the image, and how the in-mesh compiler and bake fingerprint treat it.

## Declaring a module — `MeshNodeProviderAttribute`

A module carries one assembly-level attribute deriving from `MeshNodeProviderAttribute`
(`MeshWeaver.Mesh.Contract`). Its five hooks are the complete boot-time surface:

| Hook | What it contributes |
|---|---|
| `Nodes` | Mesh nodes (node types, seeds) — with `.WithGlobalServiceRegistry` for root DI services |
| `AddressTypes` | Address types for the type registry |
| `HubConfigurations` | The MESH hub's configuration |
| `DefaultNodeHubConfigurations` | Configuration applied to EVERY per-node hub (layout areas, type registrations) |
| `BuilderConfigurations` | The full-surface hook — a `MeshBuilder → MeshBuilder` fold, applied last |

HTTP endpoints ride a SEPARATE assembly attribute — `MeshEndpointProviderAttribute`
(`MeshWeaver.Hosting.AspNetCore`), applied by the host's `app.MapMeshModuleEndpoints()` at
endpoint-mapping time. The split is layering (the mesh contract never references ASP.NET) and
timing (endpoints map after the auth middleware). Every contribution maps inside an
authenticated-by-default group — a route is anonymous only where the module explicitly opts out —
and duplicate (verb, pattern) registrations refuse the app loudly at startup. Delisting the
module removes its routes wholesale: a 404, not a compiled optional-service 503.
`MeshWeaver.Social` is the first consumer — its LinkedIn connect/publish/page-sync routes ride
this hook, with the two OAuth callback routes opting out via `AllowAnonymous` (LinkedIn's
redirect must not bounce through a login challenge; the CSRF state cookie is the guard).
`MeshWeaver.Hosting.Grpc` is the second: the whole `meshweaver.v1.Mesh` service maps through the
hook, `AllowAnonymous` on every route because the transport authenticates each connection itself
(Bearer API token in gRPC call metadata, or the trusted loopback port). One piece cannot ride the
hook: the gRPC-web MIDDLEWARE must run between `UseRouting` and the endpoint maps, so the host
keeps a single compiled `UseMeshWeaverGrpcWebWhenInstalled()` line that self-gates on the module
being listed — the module listing stays the only switch.

### Which routes ride the module, and which stay in the host

Not every route belonging to a module's feature belongs *in* the module. The dividing question is
**whose API is it**:

- **The module's OWN protocol surface rides the module.** LinkedIn's OAuth callbacks and the
  `meshweaver.v1.Mesh` gRPC service exist only because that module exists; nobody calls them when
  it is delisted, and a 404 is the honest answer. They also carry their own auth story
  (`AllowAnonymous` plus a CSRF cookie; per-connection Bearer metadata), so nothing is left behind
  in the host.
- **The PORTAL's client API stays in the host, behind a 503 seam** — even when the engine it calls
  ships as a module. `POST /api/log-incidents` (Observability) and `POST /api/speech/transcribe`
  (Speech) are both this shape: the route is part of the portal's REST surface that clients are
  configured against, its access rule is the HOST's to state, and it resolves the module's service
  **optionally**, answering an actionable 503 that names the missing module rather than a 500 or a
  bare 404. Note the two state that rule differently — speech requires the portal's Bearer-only
  `McpAuth` policy, while log-incidents is `AllowAnonymous` at the ASP.NET layer and gates on the
  `LogWatch:IngestToken` shared secret (its caller is a cluster service, not a signed-in user), and
  is not mapped at all when that token is unset. What makes them the same case is not a shared
  policy but a shared owner: the host decides who may call, and the module only supplies the engine.

Two things go wrong when a portal-API route is pushed onto the hook. The caller loses the
diagnosis — "the module is not listed" becomes an indistinguishable 404 — and, more sharply, the
route loses the host's authorization policy. The module hook's group applies the **default**
policy; a route that needs a specific one (the portal's Bearer-only `McpAuth`, whose challenge
forwarding is what makes an unauthenticated API call answer `401 + WWW-Authenticate` instead of
`302` to an HTML login) would have to name that policy by string across the assembly boundary,
which throws at request time in any host that never registered it. Both failures pass CI and
surface as "the mobile app logs me out".

Module DI options bind through the options pipeline —
`services.AddOptions<T>().BindConfiguration("Section")` — never `services.Configure(section)`:
there is no `IConfiguration` instance at install time. A module whose activation depends on
runtime facts guards itself with a resolve-time `enabledWhen` gate (the PostgreSQL indexing
module registers its provider `enabledWhen` the mesh database connection resolves) instead of
failing at boot.

Modules that also need explicit composition (test fixtures, bespoke hosts) expose ONE
`Add<Name>()` extension sharing the same internal configure path as the attribute — the two lanes
must never drift (`OgCardExtensions` is the reference shape).

## Activating — the appsettings baseline ∪ persisted store installs

A deployment's active module set is the union of two lanes, computed at boot (before the DI
container builds) and fed to `MeshBuilder.InstallAssemblies` as one list:

1. **The `Modules:Assemblies` appsettings baseline** — the DLLs the image ships with; the list is
   the operator's on/off switch for first-party packs, exactly as before. A baseline entry that
   fails to load fails loudly at startup, never silently.
2. **The persisted activation record** — one file per module under `modules/activation.d/`, written
   by the runtime landing service (`ModuleLandingService`) when a compiled module is installed from
   the Store. Each entry records the module name, its source, the install record's mesh path, its
   generation directory, its declared platform floor, and the framework MVID the landed assemblies
   were built against. The legacy aggregate `modules/activation.json` is still READ (deployments
   already carry one) and a per-module file wins over it by name; nothing writes it any more.

   > 🚨 **Why one file per module and not one index.** Every portal replica mounts the same RWX
   > `/data`, and a republish after a release pushes 30+ modules concurrently. A single mutable
   > index that each landing read, appended to and renamed over has two failure modes no retry
   > fixes: concurrent landings of *different* modules **lose each other's entries** (last writer
   > wins the whole list), and the rename **contends for the file's SMB lease** with every other
   > reader and writer of that one path — `Access to the path '/data/modules/activation.json' is
   > denied` on the write side (HTTP 409), and a `FileNotFoundException` on the read side from
   > opening into the replace window, which the reader then reported as a corrupt sidecar and
   > **booted the pod with no store modules at all**. Sharding by module removes the shared cell:
   > two writers of different modules share no path, so neither outcome is possible. The
   > restart-required flag is a marker FILE (`activation.d/.pending-restart`) for the same reason —
   > setting it is a create and clearing it is a delete, never a read-modify-write. And a record
   > that cannot be read now costs exactly that one module, reported by name, instead of collapsing
   > the whole answer to the empty list.

The union dedupes by module name (a store install of an already-baseline module contributes
nothing). **Activation is restart-based**: landing a module writes its assemblies into
`modules/<name>/` and its activation entry, flags `PendingRestart` in the sidecar, and the module
loads on the NEXT restart — nothing is loaded into the running process (a genuinely dynamic
loader collides with the kernel snapshot). Boot consumes the `PendingRestart` flag: applying the
list IS the restart. Uninstall is the mirror: the entry is disabled (kept, for history), the
folder is deleted, and the change likewise takes effect at restart.

**The skip rules** (persisted entries only — the deployment must always boot):

- **Unsatisfied platform floor** — the running platform no longer satisfies the module's declared
  `minMeshVersion` (a rollback below its requirement). The entry is SKIPPED with a loud log
  naming both versions and stays in the sidecar, waiting for the platform to move forward again.
  The gate is `ModulePlatformFloor.DeclineReason` — the ONE notion of the module platform
  requirement, shared with landing and serving. Deliberately a **semver floor, never MVID
  equality**: a module is a plain assembly binding by simple name, so a landed module keeps
  loading across ordinary platform updates; the MVID it was built with is recorded on the entry
  as diagnostics only (MVID equality is bake semantics and belongs to the NodeType assembly
  lane).
- **Missing DLL** — the entry's `modules/<name>/<name>.dll` does not exist (lost volume, manual
  deletion). Skipped loudly; re-install to heal. The check is that path SPECIFICALLY — a
  same-named DLL in the app closure never satisfies a store-installed entry (the
  `ResolveModulePath` base-directory fallback applies to baseline entries only, so a tampered
  sidecar can never silently bind the platform's own binaries).

The landing service itself gates twice more, at placement: the same floor check (declined bytes
never reach disk), and a refusal of any module whose entry DLL name collides with an app-closure
assembly — `ResolveModulePath` probes `modules/<name>/` first, so such a module would silently
shadow the platform's own binary at the next boot.

### 🚨 An ACTIVATED entry with no bytes — the boot-GC race (#2303)

The "Missing DLL" skip above is the SYMPTOM; #2303 traced one concrete way an entry ends up
pointing at nothing: a race between `ModuleLandingService.CollectGarbage` (run at every pod's boot,
before its own module set is computed) and a landing happening on a DIFFERENT replica at the same
moment.

A landing is two writes on the shared `/data` volume, deliberately ordered bytes-then-entry: it
`Directory.Move`s the new generation into place, THEN writes the sidecar entry that names it
(`LandCore`). Those two writes are adjacent in one synchronous call on the landing replica, but
nothing serializes them against a GC pass on ANOTHER replica — the per-module sidecar file and the
landing service's IO pool both bound a single process, not a cross-process sequence. If a GC pass
reads the sidecar in the gap between the other replica's two writes, the new generation directory
is on disk but no entry references it YET — indistinguishable from a genuinely orphaned directory —
and GC deletes it a moment before the landing's `WriteEntry` lands, pointing a real, enabled
activation entry at bytes that no longer exist. Nothing throws anywhere: the landing that raced GC
reports success (both of ITS writes succeeded), and the entry only reveals itself as unresolvable
the next time something reads it — `ModuleActivationStatus.Unresolvable`'s loud startup report and
`Degraded` health check (#2093), or a boot that silently skips the module via the "Missing DLL" rule
above. That is the exact shape #2303 reported for `MeshWeaver.Blazor.EntityViews`: an ACTIVATED
entry whose landed assembly was gone, with no exception or stack frame naming why — likeliest to
fire during a rolling restart landing (or auto-updating) a module while sibling pods are cycling
through boot at the same time.

The fix cannot be a lock — replica coordination here is deliberately structural, not a gate. Instead
`CollectGarbage` carries a grace period (`ModuleLandingService.DefaultGarbageMinAge`, 5 minutes):
an unreferenced generation (or `.staging-`/`.pending-` leftover) younger than the window is left for
a LATER pass rather than reclaimed immediately. A directory that survives the window and is STILL
unreferenced is a genuine orphan and is collected exactly as before — the grace period defers
reclamation, it does not disable it. The two writes of a real landing are back-to-back with no I/O
between them, so the actual exposure the window has to cover is low-single-digit seconds even over
a slow network volume; five minutes is generous headroom on top of that.

Why a sidecar file and not a mesh node: the list is consumed before any storage provider, hub, or
connection string exists, and it must move with the DLLs it describes — the landing service writes
both in one operation onto the same volume, so they cannot drift apart.

The current first-party inventory and each module's configuration section:

| Module DLL | Concern | Configuration |
|---|---|---|
| `MeshWeaver.AI.OpenAI.dll` | OpenAI-compatible model providers | `OpenAI`, `OpenAICompatible:Models` |
| `MeshWeaver.AI.AzureFoundry.dll` | Azure Foundry + Anthropic-on-Azure providers | `AzureFoundry`, `Anthropic` |
| `MeshWeaver.AI.ClaudeCode.dll` | Claude Code harness | `ClaudeCode` |
| `MeshWeaver.AI.Copilot.dll` | Copilot harness | `Copilot` |
| `MeshWeaver.AI.WebSearch.dll` | Agent web-search tools (`SearchWeb`, `FetchWebPage`, feed readers) | `WebSearch` (self-gates on credentials) |
| `MeshWeaver.Blazor.Radzen.dll` | Radzen view pack (charts etc.) | — |
| `MeshWeaver.Blazor.Analysis.dll` | Analysis view pack | — |
| `MeshWeaver.Blazor.GoogleMaps.dll` | Google Maps map provider | `GoogleMaps` |
| `MeshWeaver.ContentCollections.Indexing.PostgreSql.dll` | Content indexing (PG) | gated `enabledWhen` the mesh DB resolves |
| `MeshWeaver.Speech.dll` | Speech transcription | `Speech` |
| `MeshWeaver.Markdown.Export.dll` | Document export (PDF/DOCX/HTML/email) | — |
| `MeshWeaver.Observability.dll` | Red-log ticketing / log watch | `LogWatch` |
| `MeshWeaver.OgCard.dll` | Link-preview (og-card) layout area | — |
| `MeshWeaver.Notifications.Channels.dll` | Notification delivery channels (rule/channel node types + AI triage escalation) | `Email` (triage self-skips unless `Email:Enabled`) |
| `MeshWeaver.Social.dll` | LinkedIn publishing: connect/publish/page-sync endpoints + node-menu actions | `Social:LinkedIn` |
| `MeshWeaver.Teams.dll` | Microsoft Teams bot channel: messaging endpoint, inbound routing into threads, proactive replies | `Teams` (inert until bot credentials set) |
| `MeshWeaver.SelfUpdate.Aks.dll` | AKS/ACR mechanics: ACR tag reads, Kubernetes deployment patching, cluster instance provisioning (the self-update POLLER stays in the platform) | `SelfUpdate`, `Instances` |
| `MeshWeaver.Courses.dll` | Course delivery: the entitlement-gated `/assets/{Space}/…` route over a Space's synced repo | `GitHub:App:*` (shared with GitSync) |
| `MeshWeaver.Mail.MicrosoftGraph.dll` | Mail over Microsoft Graph: system email, inbound intake + its webhook, the Executive Assistant's mailbox tools | `Email` (`Enabled`, `InboundEnabled`) |
| `MeshWeaver.Import.dll` | Tabular import: Excel/CSV readers (its private `MeshWeaver.DataSetReader.*` closure), mapping configuration, the `ImportRequest` handler | — (🚨 list it FIRST — see below) |
| `MeshWeaver.Mcp.dll` | The Model Context Protocol server: the mesh tool surface + the `/mcp` HTTP transport | `Mcp` (`BaseUrl`; the `McpAuth` policy stays platform-side) |
| `MeshWeaver.Hosting.Grpc.dll` | The mesh gRPC transport: `meshweaver.v1.Mesh` + gRPC-web, `py`/`node` foreign participants AND the React GUI's browser data plane | `Grpc` (`TrustedPort`) |
| `MeshWeaver.Hosting.Cosmos.dll` | Cosmos DB storage backend (keyed adapter factory + native query) | selected by `Graph:Storage:Type` = `Cosmos` |
| `MeshWeaver.Hosting.Snowflake.dll` | Snowflake storage backend (persistence, change feed, cross-schema query, access projection) | selected by `Graph:Storage:Type` = `Snowflake` |

🚨 **`MeshWeaver.Hosting.Grpc` is DEFAULT-ON in every deployment.** Its endpoint is not just the
foreign-participant (`py/*`, `node/*`) transport — the React GUI connects over the very same
grpc-web `Connect`+`Deliver` split at the origin root (`clients/portal-next`, `clients/portal`).
Delist it only in a deployment with NO React GUI and NO foreign participants; anywhere else a
delist silently breaks the React frontend's live connection. (The former `Features:Grpc` flag is
gone — the module listing is the switch.)

🚨 **`MeshWeaver.Import` is listed FIRST, and a module that registers nothing is still doing work.**
No host ever called `AddImport()` — `AddImport(...)` is an application-level call a data source
makes for itself, and the portals referenced the assembly for exactly one reason: so that **in-mesh
source could `using MeshWeaver.Import`**. NodeType sources compile against
`TRUSTED_PLATFORM_ASSEMBLIES` **composed with the deployment's installed modules**
(`CompileReferences.ComposeWithModules`), and `MeshBuilder.InstallAssemblies` records an
`InstalledModuleAssembly` for **every** listed DLL — attribute or not — so listing it is what keeps
that compile surface. Because the reference set is composed in list order, a module whose own
content compiles against `MeshWeaver.Import` must be listed **after** it.

Note what a module contributes to that surface: **its entry assembly, not its private closure.** A
module's own dependencies (here the six `MeshWeaver.DataSetReader.*` assemblies, plus
`MeshWeaver.DataStructures` and `CsvHelper`) resolve at
RUNTIME from the module folder, but they are not metadata references — so in-mesh code may use the
module's public types freely, and would need the platform to carry any *other* assembly whose types
appear in those signatures. Keep a module's in-mesh-facing surface self-contained.

Boot packs select by OTHER configuration too: `Graph:Storage:Type` `Cosmos`/`Snowflake` requires
the matching `MeshWeaver.Hosting.Cosmos`/`.Snowflake` DLL in this list — installation runs before
storage selection, so ordering is safe. Delisting a UI module removes its areas mesh-wide;
embeds of a removed area render the standard area-not-found placeholder (documented per module).

Both storage backends **ship in the image but are listed by nobody** — every memex portal runs
PostgreSQL — so selecting one is purely an appsettings edit in the deployment that wants it.
They ride the closure lane rather than the Store bundle lane on purpose: persistence selection
reads `Graph:Storage` during boot, so a storage backend cannot be something the mesh installs
for itself once it is already running. The bits cost ~25 MB of publish output (Cosmos ~15 MB with
the Direct/ServiceInterop client, Snowflake ~10 MB — its driver carries Arrow plus the AWS and
GCS SDKs for stage transfer); `-p:PublishMeshModules=false` skips the whole layout for a host
that wants none of it.

Being **bootstrap tier** — the mesh cannot read itself without a storage backend, so the Store's
catalog lives behind the very storage an install would be delivering — is also what leaves these
two with no compiled reference anywhere in the tree, and therefore nothing that would notice their
folder going wrong. `StorageModuleLayoutTest` (`test/Memex.Portal.Shared.Test`) is that gate: it
walks the seam a portal walks and asserts nothing more — `ResolveModulePath` lands inside
`modules/<Name>/` rather than on its app-folder fallback, the private driver survived the prune and
loads, `InstallAssemblies` folds the assembly's `MeshNodeProviderAttribute`, and the keyed
`IStorageAdapterFactory` that `Graph:Storage:Type` resolves comes from THAT DLL. No emulator, no
endpoint, ~40 ms. It closes two blind spots at once: the compiler proves the SOURCE binds but says
nothing about the publish layout, and the emulator suites green-SKIP when their backend is
unreachable, so they can pass by not running. The same test is what a released binary would have to
satisfy if these backends ever moved out of the platform repo (#1752) — point it at the pinned bytes
instead of the in-tree build and it answers the question a moved backend raises.

Entries resolve through `MeshBuilder.ResolveModulePath`: a rooted path passes through; a bare
DLL name probes **`modules/<name>/<name>.dll`** beside the app first (the publish layout below),
then falls back to the app folder.

## The `modules/` publish layout (#1644)

Both hosts import `memex/MeshModulesPublish.targets`: publishing lays every listed module out
under `modules/<Name>/` beside the app, pruning same-identity files the app output already
carries. While a module still ALSO rides a `ProjectReference` (the transition state), its folder
prunes to empty and the loader falls back to the app folder — byte-for-byte the classic image.
Flipping a module's reference off (one module at a time, its entry upgraded to a closure layout
correct for that module) is what makes the folder carry real content; which modules EXIST then
becomes a publish (or Store-install) decision while which ACTIVATE stays the boot union above.
Skip the whole target with `-p:PublishMeshModules=false`.

`-p:MeshModulesClosureSubset=<Name>;<Name>` narrows the closure lane to the named modules, so a
project that is not a host can lay out a couple of them into its own `bin/` — today only
`Memex.Portal.Shared.Test`, so `StorageModuleLayoutTest` loads the real layout rather than a copy
of it. 🚨 A host must never pass it: `-p:` is global to every project in the build. A subset naming
nothing fails the lane RED instead of laying out nothing and reporting success.

The first flipped module is `MeshWeaver.Markdown.Export`: no host references it any more — its
targets entry runs a full closure publish pruned against the app root AND the shared-framework
targeting packs, so its folder carries the engine assembly (measured private deps beyond it:
none; the engine's package closure still rides the app via other references). Because a flipped
DLL exists nowhere else, the closure lane also lays it into a plain build's output
(`bin/…/modules/`), keeping `dotnet run` on a host working without a publish step.

### 🚨 Which COPY loaded — the boot report (#2223)

Two `modules/` trees are legitimate at once: the image publishes baseline packs beside the app, and
a store install LANDS its bytes as a fresh generation under the deployment's writable, pod-shared
root (`modules/<Name>@<id>/`). So "the pack" is not a place — and until this report existed nothing
said which of them a running portal had actually loaded.

Measured on memex-cloud 2026-08-25: the portal ran an image built from the fix's own merge commit,
the store held **two** newer copies of `MeshWeaver.Blazor.Views` that both contained the fix, and
`/proc/1/maps` showed the process had mapped the **image** copy — which did not. Every lane was
green. The mechanism is not a bug in any single step:

1. a **baseline** `Modules:Assemblies` entry resolves through `MeshBuilder.ResolveModulePath`, whose
   probes are landed root → image → app closure;
2. the landed probe looks in the fixed `modules/<Name>/`, which generation landing never writes, so
   it misses and the image copy wins;
3. the sidecar entry that *would* have named the generation is deduped away by name, silently,
   because the baseline already claimed it (`ComputeEffectiveModuleEntries`).

`ModuleLoadReport` (`src/MeshWeaver.PluginCatalog/ModuleLoadReport.cs`) makes that visible. At boot,
immediately before `InstallAssemblies`, it emits one `[ModuleLoad]` line per pack — name, source
(`appsettings` / `store`), the **exact path being loaded**, its MVID and its last-write time — and a
`STALE PACK` warning when the store holds a copy of the same module that is both **newer** and
carries a **different MVID**. Two copies with the same MVID are the same bytes in two places and
warn nothing, or the line would be noise.

It reports the array it is HANDED, so the line and the load cannot disagree; the acceptance is
literally that the path in `/proc/1/maps` equals the path the line named:

```bash
kubectl exec -n <ns> <pod> -c memex-portal -- sh -c \
  'cat /proc/1/maps | grep -o "[^ ]*Blazor.Views.dll" | sort -u'
kubectl logs -n <ns> <pod> -c memex-portal | grep '\[ModuleLoad\]'
```

🚨 **It warns; it never refuses to start.** Which copy *ought* to win is an open policy question, and
a pod that dies on the answer cannot be given the module that fixes it — the same deadlock as a
registry that cannot start delivering the module breaking it. The remedy the warning names is a
deployment decision: delist the pack from `Modules:Assemblies` so the landed generation stops being
shadowed.

### Native assets — `runtimes/<rid>/native/` (#1728)

A module is loaded with `Assembly.LoadFrom`, which never consults the module's own `deps.json`, so
the runtime's fallback probe is the module's FLAT folder and nothing else. That is why the closure
lane's first prune used to delete `runtimes/` outright — and why a module could not ship a native
library at all.

It can now. The publish keeps `runtimes/<rid>/native/**` (dropping the managed `runtimes/<rid>/lib`
trees, which genuinely need the deps.json, and `.a`/`.lib` link-time artifacts, which nothing can
open), and the host resolves them at load time: `ModuleNativeAssets` subscribes
`AssemblyLoadContext.Default.ResolvingUnmanagedDll`, derives the module folder from the REQUESTING
assembly's own location — so a dependency such as `SkiaSharp.dll`, which declares the P/Invokes
rather than the module assembly, resolves too — and probes
`modules/<Name>/runtimes/<current-rid>/native/`, then the flat folder.

Resolution rather than placement, because every module MSBuild invocation strips RID globals by
design (#1675/#1676): a module publish is always portable, so the RID is unknown when the bits are
laid out and only the host knows its own. The RID probe is the running RID plus its portable form
(`osx.14-arm64` → `osx-arm64`); it deliberately does NOT walk a wider graph, because
`linux-musl-x64` and `linux-x64` are different C libraries and loading one for the other crashes
instead of failing cleanly.

Two modules already needed this: Snowflake P/Invokes `libsf_mini_core.*` (and Mono.Unix), and
Cosmos' query-plan `ServiceInterop` is native. Both were shipping with those files pruned away.

## The bundle lane — modules as Store packages (#1664)

A compiled module reaches a deployment one of two ways: shipped in the image (the baseline above),
or **installed from the Store as part of an ordinary package**. The second rides the plugin bundle
transport end to end — there is deliberately no second distribution channel:

1. **Declare** — the package's root `index.json` carries `content.module` naming the module's
   entry-assembly (`"module": "MeshWeaver.Social"`), plus the platform floor it requires in the
   `content.minMeshVersion` field authors already write. The listing reads both onto the catalog
   entry (`PackageManifest.Module` / `.MinMeshVersion`) and the ordinary install-record stamp
   carries them onto the record. A package with content nodes AND a module is one Store product —
   card, price, install funnel, pre-install eligibility all unchanged.
2. **Build** — `MeshWeaver.Plugin.Build`'s `module-pack` mode packs a built module's closure into
   a bundle recording the `minMeshVersion` floor (`--min-mesh-version`) and, as diagnostics, the
   MVID of the identity anchor (`MeshWeaver.Compiler.dll`, #1707) in the build output. It is a
   plain dotnet invocation over
   an output folder, so ANY node repo's CI can drive it — SocialMedia builds its own module
   bundle the same way the platform repo does — and because the gate is the floor, ONE bundle
   serves every compatible platform build: nothing is rebundled per CI build. The closure is an
   explicit statement (`--with`), never a folder scrape: a publish output contains the whole app
   closure, and bundling framework assemblies would shadow the platform at the consumer.
3. **Serve** — the registry portal's `/api/plugins/bundles` serves the module section inside the
   SAME bundle that carries the package's NodeType assemblies (`meshweaver/modules/` beside
   `meshweaver/assemblies/`, one manifest naming both). The registry serves a module's bytes from
   its own `modules/<name>/` tree — the very bytes it loads and runs — and refuses to serve a
   landing its own boot would skip (uninstalled, or a floor the registry's own platform no longer
   satisfies). The index stamps each bundle's `module` (and its floor) only when the bytes are
   actually servable, so a consumer never downloads for a section that will not be there. Same
   instance-key auth, fail-closed.
4. **Land** — on install (and on update), a consumer whose package declares a module fetches the
   bundle, verifies the **platform floor** (`ModulePlatformFloor.DeclineReason` — the one notion
   of the module platform requirement, checked at the index, at the manifest, and again at
   placement), and lands it through `ModuleLandingService` into `modules/<name>/` with its
   activation entry (version + floor recorded; the built-against MVID recorded as diagnostics).
   Deliberately **not** MVID equality — that is bake semantics, the NodeType lane's gate: a
   module binds by simple name, so a bundle built against an older platform installs ex post on
   any deployment satisfying its floor. Restart-as-activation as above: `PendingRestart` is the
   signal, the next restart loads it.

### Auto-update

Store-installed modules **update themselves by default**. The boot reconcile
(`RegistryUpdateReconciler`) runs a module pass after the content pass: for every installed
module-declaring package it consults the registry's bundle index and applies the one pure decision
(`ModuleUpdateDecision`) — a newer version whose **floor this platform satisfies** lands via
`ModuleLandingService` and flags `PendingRestart`; the same served version is skipped without a
download; a bundle whose floor **exceeds** the running platform is skipped silently-with-log (it
becomes installable once the platform has updated, and the same reconcile lands it then). Nothing
is ever rolled back unattended — and an ordinary platform update needs no module re-land at all:
landed modules keep loading across platform builds.

The policy gate is the deployment's **existing update policy — `Admin/UpdatePolicy`**, the same
single surface that governs the platform image roll; there is no module-specific knob.
**Continuous — the platform default, and what an absent policy reads as — lands unattended;
Stable and None decline the UPGRADE** (the catalog's manual Update still works there): a
deployment that pins its image takes updates deliberately, and its modules do not run ahead of
that choice. A **first landing** is deliberately policy-exempt: it completes an install the
operator's own surfaces already sanctioned, and gating it would ship a package whose binary half
never arrives. The wiring is `IModuleUpdatePolicy` (`MeshWeaver.PluginCatalog`), implemented by
the memex portals over the policy node; a host that registers no implementation gets the default
(allowed).

## Modules and the in-mesh compiler

In-mesh source compiles against the platform's `TRUSTED_PLATFORM_ASSEMBLIES` **plus this mesh's
installed modules**: `InstallAssemblies` records every loaded module as an
`InstalledModuleAssembly` DI singleton, and `MeshNodeCompilationService` composes its reference
set from both — so a module published outside the app closure stays visible to scope classes and
NodeType source that reference it (e.g. a map control). Two boundaries stand:

- **Kernel cells — the pack-scripting seam (#1649).** Executable `--render` cells compose their
  reference set per SESSION, not from the frozen process snapshot alone: every installed module
  joins automatically (`MeshScriptEnvironment.SessionAssemblies` enumerates the
  `InstalledModuleAssembly` registrations — modules are Default-ALC file-backed, so the runtime
  bind is free), and a dynamic NodeType joins by DECLARING it — `cellSurface: true` in its
  definition (the pack's `index.json`). At session init the kernel resolves each cell-surface
  type's CURRENT baked assembly through the assembly store + compilation cache, references its
  PE, and binds its collectible load context by name — scoped to the session's declared set,
  never a blanket hook. Assemblies in collectible load contexts never enter the frozen snapshot,
  so the cell surface is a declaration, not a load-order lottery. Two rules follow:
  a `cellSurface` NodeType's `Source/` is **single-home** — any other NodeType that
  `shared=`-consumes it fails its compile with a message naming the owner (the CS0433
  duplicate-type class, prevented by construction); and a live session **pins** the generation it
  bound — sessions are short-lived, and a recompile mid-session keeps old sessions on the old
  generation while new sessions bind the new one (the same semantics live layout areas have).
- **The bake fingerprint is DECISIVE.** Every successful NodeType compile stamps
  `CompiledModulesHash` — a hash of the sorted installed-module MVIDs
  (`InstalledModulesFingerprint`) — beside `CompiledFrameworkVersion`, and the usable-build check
  (`HasUsableBuild`) invalidates a build stamped with a DIFFERENT non-null hash than the live set,
  while its rebuild-kickoff twin (`HasStaleFrameworkBuild`) re-drives the compile for it. That is
  what makes a module-only update safe: a store install lands new module MVIDs without changing
  the framework MVID, and baked builds that could reference the replaced module rebuild on the
  next boot instead of throwing `MissingMethodException` at activation. Definitions stamped before
  the feature carry `null`, which compares as MATCH — such builds predate modules in the compile
  surface and stay governed by the framework rule; call sites without a mesh in scope pass no hash
  and likewise keep the framework-only behavior.

## Related

UI contributed as data (menus, settings tabs, whole top-bar menus — `UiContribution` nodes) is
[UI Extensibility](/Doc/Architecture/UiExtensibility). Content plugins and their registry are
[Plugins](/Doc/Architecture/Plugins) and [Plugin Packaging](/Doc/Architecture/PluginPackaging).
Deployment surfaces: [Feature Flags](/Doc/Architecture/FeatureFlags) ·
[Environment Composition](/Doc/Architecture/EnvironmentComposition) ·
[Deployment](/Doc/Architecture/Deployment).

**Modules and composition are different axes, deliberately.** Which compiled ASSEMBLIES a deployment
loads is `Modules:Assemblies` (plus the persisted store installs above) — decided before the DI
container exists, so it cannot be a mesh-level decision. Which CONTENT PACKAGES an environment
carries is [Environment Composition](/Doc/Architecture/EnvironmentComposition)'s `Features:Flags:*`,
reconciled by the boot install pass. A Store package that carries a module rides both: its content
lands through the composition lane, its assemblies through the bundle lane above.
