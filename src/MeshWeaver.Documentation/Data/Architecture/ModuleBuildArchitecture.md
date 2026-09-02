---
nodeType: Markdown
name: Module Build Architecture
category: Architecture
description: THE unified build process — every repo, one shape. The platform image is the compiler and the reference set, everything shared is staged once per run, one Roslyn workspace builds the graph fail-fast, outputs are content-addressed in blob storage so unchanged means no compile, and every gate compiles against implementation frameworks.
icon: /static/NodeTypeIcons/code.svg
---

# Module Build Architecture

**This is THE build process, unified across every repo** (maintainer directives, 2026-08-31 →
2026-09-01: *"we want the memex to take care of the build"*, *"update once at beginning for
everyone"*, *"create one roslyn workspace, all plugins to be rebuilt plus dependencies"*,
*"maybe put in blob storage for build"*, *"unify buildprocess"*). The platform repo carries the
mechanics — the reusable lanes, the builder, this page; a consuming repo carries ONLY policy
(which modules, which pins). A repo whose CI deviates from this page is behind, not different.
Never hand-roll a repo's build.

## The pipeline, end to end

```
select ──► prepare (ONCE) ──► build (ONE workspace) ──► pack ─┬─► verify
                                                      tests ──┘
```

1. **select** — which bundles this diff can reach. The selector can say "these" or
   "everything", never "skip"; a workflow/pin change legitimately selects everything, because
   the compiler itself changed.
2. **prepare, once per run** — everything every job consumes identically is staged here, never
   per module: the platform image as a per-digest zstd tarball in the **actions cache (GitHub's
   blob storage, colocated with the runners)**, the tester-app and platform-refs extractions,
   the module-pack tool. On a warm digest the run touches **no registry at all** — measured
   live: `platform image: cache HIT — docker load, no ACR`. A cache miss falls back to pulling
   the same digest-pinned bytes, loudly: a perf fallback, never a verification fallback.
3. **build — one Roslyn workspace.** The builder compiles the selected modules **plus their
   in-repo dependencies as one graph**: every project shares the same
   `PortableExecutableReference` per path (each assembly read from the filesystem once), one
   body pass like csc (parse+declaration diagnostics up front, body diagnostics from the single
   `Emit`), **fail-fast** (the first red blocks every not-yet-started node by name — a sweep
   that must enumerate every verdict opts out of fail-fast instead).
4. **pack and tests fan out from the build's outputs** — they consume, they never recompile.
   Tests are the honest per-module cost and parallelize across jobs.
5. **verify** — unconditionally pairs the selection against the receipts; a skipped pack with a
   non-zero selection is RED.

## Every stage asserts its OWN postcondition — never the next one's

A stage's exit code answers *"did my work throw?"*. It does not answer *"did I produce what the
next stage requires?"*, and the two were read as one claim until it cost a day.

**The build is where this bites.** `build` reported success and the pack matrix then died, seven
jobs at once, with `the global workspace build produced no MeshWeaver.AI.OpenAI.dll` — on a pull
request whose entire diff was one XML doc comment. The message was accurate and one job too late:
the stage that knew the selection was the green one, and the stage that discovered the gap knew
only its own module. Two enumerators — the projects handed to the build and the modules the matrix
expands — were free to disagree, and nothing compared them.

So the build now checks, before it uploads anything, exactly what its consumers demand, **per
selected entry**:

| the consumer demands | the producer now asserts |
|---|---|
| `<Module>/<Module>.dll` | present and non-empty (a truncated emit reads as "the file is there") |
| `<Module>.closure.txt` | present — without it a pack job cannot know which in-tree siblings ride *this* bundle, and a glob would ride every other module's |
| `platform AssemblyVersion` in the build log | readable — it is the expected side of the binding-identity check every pack job runs |

Three properties make it a postcondition rather than a second opinion:

* **The same enumerator on both sides.** It is fed the selection the matrix expands, not a
  re-derivation and not a glob of the output directory. A postcondition derived from a *different*
  enumerator is the original defect, one level up.
* **A glob would pass the case it exists to catch.** The workspace legitimately holds every
  entry's assemblies side by side, so "there are DLLs in there" is true in exactly the failure
  being diagnosed. Only a per-selected-module check can see it.
