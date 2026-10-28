---
nodeType: Markdown
name: Core Carries No GUI Technology
category: Architecture
description: The platform repository names no GUI technology — no Blazor, Razor components, FluentUI, Radzen, Monaco wrapper or browser automation. What that rule covers and what it does not, the measured inventory, what moved to MeshWeaver.Plugins (the GUI package pins, the Playwright suites), the order the two halves land in, and how the build tooling that named a GUI convention moved there.
icon: /static/NodeTypeIcons/code.svg
---

# Core Carries No GUI Technology

**The rule** (policy [`core-carries-no-gui-technology`](../PolicyNotProse)): nothing in the platform
repository — no project, no package pin, no test, no tool — names a GUI technology. Blazor, Razor
components, FluentUI, Radzen, the BlazorMonaco wrapper and browser automation of the portal GUI all
live in **MeshWeaver.Plugins**, beside the portal hosts that use them.

## What the rule covers, and what it does not

| Covered — must not be in core | Not covered — stays in core |
|---|---|
| a `PackageVersion` pin for a GUI package (`BlazorMonaco`, `Microsoft.FluentUI.AspNetCore.Components*`, `Microsoft.AspNetCore.Components.Web`, `Radzen.Blazor`) | the **renderer-neutral control model**: the `UiControl` records in `MeshWeaver.Layout`. They describe *what* to render; a Blazor, React or React Native renderer decides *how*. That is the abstraction the rule protects, not a GUI reference |
| a project on `Microsoft.NET.Sdk.Razor`, `<SupportedPlatform Include="browser" />`, a `.razor` / `.cshtml` file | `FrameworkReference Microsoft.AspNetCore.App` — ASP.NET Core is the HTTP host, not a GUI |
| a test or tool that drives the portal GUI in a browser (Playwright) | the headless Chromium the **PDF export** prints with (CSS Paged Media). It is a document renderer on the server, not a user interface, and it is what the CI shards provision in `dotnet-test.yml` |
| | prose that NAMES Blazor: a comment explaining why the chart keeps sticky sessions for Blazor circuits, a doc page, a script's self-test fixture that spells `MeshWeaver.Blazor` as test data |

The test is: *would this line still be needed if the portal had no Blazor front end at all?* A pin
for FluentUI would not; a `UiControl` record would.

## Why the pins were here at all

