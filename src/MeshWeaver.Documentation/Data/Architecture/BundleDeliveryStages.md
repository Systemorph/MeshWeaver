---
nodeType: Markdown
name: Bundle Delivery Stages
category: Architecture
description: The four independent stages between a merge and a portal serving prebuilt bytes — write, compose, select, deliver — which defect lives at each, and why one symptom (a portal compiling what the registry should have served) has four separate causes that must not be triaged as one.
icon: /static/NodeTypeIcons/box.svg
---

# Bundle Delivery Stages

A portal that compiles a NodeType locally, when a bundle for it exists somewhere, produces **one
symptom with four unrelated causes**. Between 2026-09-06 and 2026-09-09 that symptom was filed four
times — #3461, #3732, #3768, #3583 (+ #3845) — and every thread then re-derived the same shared
vocabulary before discovering it was about a different stage. This page is the map that makes that
unnecessary.

> 🚨 **They are not duplicates and must not be collapsed.** They are four consecutive stages of one
> pipeline. A fix at one stage cannot close a defect at another, and — measured repeatedly — a
> *reading* taken at one stage was routinely attributed to another. That misattribution is the
> expensive part: #3583's 2026-09-11 re-measurement cited `bundle_adoption 0 of 25
> FrameworkDeclined` as proof its own requirement was unmet, when that reading was stage 3's defect
> and was fixed by #4002 **twenty-nine minutes later** (comment 06:44Z, merge 07:13:45Z).

## The pipeline

```
 merge ──▶ ① WRITE ──────▶ ② COMPOSE ─────▶ ③ SELECT ───────▶ ④ DELIVER ──▶ portal serves
           who may mutate   what goes into   which publication  do source and
           a prefix         a publication    is served to whom  bytes arrive in order
