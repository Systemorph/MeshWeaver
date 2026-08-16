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

- **Framework-MVID mismatch** — an image roll changed the running framework since the install, so
  the landed bytes are ABI-stale. The entry is SKIPPED with a loud log naming both identities and
  stays in the sidecar, waiting for a re-install against the new framework. The gate is the same
  pure function every prebuilt lane uses (`PrebuiltAssemblySeeder.DeclineReason`) — there is one
  notion of framework identity, never two.
- **Missing DLL** — the entry's `modules/<name>/<name>.dll` does not exist (lost volume, manual
  deletion). Skipped loudly; re-install to heal. The check is that path SPECIFICALLY — a
  same-named DLL in the app closure never satisfies a store-installed entry (the
  `ResolveModulePath` base-directory fallback applies to baseline entries only, so a tampered
  sidecar can never silently bind the platform's own binaries).

The landing service itself gates twice more, at placement: the same MVID check (declined bytes
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
| `MeshWeaver.Blazor.Radzen.dll` | Radzen view pack (charts etc.) | — |
| `MeshWeaver.Blazor.Analysis.dll` | Analysis view pack | — |
| `MeshWeaver.Blazor.GoogleMaps.dll` | Google Maps map provider | `GoogleMaps` |
| `MeshWeaver.ContentCollections.Indexing.PostgreSql.dll` | Content indexing (PG) | gated `enabledWhen` the mesh DB resolves |
| `MeshWeaver.Speech.dll` | Speech transcription | `Speech` |
| `MeshWeaver.Markdown.Export.dll` | Document export (PDF/DOCX/HTML/email) | — |
| `MeshWeaver.Observability.dll` | Red-log ticketing / log watch | `LogWatch` |
| `MeshWeaver.OgCard.dll` | Link-preview (og-card) layout area | — |

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

## Modules and the in-mesh compiler

In-mesh source compiles against the platform's `TRUSTED_PLATFORM_ASSEMBLIES` **plus this mesh's
installed modules**: `InstallAssemblies` records every loaded module as an
`InstalledModuleAssembly` DI singleton, and `MeshNodeCompilationService` composes its reference
set from both — so a module published outside the app closure stays visible to scope classes and
NodeType source that reference it (e.g. a map control). Two boundaries stand:

- **Kernel cells are different.** Executable `--render` cells resolve against a process-wide
  snapshot, and pack/NodeType assemblies are not reliably cell-callable — cell-callable API stays
  in compiled, startup-loaded assemblies until the pack-scripting seam lands.
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
