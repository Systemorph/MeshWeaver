---
nodeType: Markdown
name: Core Carries No GUI Technology
category: Architecture
description: The platform repository names no GUI technology — no Blazor, Razor components, FluentUI, Radzen, Monaco wrapper or browser automation. What that rule covers and what it does not, the measured inventory, what moved to MeshWeaver.Plugins (the GUI package pins, the Playwright suites), the order the two halves land in, and the proposal for the build tooling that still names a GUI convention.
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
(MeshWeaver.Plugins#4146) and core's copy is deleted by its own delete half. It is not changed here.

### Landing order

The Plugins half is an ADDITION and lands first; the core half DELETES and lands last
([Cross-Repo Pair Gate](../CrossRepoPairGate)):

1. **Plugins** — adds `Directory.Packages.Plugins.props` and its import, the moved projects, their
   registration, and the moved pin guard. Against today's core it changes nothing (every pin is
   still declared by the platform, so every entry is skipped).
2. **Core** — removes the five pins (declared under `Satellite-pins:`), the three projects, the
   E2E/thumbnail entries in `MeshWeaver.slnx`, `shard-assign.sh`, `test/Directory.Build.props` and
   `HandWovenGateRatchetGuard`, and `BlazorMonacoPinGuard`. Its body carries
   `Pairs-with: Systemorph/MeshWeaver.Plugins#<n>`.

No satellite other than Plugins references any of the moved packages (measured on `origin/main` of
Education, Reinsurance, SocialMedia, Manufacturing and Crm). Memex has its own
`Directory.Packages.props` and is unaffected.

## Inventory — what still names a GUI after this change

| Where | What | Status |
|---|---|---|
| `Directory.Packages.props` | `Radzen.Blazor` | **stays for now** — the module-library shelf stages it (below) |
| `Directory.Packages.props` | `Microsoft.Identity.Web.Ui` (Razor sign-in UI) | **stays for now** — its last core consumer is `memex/Memex.Portal.Shared`, deleted by the core-slimming delete half. It exact-pins `Microsoft.Identity.Web`, so the two move to Plugins **together**, after that delete lands |
| `tools/MeshWeaver.ModuleLibraries` | references `Radzen.Blazor` for the Plugins module `MeshWeaver.Blazor.Radzen` | build tooling — proposal below |
| `tools/MeshWeaver.PluginTester`, `.github/scripts/stage-razor-generators.py`, `main-cd.yml` (tester leg), `test/MeshWeaver.PluginTester.Test` (`RazorBuildTest`) | the tester image carries the SDK's Razor source generator so `build-project` compiles `.razor` modules | build tooling — proposal below |
| `.github/scripts/check-bundle-static-assets.py`, `test-module-asset-landing.py`, `node-repo-module-pack.yml` | the shared module lane asserts a bundle carries its Razor static web assets (`*.razor.js`, scoped `*.razor.css`, `_content/<pack>/`) | build tooling — moves with the Razor staging (below) |
| `memex/MeshModulesPublish.targets` | the `modules/<Name>/` publish layout; its comments describe the Blazor view packs | imported only by Plugins' hosts and `Memex.Hosts.Test` once `memex/Memex.Portal.Shared` is deleted — **move it to Plugins** as a follow-up source move (add half repoints the four `$(MeshWeaverRoot)/memex/…` imports, then core deletes) |
| `deploy/helm`, `deploy/aks` | comments explaining sticky sessions and slow scale-down for Blazor circuits | prose about the deployed portal, not a reference — and the deployment material is leaving core separately |
| `.github/workflows/dotnet-test.yml`, `stage-chromium-fonts.sh` | headless Chromium for the PDF export tests | not covered (a document renderer, see the table at the top) |

## Build tooling that still names a GUI — the proposal

Not changed in this pass. Both items are platform BUILD capabilities that exist only so that a GUI
module can be compiled; the proposal makes the platform generic and lets the module repository
supply the GUI-specific half.

### The module-library shelf (`tools/MeshWeaver.ModuleLibraries`)