```

| stage | the question | the defect | issue |
|---|---|---|---|
| ① **Write** | may two publishers mutate one prefix at once? | two lanes write `prebuilt-bundles/<identity>/plugins` and an overlap seals a mix | [#3461](https://github.com/Systemorph/MeshWeaver/issues/3461) |
| ② **Compose** | what does a publication *contain*? | a downstream republishes its upstream's modules, and ships siblings the image already has | [#3732](https://github.com/Systemorph/MeshWeaver/issues/3732) |
| ③ **Select** | which publication answers *this* caller? | the registry answered with its OWN identity, not the caller's | [#3768](https://github.com/Systemorph/MeshWeaver/issues/3768) — **fixed** |
| ④ **Deliver** | do source and bytes arrive in order? | sources track HEAD; bundles are baked once per identity, so source runs ahead of bytes | [#3583](https://github.com/Systemorph/MeshWeaver/issues/3583) → [#3845](https://github.com/Systemorph/MeshWeaver/issues/3845) |

Each stage has its own instrument, and reading the wrong one is how the attributions went wrong:

| stage | the instrument that answers for it |
|---|---|
| ① | the publish lane's own `verify_publication` postcondition, and a MIX refusal in the job log |
| ② | the `ext-modules` composition assertion (#3905) — *and its printed denominator* |
| ③ | `/health` → `bundle_adoption`, the `FrameworkDeclined` kind specifically |
| ④ | `BuildProvenance.StaleAdopted` on the NodeType record; the CI gate *"every upstream must be published for that identity"* |

## Why the identity is what couples them

Every stage is keyed on the **framework identity** — SHA-256 over the sorted
`(name, reference-assembly hash)` pairs of the platform image's `ContentSurfaceAssemblies`. It is a
function of the **image**, and it carries no content commit. Two consequences drive all four
defects:

1. **It moves with nearly every platform build.** Measured 2026-09-11/12 (see below): **18 distinct
   identities in 24 hours**.
2. **It is the directory name.** `<root>/<identity>/<source>/…` — so a consumer on a new identity is
   not "slightly behind", it is looking in a directory nobody has written.

That is the shared vocabulary. It is *not* a shared defect.

## Stage ③ is fixed — and this is the clean, closed case

The registry stamped the bundle index with `FrameworkMvid = FrameworkMvid` — its **own** identity —
and `PluginBundleClient.Adopt` compared it once and declined the whole index before requesting any
package. A consumer one image ahead of its registry therefore adopted nothing, however well the
bundles for its identity had been sealed.

#4002 made the consumer state its lane on the index request and the registry answer for that lane
when it holds a sealed publication for it (its own identity otherwise, so a genuine bake gap still
reads as a bake gap). `src/MeshWeaver.PluginCatalog/PluginBundleClient.cs` now sends
`?identity=…&arch=…` on the fetch.

**Measured on memex.systemorph.com:**

| | 2026-09-11 06:11Z (before) | 2026-09-12 08:0xZ (after) |
|---|---|---|
| adoption attempts | 25 | 25 |
| assemblies adopted | **0** | **25** |
| `FrameworkDeclined` | **25** | **0** |

Both replicas agree. `FrameworkDeclined` is absent from the payload entirely.

### The follow-on: an empty bundle is not a miss

With the identity gate no longer masking it, a second reading surfaced — 13 of those 25 attempts
reported `NoAssemblies`, rendered as *"content the registry was meant to serve is compiled here
instead"*. Every one of the thirteen (`AI`, `Anthropic`, `AppleIntelligence`, `Maps`, `AzureBlob`,
`AzureFoundry`, `Chat`, `Mcp`, `Notifications`, `OpenAI`, …) is a **module package**: it declares no
NodeTypes at all, its module lands correctly through the separate `ModuleLandingService` path, and
nothing whatever is compiled in its place.

🚨 **"This package has no NodeTypes" and "this package's NodeTypes failed to arrive" are different
sentences** — the exact conflation `BundleAdoptionKind` was split up to prevent, reappearing one
value further along. `BundleAdoptionKind.NothingToAdopt` now separates them, and
`BundleOffering.ClassifyEmpty` decides from a **positive declaration** in the manifest (the package
says it ships a module, or content) and never from the absence of assemblies — so a producer that
ships a genuinely empty archive stays `NoAssemblies`, and stays loud.

Two consequences beyond the wording:

- `bundle_adoption` no longer reads **Degraded for ever** on a portal with nothing wrong with it.
  The health check consumes `ledger.Misses` directly, so the flip needs no change in the host.
- On a `Modules:RequirePrebuilt` mesh, a module-only package previously threw
  `PrebuiltRequiredException` and **failed the install of a correctly delivered package**. That flag
  forbids a silent fallback to compiling; a package that compiles nothing has no fallback to forbid.

The ledger's sentence keeps the denominator visible — `… no misses (13 carried no NodeTypes to
adopt)` — because *"25 attempts, 25 adopted, no misses"* would invite the reader to conclude 25
packages were served.

## Stage ④ is the one that costs CI today

**Measured 2026-09-11T08:20Z → 2026-09-12T07:51Z on `Systemorph/MeshWeaver.Reinsurance`, counted by
JOB, zero API errors, every run in the window accounted for:**

```
91 "Reinsurance Plugins CI" runs   →  45 success, 13 cancelled, 33 failure
33 failed jobs, classified by failed STEP:
   23  Gate: every upstream must be published for that identity
   10  Bake (compiler --output), then gate a mesh against it (--seed)
```

The 23 are stage ④ in CI rather than on a portal:

```
release availability: 1 of 2 source(s) are not available for framework identity sf0f9ef77…
  • crm — no sealed publication under prebuilt-bundles/sf0f9ef77…/crm