* **No `if:` on it, and its self-test runs first.** The selection-with-no-container-entries case
  is handled *inside* the check (it asserts nothing and says so) rather than by a condition that
  would render as a grey tick indistinguishable from a pass. An unproven postcondition is the
  thing this section is about.

It deliberately never asks the reverse question: the workspace carries in-tree dependencies of
selected modules beside them, so "emitted but not selected" is normal and is not a finding.

**The generalisation, for any stage you add to this lane:** name the artifacts the next stage
opens, and assert them where they are produced. One accurate failure upstream is worth N accurate
failures downstream, and a downstream failure can only ever describe its own shard.

## Content-addressed outputs in blob storage = incremental CI

Build outputs are keyed by **(module source tree hash × tester-image digest × platform-image
digest)** in the actions cache. Consumers restore by key; producers publish once.

Two consequences, both load-bearing:

* **"Build only what changed" falls out for free, below the selector**: an unchanged module on
  unchanged toolchain digests is a pure cache hit — the restore IS the build, zero compile.
  This composes with (does not replace) diff selection.
* **No producer/consumer races**: downstream jobs `needs:` the build job and restore its
  outputs — no artifact polling, no bounded waits.

Own-account blob storage would add auth and distance for nothing; the actions cache is the
same Azure blob, colocated, already carrying the images.

## The compiler is the platform image

A module never runs against a source tree or a NuGet feed: it is loaded into the platform
IMAGE and bound by the assemblies in there. So the honest compiler is that image's own —
`mw-plugin-test build-project` runs INSIDE the pinned platform container; its `/app` is the
reference set, its runtime is the shared-framework surface. No SDK, no restore, no platform
source checkout. A declared mode that cannot run FAILS — there is deliberately no SDK
fallback, because a fallback makes "the container built it" and "the SDK built it"
indistinguishable in a green log.

Additional libraries resolve from the curated **module-libraries shelf**, and a
`PackageReference`'s compile surface is its **transitive closure** from the shelf's deps.json —
exactly what the SDK hands a consumer (`PackageReference Microsoft.Graph` lets code
`using Microsoft.Kiota…`; anything less re-creates that gap as a CS0234 wall).

🚨 **What a module COMPILES against and what its bundle CARRIES are two questions with two
answers.** For the compile the image is authoritative — a module binds the assemblies of the host
it is loaded into. For the bundle it is not: the image is a PORTAL, and a portal with a module
compiled into it also carries that module's private package dependencies, so "`/app` has the file"
is a fact about one host, never a platform guarantee. A bundle carries its own package closure
minus the SHARED FRAMEWORK, exactly as the SDK lane's `--deps-closure` does — see
[ModuleClosureAccounting](../ModuleClosureAccounting) for the rule and the two outages that came
from conflating the two questions.

> ⏳ **The bake's REFERENCE SET — which image's `/app` a NodeType bake compiles against — and the
> framework identity that keys a publication are being redesigned** (branch
> `fix/3022-bake-reference-set-is-the-portal`; the identity-fork guard is PR #3066, the
> `-p:Version=` fork itself was fixed in #3041). This section describes the compile of a MODULE and
> is unaffected; read [CiContentBake](../CiContentBake) → "The identity rule" and
> [BakeIdentityMismatch](../BakeIdentityMismatch) for the bake half once those land.

## Gates compile against IMPLEMENTATION frameworks

Every compile-check extracts the image's `/usr/share/dotnet/shared` beside `/app` and, seeing
`System.Private.CoreLib.dll`, drops the SDK ref pack entirely: the check compiles exactly what
the mesh compiles. This is not a preference — a container-built module references
`System.Private.CoreLib`'s identity directly, which the ref pack cannot resolve (11 NodeTypes
false-red with CS0012 the moment the floor bundles were container-built) and which CS0433-s
beside it. Measured: 80/80 NodeTypes in 44s vs 161s + 11 false regressions.

## CI is silent — warn/error plus verdicts

Per-item narration (resource names, per-package resolutions) sits behind `--verbose`; the
default log carries verdicts (start/OK/FAIL, phase timings, the compiler declaration, test
names), warnings, and errors. Docker pulls are `-q`. In-run artifacts expire in 1–3 days.
No component ever publishes from the mesh router — the router names a spokesman
(`RouterCarrier` → the nodeops execution hub) and infrastructure speaks through it.

