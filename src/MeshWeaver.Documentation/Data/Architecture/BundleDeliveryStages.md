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
> and was fixed by #4002 the following morning.

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