No core project referenced any of the GUI packages. They were pinned in core's
`Directory.Packages.props` only because that file was **MeshWeaver.Plugins' only version source**:
Plugins' `src/Directory.Packages.props` imports the platform's list (source build) or the image's
`platform-refs.props` (container build, `scripts/container-refs.py`), and it forbade a list of its
own. That made core's build blind to a pin it carried — the clearest case being BlazorMonaco, a
**security pin** (its bundled Monaco inlines a DOMPurify below the scanner floor, MeshWeaver#3378):
a bump in core changed what the portal's editor was built from while core's CI was green by
construction. A guard (`BlazorMonacoPinGuard`) existed only to cover that blind spot.

## What moved, and how

### 1. The GUI package pins → `src/Directory.Packages.Plugins.props` (MeshWeaver.Plugins)

Plugins now owns the version of every package the platform does not use. The file is imported
**last**, in both build modes, and every entry is **fill-in only**:

```xml
<PackageVersion Include="BlazorMonaco" Version="3.5.0"
                Condition="'@(PackageVersion->AnyHaveMetadataValue(`Identity`, `BlazorMonaco`))' != 'true'" />
```

- **Container build** — the image's `.deps.json` stays authoritative for what shipped (the image is
  built from Plugins' hosts, so the shipped version *is* this pin). The shared-framework package
  `Microsoft.AspNetCore.Components.Web` is trimmed out of `.deps.json`, so this file is what supplies
  its version there.
- **Source build** — while a platform checkout still pins an id, the platform's entry stands and this
  one is skipped. That is what lets the Plugins half land first: one id is never declared twice
  (`NU1506` under warnings-as-errors). The comparison is case-insensitive, as NuGet ids are.

Moved: `BlazorMonaco`, `Microsoft.FluentUI.AspNetCore.Components`,
`Microsoft.FluentUI.AspNetCore.Components.Icons`, `Microsoft.AspNetCore.Components.Web`,
`Microsoft.Playwright`. `BlazorMonacoPinGuard` moved with its pin to Plugins'
`src/Memex.Portal.Gui.Test`, where a bump is now built and tested by the pull request that makes it.
Plugins' template generator (`tools/generate-memex-template.cs`) reads the same file, fill-in only.

### 2. The browser suites → MeshWeaver.Plugins `src/`

| Was in core | Now in MeshWeaver.Plugins |
|---|---|
| `test/MeshWeaver.Portal.E2E.Test` (Playwright, portal GUI) | `src/MeshWeaver.Portal.E2E.Test` |
| `tools/MeshWeaver.ThumbnailGenerator` (Playwright screenshots of layout areas) | `src/MeshWeaver.ThumbnailGenerator` |
| `test/MeshWeaver.ThumbnailGenerator.Test` | `src/MeshWeaver.ThumbnailGenerator.Test` |

Both suites are registered in Plugins' `.github/compiled-validation.json` (no CI workflow grew).
`E2E_LAUNCH=1` launches `src/Memex.Portal.Monolith` from the Plugins checkout — in core it still
pointed at `memex/Memex.Portal.Monolith`, which had already left. Each project removes the platform
test props' global `using MeshWeaver.Messaging` in its own body, so core's `test/Directory.Build.props`
no longer names them. `memex-local e2e test` resolves the Plugins checkout the same way the portal
build does (`MEMEX_PLUGINS_REPO`, default beside the core checkout).

### 3. `Memex.Portal.Shared` (Razor SDK, `SupportedPlatform browser`, `Microsoft.Identity.Web.UI`)

Already handled by the core-slimming source move: Plugins carries `src/Memex.Portal.Shared`
(MeshWeaver.Plugins#4146) and core's copy is deleted (MeshWeaver#7125). It is not changed here.

### Landing order

The Plugins half is an ADDITION and lands first; the core half DELETES and lands last
([Cross-Repo Pair Gate](../CrossRepoPairGate)):

1. **Plugins** (MeshWeaver.Plugins#4202) — adds `Directory.Packages.Plugins.props` and its import, the moved projects, their
   registration, and the moved pin guard. Against today's core it changes nothing (every pin is
   still declared by the platform, so every entry is skipped).
2. **Core** — removes the five pins (declared under `Satellite-pins:`), the three projects, the
   E2E/thumbnail entries in `MeshWeaver.slnx`, `shard-assign.sh`, `test/Directory.Build.props` and
   `HandWovenGateRatchetGuard`, and `BlazorMonacoPinGuard`. Its body carries
   `Pairs-with: Systemorph/MeshWeaver.Plugins#4202`.

🚨 **No gate enforces this order for a pin removal.** The cross-repo pair gate reads `Pairs-with:`
only when a public type or member leaves `src/` (it passes before reading the declaration
otherwise), and the satellite-pins gate checks that each removed id is DECLARED, not that a
counterpart has merged. So the core half is armed by hand, and only once the Plugins counterpart is
merged into Plugins `main`; landing it first leaves every Plugins Blazor project without a version
(NU1010) at its next platform move.

No satellite other than Plugins references any of the moved packages (measured on `origin/main` of
Education, Reinsurance, SocialMedia, Manufacturing and Crm). Memex has its own
`Directory.Packages.props` and is unaffected.

## Inventory — what still names a GUI after this change

| Where | What | Status |
|---|---|---|
| `Directory.Packages.props` | `Radzen.Blazor` | **moved** — MeshWeaver.Plugins pins it beside its own module-library shelf (below) |
| `Directory.Packages.props` | `Microsoft.Identity.Web.Ui` (Razor sign-in UI) | **moved** — with `Microsoft.Identity.Web`, together, as MeshWeaver.Plugins fill-in pins (Plugins#4297, then #7262) |
| `tools/MeshWeaver.ModuleLibraries` | referenced `Radzen.Blazor` for the Plugins module `MeshWeaver.Blazor.Radzen` | **deleted** — MeshWeaver.Plugins owns its shelf (below) |
| `tools/MeshWeaver.PluginTester` (the Razor build extension and its staging, `stage-razor-generators.py`, the Razor tests) | the tester image carried the SDK's Razor source generator so `build-project` compiled `.razor` modules | **moved** — MeshWeaver.Plugins carries the extension (`src/MeshWeaver.PluginTester.Razor`) and supplies it through `build-extensions-project` (below); core keeps only the generic extension seam |
| `.github/scripts/check-bundle-static-assets.py`, `node-repo-module-pack.yml` | the shared module lane asserts a bundle declares every static web asset it owes | **generic** — the source tree's `wwwroot/**` plus every file the build emitted under its output's `wwwroot/`; no source kind's asset convention is encoded in core any more |
| `memex/` (`MeshModulesPublish.targets`, `aspire/Memex.Portal.ServiceDefaults`, its `Directory.Build.props`) | the `modules/<Name>/` publish layout, whose comments describe the Blazor view packs, and the portal hosts' service defaults | **moved** — MeshWeaver.Plugins owns both under `src/` (Plugins#4333), since only its hosts use them; core keeps the reader side of the layout (`ImageModuleSeed`, `PlatformShippedAssemblies`, `MeshBuilder.ResolveModulePath`) |
| `deploy/helm`, `deploy/aks` | comments explaining sticky sessions and slow scale-down for Blazor circuits | prose about the deployed portal, not a reference — and the deployment material is leaving core separately |
| `.github/workflows/dotnet-test.yml`, `stage-chromium-fonts.sh` | headless Chromium for the PDF export tests | not covered (a document renderer, see the table at the top) |

## Build tooling that named a GUI — moved to the module repository

Both items were platform BUILD capabilities that existed only so that a GUI module could be
compiled. The platform is now generic, and the module repository supplies the GUI-specific half.

### The module-library shelf (`tools/MeshWeaver.ModuleLibraries`)

**Done.** The shelf of extra libraries a container-built module may bind (provider SDKs,
import/export helpers, `Radzen.Blazor` for `MeshWeaver.Blazor.Radzen`) is no longer a core
project. Core keeps the *mechanism* and the module repository owns the *list*:

1. **Core (addition) — in place.** `mw-plugin-test build-project --module-libs <dir>` reads that
   directory as the shelf, and it REPLACES the search for the image's `module-libs/` the way
   `--razor-generators` replaces the Razor search. A named directory that is missing or holds no
   single `deps.json` fails the build by name; it never falls back to the image's shelf
   (`ModuleLibrariesShelfTest`). The shared `node-repo-module-pack.yml` lane takes
   `module-libraries-project:`, a csproj in the caller's repository. The workspace-build job
   publishes it against the platform checkout at `platform-ref`, fails closed on a publish with
   no single `deps.json` or no assemblies, removes the checkout and the project's `obj`/`bin` before
   the source mount, and passes the result as `--module-libs`. It refuses a pinned tester that does
   not advertise the flag, naming the pin to move. The named shelf is also part of every module's
   build-ledger key (`module-build-key.py --module-libraries-project`): its project, every in-repo
   project it reaches and the `Directory.*.props|targets` it inherits, so a shelf edit is a new
   build and never a reused bundle. Diff-scope selection (`node-repo-scope.py`) is told the
   shelf too: no module references it, so a diff touching it or a project it reaches runs and
   tests every module instead of narrowing to none. Omitted, the tester image's shelf is used, the key is
   byte-identical to before, and nothing changes for a caller that sets nothing.
2. **Plugins — in place.** `src/MeshWeaver.ModuleLibraries` carries every entry only Plugins
   modules use (Plugins#4303), `src/Directory.Packages.Plugins.props` pins them — members of a
   family core still pins part of take their version from that sibling — and the module-pack call
   passes `module-libraries-project:` (Plugins#4322).
3. **Core (deletion) — in place.** Core deleted `tools/MeshWeaver.ModuleLibraries`, the main-cd step that
   stages it into the tester image, and the pins only it used (`Radzen.Blazor`, the AI provider
   SDKs and their `Microsoft.Agents.AI` / `Microsoft.Extensions.AI` families, ClosedXML, CsvHelper,
   MailKit, Microsoft.Graph, ExcelDataReader, the Orleans clustering/persistence and
   ModelContextProtocol members only Plugins restores), and `InstanceSetupDefaults.RequiredModules`,
   the list of first-party modules a portal must load, which the Fleet Console now declares. The
   tester's own build no longer stages a shelf either (its `StageModuleLibraries` target and the
   `MeshWeaverModuleLibrariesRoot` property are gone with the step that set it). The tester keeps
   reading a shelf named by `--module-libs`, or a `module-libs/` laid beside it or the container, so
   the mechanism outlives the list.

**Risk.** The closure stops being one shared, image-baked fact and becomes per-run: a lane that
publishes the shelf itself must derive the closure from that publish's `deps.json` exactly as the
image job does today (the 2026-08-19/20 outage was a guessed closure), and the bundle must still
refuse a reference the shelf did not supply. The tester image no longer carries a default shelf,
so a satellite whose container modules need one must pass `module-libraries-project:` itself.

### The Razor source generator — done: core carries no Razor build code

`mw-plugin-test build-project` compiles a source kind it does not know itself through a BUILD
EXTENSION, and the platform ships none. `tools/MeshWeaver.PluginTester.Extensibility` is the
contract: an extension claims item types (their `Include`/`Remove` operations reach it in
declaration order) and source suffixes, contributes a source-generator run — its additional texts,
global and per-file analyzer-config options — which the builder executes BEFORE the generic
`--generators` ones, writes the static assets its inputs imply after the builder's verbatim
`wwwroot/` copy (a refusal is RED), names the cause of compile errors that point at it, and indexes
the type names its files declare for the superseded-entry staleness check. The builder loads
extensions from `build-extensions/<name>/` beside itself, or from `--extensions <dir>`, which
replaces that search, into a host-first load context, so Roslyn and the contract are one type on
both sides. `BuildExtensionSeamTest` holds the seam with a fake extension.

**Razor is MeshWeaver.Plugins' extension.** `src/MeshWeaver.PluginTester.Razor` carries the Razor
SDK's item rules, the CSS scope, the generator lookup, collocated JS modules and the `.styles.css`
aggregate, and its tests run there. The module-pack lane publishes it per run from the caller's
`build-extensions-project:` against the platform checkout — with the exact SDK the ledger keys,
because the extension's generator bytes come from that SDK — and passes it as `--extensions`.

**What core keeps, and why.**

- **The unclaimed-source refusal.** A project carrying `.razor`/`.cshtml` that no loaded extension
  claims is refused by name, including an explicit `Content Include` from outside the project. That
  is what turns "no Razor extension loaded" into a named failure instead of a smaller assembly, so
  the two suffixes stay listed in the builder.
- **The bundle's static-asset check, made generic.** `check-bundle-static-assets.py` demands what
  the source tree's `wwwroot/**` holds and every file the BUILD emitted under its output's
  `wwwroot/`. Which assets an extension-compiled source implies is the extension's knowledge: it
  emits them and refuses at build time an input whose asset cannot be produced.

**Risk.** A Razor-compiler regression against a new platform Roslyn now surfaces on Plugins' side
at its next run against a sealed platform set rather than in core's own CI — the documented
compatibility lag (policy `dependents-verify-on-their-side`). The extension binds the contract by
identity through the host-first context, so a contract change in core is a pair change with
Plugins like any other public surface.

## See also

- [Carving Projects Out Of Core](../CarvingProjectsOutOfCore) — the general source-move vs module-move map
- [Cross-Repo Pair Gate](../CrossRepoPairGate) — `Pairs-with:` and `Satellite-pins:`
- [Security Scanning](../SecurityScanning) — the BlazorMonaco finding the moved guard protects