## Measured (2026-09-01, the night the shape landed)

| | before | after |
|---|---|---|
| MeshWeaver.AI compile (builder) | 161s local / 559s CI (double pass) | **80.6s local** (single pass); ~97% of the remainder is nullable flow analysis in a few very large methods — source-side work |
| NodeType gate | 161s + 11 false CS0012 reds | **80/80 in 44s** (implementation frameworks) |
| Registry trips per warm run | 2 pulls × N jobs (the `connection refused` stampede) | **0** |
| Module pack tool | 48–79s `dotnet build` × N jobs | ~5s download, built once |
| Orleans test suite | ~90 silo boots + disposal drain | **3 clusters** (the mesh pool; see WritingTests § The Mesh Pool) |

## Where the fleet stands — measured, not assumed (2026-09-02)

The contract below is the TARGET. This table is what `origin/main` of every repo actually did on
2026-09-02 (~07:15Z; sources: each repo's `.github/workflows/ci.yml`, `gh api …/branches/main/protection`
and `…/rulesets`, `az acr manifest list-metadata`). Re-measure before acting on it: a row that has
since moved is a row to fix here, not a row to trust.

| Repo | Lanes called (`uses:`) | Lane pin(s) | Platform image pin | Pin gate | Skip-detector job required? |
|---|---|---|---|---|---|
| MeshWeaver.Plugins | module-pack ×2, gate, publish-bake — **validate, compile-check and tag-modules hand-rolled**; the required `Compile every NodeType (vs core)` runs a **vendored** `scripts/compile-check.py` | **three shas** (`e16e301e`, `f1ed4041`, `8ae946c4`) | one gated SET (`MW_PLATFORM_SET` names the promoted build; tester + portal digests, six copies agree) | `scripts/check-platform-pins.py` in `validate`, `--check-tags` in `preflight` | `Every gate executed` exists, **not required** |
| MeshWeaver.Reinsurance | all five | one sha | one literal digest | none | no such job |
| MeshWeaver.SocialMedia | all five + module-pack | **two shas**; module-pack's `platform-ref` a further 462 commits behind | one literal | none | no such job |
| MeshWeaver.Manufacturing | compile-check, gate, publish-bake — validate and tag-modules hand-rolled | one sha | one literal | `check-ci-invariants.py` (job shape only) | `Every required gate actually ran` exists, **not required** |
| MeshWeaver.Crm | all five | one sha | one literal | none | no such job |
| MeshWeaver.Education | **none — fully hand-rolled**, with a vendored `publish-bake-bundles.sh` (194 lines against the platform's 434) | — | three literals from one CD run, no gate | none | no such job |

Four facts from that table that the contract does not yet describe as achieved:

* **The platform's scripts float at core `main` in every satellite today.**
  `node-repo-{compile-check,gate,publish-bake}.yml` fetch `compile-check.py`,
  `compose-sealed-modules.sh`, `bake-scope.sh`, `carry-forward-bundles.sh` and
  `publish-bake-bundles.sh` at their `platform-ref` input (`node-repo-validate.yml` gains the same
  input for `check-workflow-timeouts.py` with PR #3067). That input **defaults to `main`, and no
  satellite passes it** to those three lanes — Plugins passes it only to module-pack, resolved once
  from `MW_PLATFORM_REF: main`. So a core script change reaches every satellite on its next run, pin
  or no pin (two `compile-check.py` fixes did on 2026-09-01, 18:03Z and 18:22Z), and two runs of
  identical satellite code can disagree. There is a real tension underneath — the publish script's
  layout must match the portals that consume it, and the portals self-update from `main`
  ([CiContentBake](../CiContentBake) → "The one ref that still floats") — but the contract is
  `platform-ref` = the `uses:` sha, bumped together; until a repo passes it, its scripts are unpinned
  and this page says so rather than claiming otherwise.
* **Vendored `compile-check.py` copies still exist in five repos — six different files.** In
  Reinsurance, SocialMedia, Manufacturing and Crm CI does not run them: they are dead copies that lack
  core's `using static` handling, and each repo's AGENTS.md still tells a developer to run one, so a
  local verdict can disagree with CI's. In Plugins the vendored copy IS the required check.
* **The image pin is one gated set only in Plugins.** Elsewhere one literal (nothing to disagree
  with, and no gate that the literal names the build its comment claims — Plugins' own comment
  records exactly that drift), or Education's three ungated literals.
* **The skip-detector is required nowhere.** GitHub counts a skipped required context as satisfied
  ([ReadingCiSignals](../ReadingCiSignals)), so the job asserting "every gate actually ran" is the
  one context that must be required. Plugins and Manufacturing have the job and do not require it;
  the other four have no such job.

## Adoption contract (every repo)

1. Pin the reusable lanes (`node-repo-*.yml`) at **one** MeshWeaver main SHA per repo — never copy
   them, never `@main`, never two shas in one file. Bump every `uses:` line in one commit.
2. **Scripts are centralized**: the lane fetches the platform's `.github/scripts/compile-check.py`
   (and `compose-sealed-modules.sh`, `bake-scope.sh`, `carry-forward-bundles.sh`,
   `publish-bake-bundles.sh`, `check-workflow-timeouts.py`) at its `platform-ref` input and runs
   it against the caller's tree — a repo keeps ONLY its `scripts/compile-check.allow` (policy).
   **Pass `platform-ref` = the `uses:` sha on every lane call**; the input defaults to `main`, and a
   call that omits it floats (see the table above). Per-repo script copies are retired — delete
   them, and point the repo's AGENTS.md at the lane, never at a local file. (*"We can ship in
   hosting"* — the endgame is a `compile-check` verb inside the tester image itself, where the
   reference set is the container's by construction; the fetched-script stage is the unified interim.)
3. **The platform image pin is ONE SET, gated.** Every `sha256:` literal naming the tester image,
   every literal naming the portal image, and every `Systemorph/MeshWeaver` checkout `ref:` in the
   file agree, and the gate that says so (`scripts/check-platform-pins.py`; `--check-tags` resolves
   each pin against the named promoted build) runs in `validate`. *"We always compile against
   main"* is met by bumping the whole set to the newest **promoted** build — never by un-pinning
   (MeshWeaver.Plugins#1067, which proposed resolving `:main` per run, was closed for exactly that).
4. **Every job is hard-cut at 45 minutes** — a literal `timeout-minutes` ≤ 45 on every job;
   `check-workflow-timeouts.py` refuses a missing, oversized or expression-valued cap and runs on
   every satellite from the `validate` lane (core PR #3067). The rule and its evidence live in
   AGENTS.md → "Every CI job is HARD-CUT at 45 minutes" and the `/ci` skill; not restated here.
5. Reference this page from the repo's AGENTS.md — the build section defers here.
6. Repo-specific policy (module lists, always-modules, allow-files, registry consumption, the
   digest set) stays in the caller; mechanics never do.

## Roadmap (agreed, in flight)

* **The one-workspace lane** — the builder takes every selected module as entries of one graph
  (it already graph-builds with shared references and fail-fast); the per-module compile jobs
  collapse into one `build-workspace` job publishing content-addressed outputs; pack/tests fan
  out from it. *"After global build ⇒ run tests."*
* **`pack-module`, `compile-check` and test verbs inside the tester** — the last runner-side
  `dotnet`/python uses move into the image the fleet already ships.
* **Self-hosted runners with a mounted git mirror + warm image store** (MeshWeaver#2926): bare
  mirror volume, `git worktree add` per run at the release ref, worktree deleted at job end.

See also: [ModuleClosureAccounting](../ModuleClosureAccounting) ·
[ModuleVersioning](../ModuleVersioning) ·
[CiContentBake](../CiContentBake) (the bake, the seal, the release wave) ·
[ContinuousDeliveryContract](../ContinuousDeliveryContract) (what `main-cd` promises) ·
[ReadingCiSignals](../ReadingCiSignals) (what a tick proves) ·
[NodeTypeCompilation](../NodeTypeCompilation) · [PluginBuildContract](../PluginBuildContract) ·
[BuildProcess](../BuildProcess) (the cascade `build` verb — a design, not a lane) ·
[InMeshBuildAndTest](../InMeshBuildAndTest).