```

- `crm` missing in **23 of 23**; `plugins` in 13 of 23.
- **18 distinct framework identities** across the 23 — each wake asks about a new one.
- All 23 were `repository_dispatch`, i.e. the `meshweaver-upstream-published` wake.

The gate is correct and its message is accurate (*"This is a WAIT on the upstream, not a defect in
this repository"*). What the number shows is the **shape**: nothing bakes for the identities that
are live, so a consumer that resolves a new identity finds an empty directory, and the wake that is
supposed to rescue it arrives for a *different* identity than the next one it will resolve.

The 10 remaining failures are Reinsurance content-gate failures (7 `render:`, 3 `tests:`, all on
`Ifrs17/*`) and belong to none of these five issues.

## Stage ② — what the guard sees, and what it does not

> **2026-09-13 — defect 1 fixed at the lane.** The `ext-modules` step of `node-repo-publish-bake.yml`
> keeps this repository's own module bundles and the ones composed from an upstream's seal in two
> directories; both are composed into the compile surface and both take part in the one-build
> verdict, but only the own ones are staged for `modules/_index`. A satellite that owns no module
> seals an EMPTY index, which `publish-bake-bundles.sh` accepts only when the workflow exports
> `SEAL_MODULES_OWN=0` (a workflow that exports nothing is still the pre-#2707 skew, refused).
> Replayed on Crm's and Education's 2026-09-12 composition (four upstream packages, no own module):
> before, `sealing MeshWeaver.AI as module package 'AI'` ×4 and a four-entry index; after, four
> `composed only: … NOT in this publication's seal` lines and `sealed set: 0 own module bundle(s)
> …; 4 upstream cop(y/ies) … NOT sealed`. Core's `plugins-bake` (four own artifacts, no upstream)
> is unchanged: `sealed set: 4 own …; 0 upstream`. The guard's denominator is still the COMPOSED
> set (4 on those repositories) — the population the bake loads — and pointing it at a
> publication's ~40 module bundles remains the module-pack lane's change.


🚨 **#3905's assertion was green in all 10 of those bake jobs — over a denominator of 4:**

```
module set: 4 MeshWeaver.* assembly file(s) across 4 bundle(s), 4 distinct name(s),
            0 carried by more than one bundle, 0 carried at more than one BUILD
```

The 15-copies / 3-builds population #3732 measured lived across a publication's ~40 *module*
bundles, which the `ext-modules` composition step does not see. **The guard is real and prints its
denominator honestly; it is not yet pointed at the population that had the defect.** Reading its
green as "stage ② is clear" is the denominator error this fleet keeps making.

🚨 **The log trap that makes this worse:** in a GitHub Actions log the `echo "module set: $COPIES …"`
line (the script text) appears *before* the executed output line carrying the numbers. Grepping for
the words alone matches the echo and reports the assertion as present — or as failing — in runs
where it never ran. Match on the digits.

## Stage ① — the claim that changed

#3461 was filed as *"an overlap seals a mix **no reader can detect**"*. That is no longer true of the
whole window. `publish_one_target` now runs `verify_publication` between the last content upload and
the seal: every uploaded file is stamped with a per-run `publication` token, all stamps are re-read,
and a set carrying another run's token is **refused, with the foreign seal removed**.

What remains is a genuine residual, and the script states it against itself:

> the interval between the last verification read and the `_complete` upload. A publisher that
> overwrites a file inside that one-upload window still lands under this run's seal.

Two further corrections that the thread has outgrown, both worth recording because each was acted
on as if true:

- **"One owner per prefix" would not have helped.** Three independent measurements (09-06, 09-08,
  09-10) found **zero cross-lane contention**; all 4 observed concurrent publications on one prefix
  were **same-lane**. The premise that both lanes address one directory is confirmed (7 of 19
  prefixes, with different source commits) — but the race that actually happens is in-place mutation
  by *either* lane, which only the generation layout removes.
- **The retention sweep is NOT dead.** `AddPrebuiltBundleRetention` was reported as having zero
  callers; it is registered at `memex/Memex.Portal.Shared/MemexConfiguration.cs`. The search covered
  `src/` and `test/` and this repository ships code from **three** roots. (#3963, closed.)

The landing-order defect recorded on that thread — a later-finishing publication with *older* content
displacing a newer landed module — was fixed by #3996: `ModuleLandingService` compares versions
before moving the head, and `ModuleUpdateDecision` answers `SkipOlder`.

## Re-adjudicated 2026-09-13, after the daily-wave refactor

The five issues were re-read against `main` (`cdb2878bb`) the day after the satellites moved to the
daily wave (Plugins#1707/#1709, core #4085). What each stage looked like that morning, with the
instrument and its denominator:

| stage | reading, 2026-09-13 | verdict |
|---|---|---|
| ① write (#3461) | `verify_publication` read over every `plugins` publish job of both lanes, 2026-09-12T08:00Z → 09-13T08:00Z, by job: core `plugins-bake` **25** executed (23 sealed, 2 converged-skip), Plugins `publish-bake` **10** (8 sealed, 1 converged-skip, 1 *"a later run sealed first — not sealing backwards"* skip). **62 of 62** target verifications read `N/N file(s) hold this run's bytes … 0 byte-identical from another`; **0** MIX refusals, 0 superseded, 0 verify-incomplete. 20 distinct identities; **6 written by both lanes, all 6 with different Plugins commits**, hours apart — the routine same-prefix/different-content case, with no overlap in the window. Phase 4 unreachable from every producer: `publish-bake-bundles.sh:203` and `node-repo-publish-bake.yml:335` default `flat`, and none of the seven callers (core, Plugins, five satellites) passes `publication-layout`. 🚨 Two things the same logs showed: the never-seal-backwards guard was a **no-op in the core lane** — 5 of 25 core re-seals logged `the compare API refused to order …: gh: Not Found (HTTP 404)` → `republishing as before`, because the publish step compared with core's own `github.token`, which cannot read the private Plugins repo (fixed the same day: the step now uses the content repository's App token, the checkout's own expression); and the two lanes seal **different module sets** for one identity by construction (core 4 modules / 45 files, Plugins 5 / 46 with `defaultviews`), so "same content" across lanes is never byte-identical. | open — phase 4/5 unlanded |
| ② compose (#3732) | `/health` ×10 per portal: `pending_module_activation` absent on all **5** distinct replica payloads (3 cloud + 2 systemorph) — half 1 met for the second day; `content-types` Degraded on all 5 with **different** sets per replica (`SocialMedia/Post ×31` dark on cloud replicas A and B, fine on C), while the node record reads `AdoptedVerified`, one MVID, fingerprints equal — the per-record/per-replica gap #4071 names. The guard's denominator is still **4** (`node-repo-publish-bake.yml:1398`). 🚨 The thread's "defect 1 is #3760" was wrong: #3760 (merged 2026-09-12T18:11Z) publishes to `cr.meshweaver.cloud`; composition is decided at `node-repo-publish-bake.yml:1383-1384` and `:1519-1521`, where a downstream's seal still carries byte copies of its upstream's modules. | open — defect 1 structural, guard population, half 2 |
| ③ select (#3768) | `bundle_adoption` on memex.systemorph.com, both replicas: 24–26 attempts, 21–25 adopted (13 carried no NodeTypes), **1 miss** `LearningRoadmap: NoAssemblies` — a producer-side empty archive, correctly loud; `FrameworkDeclined` absent. | closed, stays closed |
| ④ deliver (#3583 → #3845) | The wave removed neither half. The **report** moved: core CD's per-build `plugins` registration wakes every satellite via `meshweaver-upstream-published` (Crm 23 / SocialMedia 24 / Manufacturing 23 runs in 19 h; Education 42 `framework-released` on top, Education#320 unmerged; Reinsurance 0) — [The Release Wave](../TheReleaseWave) → *After phase 1*. The **condition** became total: since #3760 (18:11Z) every satellite publish-bake fails at the lane's preflight on the unprovisioned publisher password — **66 of 66** jobs, the five daily runs included (#4142) — so no satellite has sealed anything for any identity since. #3845's five holes all hold (`SealedSyncGate.cs:56-57`, `ModuleDiscoveryService.cs:694`, `GitHubActionArea.cs:86`, `GitHubSyncSettingsTab.cs:407`, #4063). | both open — the bake-side decision (identity churn options) is still Roland's |

Two neighbours were adjudicated in the same pass and are recorded where they belong: #3878 (the
whole-run registry hand-over) is **closed** — it landed, was verified three times in production, and
was reversed nine hours later by the per-module-deploy directive
([Module Publication Gate](../ModulePublicationGate) carries the dated supersession); #3842 /
Plugins#1565 (no platform pins) are **delivered** — zero pin literals and zero sha-pinned lane refs on
all six repositories' `main`, with the resolver-copy drift (seven copies, six sizes) as the one
residual, on [The Release Wave](../TheReleaseWave).

## How to triage the next one

1. **Name the stage before naming the issue.** The four instruments above answer different
   questions; a reading from one is not evidence about another.
2. **Quote the denominator with every zero.** A green guard over 4 names and a green guard over 40
   are different claims. `search` is RLS-filtered and `/health` is per-replica — say which replica,
   and how many distinct payloads you saw.
3. **Count CI by job, not by run.** One run carries several jobs and a re-run hides executions.
4. **Read the executed line, not the script echo.**

## Reading

- [Sealed Publication Reads](../SealedPublicationReads) — stage ① and ③, the read answers and the seal
- [Sealed Publication Generations](../SealedPublicationGenerations) — the layout that removes stage ①'s residual
- [CI Content Bake](../CiContentBake) — what produces a publication; §"an identity nobody baked"
- [Module Adoption Policy](../ModuleAdoptionPolicy) — stage ④ on a live portal
- [Module Owned Siblings Ride](../ModuleOwnedSiblingsRide) — stage ②, one name one build
- [Rolling Update Build Tolerance](../RollingUpdateBuildTolerance) — one identity per NodeType record (#4071)
- [Publication Seal Starvation](../PublicationSealStarvation) — when stage ④'s gate never releases (#4063)
