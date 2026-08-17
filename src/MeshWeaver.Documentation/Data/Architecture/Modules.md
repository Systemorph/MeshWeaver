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
2. **The persisted activation list** — `modules/activation.json`, a sidecar file beside the module
   folders, written by the runtime landing service (`ModuleLandingService`) when a compiled module
   is installed from the Store. Each entry records the module name, its source, the install
   record's mesh path, and the framework MVID the landed assemblies were built against.

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
| `MeshWeaver.Hosting.Grpc.dll` | The mesh gRPC transport: `meshweaver.v1.Mesh` + gRPC-web, `py`/`node` foreign participants AND the React GUI's browser data plane | `Grpc` (`TrustedPort`) |

🚨 **`MeshWeaver.Hosting.Grpc` is DEFAULT-ON in every deployment.** Its endpoint is not just the
foreign-participant (`py/*`, `node/*`) transport — the React GUI connects over the very same
grpc-web `Connect`+`Deliver` split at the origin root (`clients/portal-next`, `clients/portal`).
Delist it only in a deployment with NO React GUI and NO foreign participants; anywhere else a
delist silently breaks the React frontend's live connection. (The former `Features:Grpc` flag is
gone — the module listing is the switch.)

Boot packs select by OTHER configuration too: `Graph:Storage:Type` `Cosmos`/`Snowflake` requires
the matching `MeshWeaver.Hosting.Cosmos`/`.Snowflake` DLL in this list — installation runs before
storage selection, so ordering is safe. Delisting a UI module removes its areas mesh-wide;
embeds of a removed area render the standard area-not-found placeholder (documented per module).

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

The first flipped module is `MeshWeaver.Markdown.Export`: no host references it any more — its
targets entry runs a full closure publish pruned against the app root AND the shared-framework
targeting packs, so its folder carries the engine assembly (measured private deps beyond it:
none; the engine's package closure still rides the app via other references). Because a flipped
DLL exists nowhere else, the closure lane also lays it into a plain build's output
(`bin/…/modules/`), keeping `dotnet run` on a host working without a publish step.

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
[Deployment](/Doc/Architecture/Deployment).
