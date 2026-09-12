---
nodeType: Markdown
name: Framework Identity Churn
category: Architecture
description: Why the framework identity moves on every core commit rather than on every content change — measured, with the falsification test that killed the obvious fix — what that churn costs CI today, and the costed options for #3583's bake-side half.
icon: /static/NodeTypeIcons/box.svg
---

# Framework Identity Churn

The framework identity is the directory name a prebuilt bundle lives under
(`<root>/<identity>/<source>/…`), so a consumer that resolves an identity nobody baked does not find
stale bytes — it finds **nothing**. The companion page *Bundle Delivery Stages* (committed separately, PR #4079) calls this
stage ④, *deliver*, and names it as the open half of #3583.

This page is the measurement behind the decision, and the decision is **not made here**. It records
what the churn is, what it costs, what the options cost, and — importantly — **one falsification
test that killed the option that looked cheapest.**

> 🚨 **Measured vs. arithmetic.** Every number below is labelled. The runner-hour figures are
> multiplication over measured inputs, not observations, and are marked as such. An estimate written
> in the language of measurement is the failure mode this fleet keeps paying for.

## What the identity is supposed to be

`FrameworkBuildIdentity` (`src/MeshWeaver.Compiler/FrameworkBuildIdentity.cs`) hashes the sorted
`(name, surface-id)` pairs of 26 canonical `ContentSurfaceAssemblies`, where `surface-id` is the
**SHA-256 of that assembly's REFERENCE ASSEMBLY** — the compiler's own definition of a public
surface, byte-stable under body-only and private-member edits. Seventeen of them
(`FullMvidAssemblies`, the toolchain closure of `MeshWeaver.Compiler` + `MeshWeaver.NuGet`)
contribute their **full implementation MVID** instead, because their code shapes the generated input
of every NodeType compile.

Its stated goal is in the file: *"Rebuild only when we need to — a NodeType's bytes go stale only
when the API SURFACE they compiled against changed."*

## What it actually does — measured

**Window: 2026-09-11T08:20Z → 2026-09-12T07:51Z (24 h). Core `main`, first-parent merges.**

| | count | how |
|---|---|---|
| merges to `main` | **43** | `git log --first-parent` (the control for every row below) |
| touched any of the **26** content-surface projects | **17** | path-filtered `git log` |
| touched any of the **17** full-MVID projects | **5** | path-filtered `git log` |
| touched the 2 toolchain roots | **0** | path-filtered `git log` |
| **distinct identities actually observed** | **≥ 18** | distinct `s…` values across 23 Reinsurance gate refusals — a floor, not a total: only 23 runs sampled |

So at most 17 merges had any content reason to move the identity, only 5 had a by-design one — and it
moved at least 18 times. **The churn is not content churn.**

## Why: the commit sha is compiled into every assembly

`Directory.Build.props` pins `AssemblyVersion`/`FileVersion` (#3022) but deliberately lets the SDK
append the commit to `AssemblyInformationalVersion`:

```
InformationalVersion (CIRun) = $(PlatformVersion)         → SDK appends +$(SourceRevisionId)
                             = e.g. 3.0.0+<40-char sha>
```

That is load-bearing and must not simply be deleted — the props explain why: *"the same bytes
whether the Build-and-Test run or the main-cd image build compiled them. That is what lets the CI
NodeType bake produce assemblies the deployed portal ADOPTS instead of recompiling."* It buys
**commit**-determinism. It does not buy **content**-determinism, and the identity is built on the
latter.

### 🚨 The falsification test — and it killed the obvious fix

**Prediction:** the sha attribute leaks only into the 17 full-MVID members (implementation MVIDs),
leaving the 26 reference-assembly surface hashes stable. If true, the fix is small and local:
exclude assembly-level attributes from the MVID contribution, inside `FrameworkBuildIdentity`.

**Method.** Three fresh builds of `MeshWeaver.Hosting`, `-c Release -p:CIRun=true
-p:MeshWeaverSurfaceManifest=true`, same tree, **identical** `--artifacts-path` and `--output` wiped
between legs so no path differs, varying only `-p:SourceRevisionId`. The differing input was
confirmed both in the generated `AssemblyInfo.cs` and in the built DLL's bytes
(`3.0.0+1111…` vs `3.0.0+2222…`). MVIDs were read from the `#GUID` heap of each PE (reader validated
first: same file twice → equal; two different assemblies → different).

| | MVIDs | surface hashes |
|---|---|---|
| **control** — A vs A2, *same* sha, two independent fresh builds | **0 of 22 moved** | **0 of 21 moved** |
| **test** — A vs B, identical content, *different* sha | **22 of 22 moved** | **21 of 21 moved** |

The control is what makes the test readable: builds are deterministic, so every difference in the
test row is attributable to the sha alone.

🚨 **The prediction was wrong, and the result is a refutation, not a confirmation.** The version
attribute lands in the **reference assembly too**, so all 21 measured surface hashes moved as well.
Confirming the mechanism from the other side: `ShortGuid`, `Utils`, `Reflection` and
`ServiceProvider` — leaf assemblies with no code path to anything that changed — moved identically,
which rules out any transitive code effect and pins it on the attribute.

**Denominators.** All **17** full-MVID members were built, and all 17 moved. **21 of 26**
content-surface assemblies were in this closure; the 5 not measured are
`Deployment.Contract`, `GitSync`, `Hosting`, `Mesh.Operations`, `PluginCatalog`.

**Consequence.** Normalising attributes out of the identity is **not** a change local to
`FrameworkBuildIdentity`. It has to cover the reference-assembly hash for all 26 as well, which lives
in `MeshWeaverSurfaceManifest.targets` — a file every host imports, including the portal in
MeshWeaver.Plugins. Both sides must agree byte-for-byte or every bake declines. It is a
fleet-coordinated change, not a local one.

## What the churn costs

**Measured inputs:** a satellite `publish-bake` job runs **9 m 07 s – 15 m 30 s** (7 samples, median
≈ 11 min). A full core CD run takes **42 – 59 min** (7 samples, median ≈ 48 min). Six publication
sources exist (`crm`, `education`, `meshweaver-content`, `plugins`, `reinsurance`, `socialmedia`).

**Arithmetic on those inputs — not measured:** 6 sources × ≈ 11 min ≈ **66 runner-minutes per
identity** → ≈ **20 runner-hours/day** at 18 identities, ≈ **47 runner-hours/day** at 43.

**Measured CI cost, same 24 h window, `MeshWeaver.Reinsurance`, counted BY JOB** (a re-run hides
executions; one run carries several jobs):

```
91 "Reinsurance Plugins CI" runs → 45 success, 13 cancelled, 33 failure
33 failed jobs:
   23  Gate: every upstream must be published for that identity   ← this mechanism
   10  Bake … --seed  (Ifrs17 content gates: 7 render:, 3 tests:) ← unrelated
    0  the ext-modules module-set assertion (#3732's gate)
```

All 23 read `crm — no sealed publication under prebuilt-bundles/<identity>/crm`; `plugins` was also
missing in 13 of them; the 23 spanned **18 distinct identities**; all 23 were `repository_dispatch`.

## The options, costed

| option | cost | what it breaks | what it leaves unfixed | wrong if |
|---|---|---|---|---|
| **1. Bake per live identity** | ≈ 20–47 runner-h/day *(arithmetic)* | nothing structurally | **Convergence is doubtful:** at 43 merges/day the identity moves every ≈ 33 min, while a two-level bake chain is ≈ 22 min *plus* a ≈ 48 min image build. The wake arrives for an identity already superseded — which is the 23/23 pattern above. | identity churn is reduced first. It is a multiplier on a rate that option 3 would cut ~8×. |
| **2. Consumer resolves a *compatible* identity** | cheapest to write | 🚨 **the most dangerous.** "Compatible" is what the identity already encodes. A bundle manifest carries the identity *string*, not the surface *set*, so a consumer cannot verify compatibility — only assume it. Wrong assumption ⇒ `MissingMethodException` / `TypeLoadException` at NodeType execution: a runtime fault in the portal instead of a declined bundle. | everything — it converts a loud decline into a silent mis-binding | sound only if bundles carried the full surface manifest. They do not. |
| **3. Make the identity content-derived** | **larger than it looks** — see the falsification test: all 26 surface hashes plus the 17 MVIDs, in a targets file every host imports, needing byte-exact agreement on both sides | risks forking bake-vs-image identity during rollout — the exact failure the current design prevents | nothing, if it lands: **≥ 38 of 43** daily identity moves carry no content reason | reference assemblies carry other per-build variation. The control says they do not: only the sha varied. |
| **4. Decouple the bake from the identity** | publish content-addressed, with identity as a compatibility *attribute* a consumer can evaluate | needs the same surface-set-in-manifest work option 2 lacks, done honestly | — | — |

**What this page does not do:** pick one. Option 3 attacks the cause and today's measurement made it
more expensive than advertised, not less necessary; option 1 is a multiplier on a rate that 5 of 43
merges justify; option 2 should stay off the table while manifests carry no surface set.

## What landed, and what did not

A future reader should not have to reconstruct which half of this shipped.

| | state |
|---|---|
| **The gate's three refusals are distinguishable**, and the resolved identity + its origin are logged on every path | **LANDED** (#4084). Log quality only: every refusal still exits 1, nothing is advisory, no retry, no fallback. |
| **4b — resolve the identity from the newest SEALED set** instead of a moving tag | **NOT DONE, still open.** It is a resolution change, and the measurement below shrank its target. |
| Options 1–4 above | **not started.** Roland's call. |

### Why 4b shrank

The failures were classified by dispatch type (`display_title` carries it). **Measured, same 24 h
window, `repository_dispatch` runs of Reinsurance Plugins CI, with denominators:**

| dispatch type | runs | failures |
|---|---|---|
| `meshweaver-framework-released` | **45** (23 fail, 13 cancelled, 9 success) | **23** |
| `meshweaver-upstream-published` | **35** (10 fail, 25 success) | **10** |

Of the **23 gate** failures, **20 were `framework-released`** and 3 were `upstream-published`; the 10
bake failures split 3 / 7. Phase 1 of the CI refactor drops `meshweaver-framework-released` from the
satellites' trigger types — so it removes **20 of the 23 gate failures and ~45 runs/day** by a route
that has nothing to do with 4b.

What remains for 4b, by trigger path: `upstream-published` and `push`/`pull_request` resolve from a
wake payload and a repo pin respectively — both already **settled**, so 4b does not apply. Only the
**`schedule`** path resolves a moving release tag, which is the one the gate's own text says never
converges (*"the schedule poll does NOT retry this identity… waiting for the poll to clear this is
waiting for something that never [comes]"*). Measured failures on that path: **0 of 2** runs — n=2,
far too small to justify urgency, though phase 1 promotes the daily cron to the primary full run.

### 🚨 Phase 1 removes the REPORT, not the CONDITION

This is the part worth not rediscovering. Those 20 failures were the system *telling* us that no
upstream was baked for the new framework identity. Dropping the release wake stops the telling; it
does not bake anything. **A portal that rolls onto such an identity still finds no sealed bundle and
still compiles every NodeType locally at runtime** — the cost stage ④ exists to remove. The daily
cron catches the condition within 24 h instead of the release wake catching it immediately.

That is a real trade and it may well be the right one — 45 runs a day is real money, and a 24-hour
detection window is tolerable for a backwards-compatible platform. But it is the same shape as
making a gate advisory: a loud CI red becomes a quiet runtime compile. It should be chosen
knowingly, not arrived at, which is why it is written down here rather than left in a thread.

## A related scope question, recorded so it stops being re-litigated

#3583's requirement 3 asks to *"announce a version when it is actually READY on the portal"*. The
mechanism that shipped (#3844, `BuildDeliveryHold.EventOf`/`Notify`) announces a **NodeType**, on a
**hold transition**, into the **`Admin/_Notification`** bell — so on a portal where nothing entered a
hold it emits nothing at all, which is why two threads disagreed about whether it exists.

The shape the requirement describes is a **state**, not an event: *"package X, version V, adopted for
identity I, on this replica"*. `DeploymentReportService` already carries `FrameworkIdentity` and
`AdoptedFrameworkIdentities`, so a `DeploymentReport` field is close to hand. Note that this is the
**same shape that would answer the 23 CI failures above** — the gate's question, *"is `crm` sealed for
`<identity>`?"*, is a readiness query against a published fact.

## Method

Read-only. Public `/health` and `/api/version`; GitHub REST counted by job with full 40-char shas;
`git log` path-filtered against a first-parent control; three local Release builds and a PE metadata
diff. No mesh write, no registry, no cluster.

🚨 **Three counts in drafts of this analysis were wrong.** All three are the same shape — an
instrument that answers confidently on a population it never saw — and they are recorded because the
page's whole argument is a set of counts:

1. `zsh` does not word-split an unquoted variable, so `git log -- $PATHS` passed one giant path and
   returned a confident `0`. Caught by a "do these directories exist" control.
2. A `sed` range that did not match produced an **empty** path list, which `git log` reads as "no
   filter" and answers with *every* commit — a false 43. Caught by comparing against the
   total-merge control.
3. The full-MVID set was extracted from an arbitrary `sed -n '105,172p'` window of the pinning test,
   which **cut the last four entries**: the set is **17**, not 13. Caught in review on #4082. The
   window was replaced with an `awk` range that terminates on the block's own closing `];`, and the
   dependent count was re-run — it is **unchanged at 5**, because none of the four (`Reflection`,
   `ServiceProvider`, `ShortGuid`, `Utils`) was touched in the window, and all 17 were present in
   the falsification build and moved. So the conclusion held while the denominator did not, which is
   exactly why the denominator has to be stated rather than trusted.

The counts that survived re-measurement under `bash`, with validated path lists and a total-commit
control, are the ones above. **`ContentSurfaceAssemblies` is 26** — re-checked by counting lines that
are exactly an entry, and confirming no quoted assembly name appears in a comment inside the block.

## Reading

- *Bundle Delivery Stages* (PR #4079) — the four stages and which issue owns each
- [CI Content Bake](../CiContentBake) — §"an identity nobody baked"
- [Module Adoption Policy](../ModuleAdoptionPolicy) · [Rolling Update Build Tolerance](../RollingUpdateBuildTolerance) (#4071)
- [Publication Seal Starvation](../PublicationSealStarvation) (#4063)