**Today.** A curated csproj of extra libraries a container-built module may bind (provider SDKs,
import/export helpers, and `Radzen.Blazor` for `MeshWeaver.Blazor.Radzen`). Main-cd publishes it into
the tester image as `module-libs/`; its `deps.json` is the closure, core's central pins its versions.

**Proposal.** Keep the *mechanism* in core and move the *list* to the repository that owns the
modules: the shelf becomes a declaration the module lane receives, not a hard-coded project.

1. **Core (addition)** — `mw-plugin-test build-project` accepts `--module-libs <dir>` in addition
   to the staged `module-libs/`, and the shared `node-repo-module-pack.yml` lane gains an optional
   `module-libraries-project:` input: when set, the lane publishes THAT csproj (against the platform's
   pins plus the caller's own pin file) and hands its output to `build-project`. The tester-image
   shelf stays as the default, so nothing changes for a caller that sets nothing.
2. **Plugins** — adds its own `tools/ModuleLibraries` csproj carrying the entries only Plugins
   modules use (`Radzen.Blazor`, and over time the provider SDKs), pins `Radzen.Blazor` in
   `src/Directory.Packages.Plugins.props`, and passes `module-libraries-project:`.
3. **Core (deletion, last)** — drops `Radzen.Blazor` from its shelf and from `Directory.Packages.props`,
   declared under `Satellite-pins: Radzen.Blazor`.

**Risk.** The closure stops being one shared, image-baked fact and becomes per-run: a lane that
publishes the shelf itself must derive the closure from that publish's `deps.json` exactly as the
image job does today (the 2026-08-19/20 outage was a guessed closure), and the bundle must still
refuse a reference the shelf did not supply. Each satellite that relies on a shelf entry
(Import's ClosedXML/CsvHelper, the AI providers) keeps working on the image default until it opts
in, so the change can be staged per repository.

### The Razor source generator in the tester image

**Today.** `tools/MeshWeaver.PluginTester` stages the .NET SDK's Razor source generator per RID
(`razor-generators/`, from `stage-razor-generators.py`, main-cd's tester leg) so `build-project` can
compile `.razor`/`.cshtml`; `RazorBuildTest` pins it against core's Roslyn; the shared lane's static
asset assertions know the Razor asset conventions.

**Proposal.** Make the tester's generator loading generic, and let the GUI repository supply the
Razor one.

1. **Core (addition)** — generalise `razor-generators/` to `generators/<name>/<rid>/`: the tester
   loads every staged Roslyn source generator through the same load context, with the same
   per-RID, PE-machine and provenance checks `stage-razor-generators.py` performs today. The Razor
   generator keeps being staged by default, so the image does not change.
2. **Plugins** — a small `tester-gui` image (`FROM` the platform tester at the pinned digest) adds
   the Razor generator layer, plus `RazorBuildTest` and the static-asset assertions
   (`check-bundle-static-assets.py`, `test-module-asset-landing.py`) as Plugins-side gates, and the
   module lane runs Razor modules on it.
3. **Core (deletion, last)** — stops staging the Razor generator, deletes
   `stage-razor-generators.py`, `RazorBuildTest` and the static-asset scripts, and removes the
   Razor-specific branches from `ProjectFile` (the `.razor`/`.cshtml` item resolution and CSS-scope
   computation move with the generator as a plugin to the builder, or stay as a generic
   "additional files for a generator" hook).

**Risk.** This is the larger change: Razor compilation is wired into `ProjectFile` (`RazorItem`,
`ScopedCss`), not only into staging, so step 3 needs that seam first; and a Razor-compiler
regression against a new platform Roslyn would surface on Plugins' side at its next run against a
sealed platform set rather than in core's own CI — the documented compatibility lag
(policy `dependents-verify-on-their-side`). Step 1 is safe on its own and worth doing first.

## See also

- [Carving Projects Out Of Core](../CarvingProjectsOutOfCore) — the general source-move vs module-move map
- [Cross-Repo Pair Gate](../CrossRepoPairGate) — `Pairs-with:` and `Satellite-pins:`
- [Security Scanning](../SecurityScanning) — the BlazorMonaco finding the moved guard protects
