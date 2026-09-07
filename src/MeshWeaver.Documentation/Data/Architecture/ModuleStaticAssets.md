---
nodeType: Markdown
name: A Module's Static Web Assets
category: Architecture
description: A module's CSS/JS ride the bundle in their own folder and must land MODULE-RELATIVE beside the entry assembly, or the module loads perfectly and 404s every asset it ships behind one Debug line. The four hops, the four landing sites, and the measured denominator.
icon: /static/NodeTypeIcons/code.svg
---

# A Module's Static Web Assets

A module is not only assemblies. A view pack ships CSS and JS — its own collocated
`*.razor.js`, its scoped-CSS aggregate, and its dependencies' assets already namespaced under
`_content/<Dep>/` — and those bytes travel a **separate path through the bundle** from the DLL
closure. Anything that lands a module by hand and copies only the closure produces a module that
**loads perfectly and then 404s every asset it ships**, announcing it in a single `LogDebug` line.

That is MeshWeaver#3514, and this page exists because the failure has no other tell.

## First, the claim this page corrects

**"Modules cannot carry static assets" is stale, and has been since #1644/#1724.** The living
constraint is the NATIVE one: `Assembly.LoadFrom` never reads a module's `deps.json`, so a
`modules/<Name>/runtimes/<rid>/native/…` tree is unreachable by construction and the closure lane
deletes it by design (#1728). Static WEB assets are a different claim wearing the same words: the
lane exists end to end, and every hop of it is deliberate.

The reason the stale reading persists is that the lane was **dead in production** for a while
without any code path being wrong — `PluginBundleEndpoints.MapPublish` discarded `StaticAssets` at
the registry shelf, so six landed Blazor packs held `wwwroot = 0` and
`MeshWeaver.Blazor.EntityViews.styles.css` 404'd live until #2221. *Reading the code was not
verifying the delivery.* Check the artefact on disk, or the URL.

## The four hops

```
 pack ─────────────► bundle ─────────────► land ─────────────► serve
 ModulePackCommand   meshweaver/modules/   <module dir>/*.dll   _content/<Name>/…
                     meshweaver/moduleassets/<rel>
                                           <module dir>/<rel>
```

**1 — Pack.** `ModulePackCommand` enumerates the module publish's `wwwroot/**` and writes each file
at `meshweaver/moduleassets/wwwroot/<relative>` (`NuGetPackageWriter.ModuleAssetEntryPathFor`).
Two folders, on purpose, and their shapes are opposite: **module files are FLAT** (a bundle carries
one module, its manifest names every file), while **assets keep their module-relative path**,
because a pack's own component source hard-codes the URL it will request.

**2 — Bundle.** `meshweaver/modules/` holds the closure. `meshweaver/moduleassets/` holds the asset
tree. Nothing under the first implies anything about the second, and the two counts do not
correlate: measured on one real run, `MeshWeaver.AI.AzureFoundry` carries **82 assemblies and
0 assets** while `MeshWeaver.Blazor.Chat` carries **23 assemblies and 283 assets**.

**3 — Land.** `ModuleLandingService.LandCore` writes the assemblies flat into a fresh generation
directory and then writes each asset at its **verbatim module-relative path** under the same
directory. So `moduleassets/wwwroot/x.js` becomes `<module dir>/wwwroot/x.js`.
`ModuleLandingService.ValidateAssetPath` refuses a rooted or traversing path before any byte
touches disk.

**4 — Serve.** `MeshModuleStaticAssetExtensions.ModuleWwwrootPath` looks for `wwwroot` **beside the
assembly that was actually loaded** — not at a name-derived path, because landing writes
`modules/<Name>@<generation>/` and a process-local pin writes somewhere else again (#2509/#2562).
`Discover` then applies two opposite rules: the pack's OWN assets (at the wwwroot root) are re-based
onto `_content/<Name>/…`, and its DEPENDENCIES' assets (already at `wwwroot/_content/<Dep>/…`) are
served at that same path **only when the host does not already provide `<Dep>`**, so a module can
never shadow a platform asset.

## The invariant, in one line

> **A landing copies `meshweaver/moduleassets/` MODULE-RELATIVE into the same directory it copied
> `meshweaver/modules/` into.** Anything else and hop 4 looks in a directory that is not there.

Note *module-relative*, not *wwwroot*. `wwwroot/` is the only asset root the packer currently emits,
but the packer's contract — and `ValidateAssetPath`'s — is a relative path, so a landing written
against `moduleassets/wwwroot` specifically is correct today and silently lossy the day a second
root appears. Copy `moduleassets/.`, not `moduleassets/wwwroot/.`.

## Why this survives review, and CI, and production

Each of these is individually reasonable, and together they make the defect invisible:

- **The module loads.** The closure is complete; only the assets are missing. Nothing throws at
  load, at activation, or at compile.
- **The log says almost nothing.** `Discover` logs `Module {Module} contributes no static assets`
  at **Debug**, deliberately — for a module still riding the app closure that path is legitimately
  absent, and warning on every boot would be noise. Correct, and it means the tell is below the
  default log level.
- **Production is unaffected.** The runtime consumer (`ServedModuleBytes.FromSealed`) reads the
  `moduleassets/` prefix correctly, so the portals serve these URLs 200 and no deployment can
  reproduce it.
- **The lanes themselves do not look.** `node-repo-publish-bake` is mesh-free and
  `node-repo-gate` runs no browser, so both were green on a landing that dropped every asset.
- **The victim is a THIRD party.** It bites the first browser-driven consumer downstream — which
  was MeshWeaver.Education's mesh e2e, three days after MeshWeaver.Plugins#1268 moved the
  collaboration views into `MeshWeaver.Markdown.Collaboration`: `Failed to fetch dynamically
  imported module: …/_content/MeshWeaver.Markdown.Collaboration/Components/
  collaborativeMarkdownView.js`, every exercise page rendering a brief and then a bare Code node
  card where the workbench belongs, 4 × 180 s per course.

**So a green lane is not evidence here.** The only honest signal is the bytes on disk or the URL.

## Every place that lands a module, and what each one owes

Measured across the platform's CI on 2026-09-07:

| Site | Shape | Owes assets? |
|---|---|---|
| `node-repo-gate.yml` → `ext-modules` | `--module /ext/<Name>/<Name>.dll` into a running mesh | **Yes** — fixed by #3514 |
| `node-repo-publish-bake.yml` → `ext-modules` | same, for the compile surface | **Yes** — fixed by #3514 |
| `node-repo-compile-check.yml` | `cp modules/*.dll refs/` — a Roslyn REFERENCE SET | No. Nothing is served; a reference set has no request path. |
| `node-repo-module-pack.yml` | asserts the entry DLL is present in a freshly packed bundle | No. It inspects, it does not land. |
| `ModuleLandingService` (runtime) | the production path | Already correct |
| `ServedModuleBytes` (runtime) | reads the sealed bundle | Already correct |

The rule that decides the column is not "is this a module?" but **"will a browser ask this process
for `_content/<Name>/…`?"** A reference set never will.

🚨 **A hand-rolled copy of a lane block is where this recurs.** MeshWeaver.Education's
`e2e/mesh/fetch-upstream.sh` is exactly such a copy, and it is the one place the latent gap became a
real outage. MeshWeaver.Plugins' `scripts/mesh-test.py` unpacks bundles whole and hands the mesh
`--module …/meshweaver/modules/<Name>.dll`, which is the same shape — latent only because nothing
points a browser at it (MeshWeaver.Plugins#1440). See
[Module Build Architecture](/Doc/Architecture/ModuleBuildArchitecture): never hand-roll a repo's
build; call the shared lane.

## The gate

`.github/scripts/test-module-asset-landing.py` EXECUTES the lanes' landing rather than asserting
about it, and runs in `dotnet-test.yml` beside the other workflow-shell gates.

- It **extracts both lanes' step by `id:`** from the workflow and runs that text. A copy in the
  harness would pass while the real thing rots; the `id:` is therefore load-bearing, and losing it
  is RED, not a skip.
- The verdict is read **off the landed bytes** — every shipped asset must exist at its
  module-relative path with a matching sha256 — never off the step's own log.
- The bundle layout is **read out of `NuGetPackageWriter.ModuleAssetFolder` in `src/`**, so renaming
  the constant reds the harness instead of silently orphaning the lane.
- **Two falsification arms per lane, both mutating the real step**: with the asset block removed
  (the pre-#3514 lane) the positive case must go red; with only the `cp` removed, the lane's OWN
  fail-closed check must fail — which is what proves that check is not a no-op.
- **Three controls**, or the harness could not tell "it lands assets" from "it always passes": a
  bundle with no assets still composes, a bundle with no entry DLL is refused, and the DLL closure
  still lands beside the assets.

The lane's own check is per file and prints a denominator:
`external modules: 30 composed, 6 of them carrying static web assets, 294 asset file(s) landed
module-relative under /ext/<module>/`.
A count that could only ever read zero is not a check — [an uncounted zero has two causes](/Doc/Architecture/NegativeControls)
and one of them is "we looked in the wrong place".

## The measurement

Run against the 30 real module bundles of MeshWeaver.Plugins run `34086016940` (2026-09-07),
through the extracted lane step:

| | modules | with assets | asset files shipped | landed byte-identical |
|---|---|---|---|---|
| with the fix | 30 | 6 | 294 | **294** |
| pre-fix | 30 | 6 | 294 | **0**, exit code **0** |

The 6: `MeshWeaver.Blazor.Chat` (283 — its editor JS and its dependencies' namespaced assets),
`MeshWeaver.Blazor.OpenStreetMap` (7 — the `.razor.js` of #2384's class), and
`MeshWeaver.Blazor.Analysis` / `.EntityViews` / `.Graph` / `.Radzen` (1 each — the scoped-CSS
aggregate). The other 24 ship no assets at all, which is why the pre-fix lane could look healthy:
**80% of the population is a legitimate zero.**

The second row is the whole point. The pre-fix lane exited **0** having landed **none** of the 294
files — green, and wrong, with nothing in its log to distinguish it from the 24 modules for which
zero was the right answer.

### Closing the loop: the URL, not the directory listing

A landed directory is a proxy. The measurement that is not a proxy runs the whole chain — real
bundle → the lane step extracted from `node-repo-gate.yml` → a real `WebApplication` with
`AddMeshModuleStaticAssets()`/`UseMeshModuleStaticAssets()` over the landed tree — and fetches the
URL. Against `MeshWeaver.Blazor.Chat` 1.0.23 (23 assemblies, 283 assets, 5 mounts):

| request | with the fix | pre-fix |
|---|---|---|
| `_content/MeshWeaver.Markdown.Collaboration/Components/collaborativeMarkdownView.js` — #3514's literal stack trace | **200**, `text/javascript`, 12,391 B, byte-identical | **404** |
| `_content/MeshWeaver.Blazor.Chat/chatResizer.js` (own asset, re-based) | **200**, 4,273 B, byte-identical | **404** |
| `_content/MeshWeaver.Blazor.Chat/ChatMessageList.razor.js` (collocated JS) | **200**, 22,739 B, byte-identical | **404** |
| `_content/MeshWeaver.Blazor.Chat/background.png` (binary) | **200**, byte-identical | **404** |
| same URL with `Accept-Encoding: br` | **200** + `Content-Encoding: br`, byte-identical to the landed `.br` sibling | **404** |
| `_content/MeshWeaver.Blazor.Chat/does-not-exist.js` (negative control) | **404** | **404** |

The negative control is why the first five rows mean something: a mount that answered 200 for
everything would satisfy them all.

And the pre-fix host's entire complaint, at **Debug**:
`Module MeshWeaver.Blazor.Chat contributes no static assets — no wwwroot at …/MeshWeaver.Blazor.Chat/wwwroot`.
That single line is what a whole outage looked like from the inside.

## If you are landing a module bundle yourself

1. Copy `meshweaver/modules/.` into `<module dir>/`.
2. Copy `meshweaver/moduleassets/.` into the **same** `<module dir>/`. Module-relative, not
   `wwwroot`-specific.
3. Assert per file that what the bundle ships is what the directory holds, and **fail closed** with
   both numbers in the message.
4. Point the loader at `<module dir>/<Name>.dll`. Hop 4 keys off the loaded assembly's own
   directory, so the module directory is the only thing that has to be right.

## Related

- [Module Build Architecture](/Doc/Architecture/ModuleBuildArchitecture) — the unified build; never
  hand-roll a repo's lane
- [Plugin Packaging](/Doc/Architecture/PluginPackaging) — bundles, the framework identity, the
  `Release` node
- [Module Versioning](/Doc/Architecture/ModuleVersioning) — what you author, what the build derives
- [The Platform Image's Closure](/Doc/Architecture/PlatformImageClosure) — what a module may assume
  is already there
- [CI Content Bake](/Doc/Architecture/CiContentBake) — the shared `workflow_call` lanes satellites
  call instead of copying
