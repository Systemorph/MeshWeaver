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

## Activating — `Modules:Assemblies`

The deployment's appsettings lists the DLLs to install; the list IS the on/off switch. The
current first-party inventory and each module's configuration section:

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
becomes a publish decision while which ACTIVATE stays `Modules:Assemblies`. Skip the whole
target with `-p:PublishMeshModules=false`.

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
- **The bake fingerprint.** Every successful NodeType compile stamps
  `CompiledModulesHash` — a hash of the sorted installed-module MVIDs
  (`InstalledModulesFingerprint`) — beside `CompiledFrameworkVersion`. While modules ride the
  image the framework MVID already invalidates on every rebuild, so the hash is recorded but not
  yet decisive; it joins the usable-build check when modules ship separately from the image, so a
  module-only update invalidates baked builds that could reference it. Definitions stamped before
  the feature carry `null`, which compares as MATCH — such builds predate modules in the compile
  surface and stay governed by the framework rule.

## Related

UI contributed as data (menus, settings tabs, whole top-bar menus — `UiContribution` nodes) is
[UI Extensibility](/Doc/Architecture/UiExtensibility). Content plugins and their registry are
[Plugins](/Doc/Architecture/Plugins) and [Plugin Packaging](/Doc/Architecture/PluginPackaging).
Deployment surfaces: [Feature Flags](/Doc/Architecture/FeatureFlags) ·
[Deployment](/Doc/Architecture/Deployment).
