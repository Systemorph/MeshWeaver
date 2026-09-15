---
nodeType: Markdown
name: Framework Identity Churn
category: Architecture
description: >-
  Why the framework identity moves on every core commit rather than on every content change —
  measured, with the falsification test that killed the obvious fix — what that churn costs CI today,
  and the costed options for #3583's bake-side half.
icon: /static/NodeTypeIcons/box.svg
---

# Framework Identity Churn

The framework identity is the directory name a prebuilt bundle lives under
(`<root>/<identity>/<source>/…`), so a consumer that resolves an identity nobody baked does not find
stale bytes — it finds **nothing**. [Bundle Delivery Stages](../BundleDeliveryStages) calls this
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
| **4b — resolve the identity from the newest SEALED set** instead of a moving tag | 🚨 **ALREADY IMPLEMENTED — it was never open**, and it does not remove the red it was expected to. Measured below (*Second correction*): the satellites resolved the newest **platform-sealed** set on the `schedule` path from at least 2026-09-10, two days before this row was first written, and a `schedule` run that did exactly that still reddened at the upstream gate. What is left on that path is the CONDITION, i.e. options 1–4. |
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

#### 🚨 Correction (2026-09-13): 4b did not shrink — it became the PRIMARY path

The two sentences above are both true and the conclusion drawn from them is wrong, and the refutation
is the subordinate clause at the end of the second one. 4b applies to exactly one trigger path, the
`schedule` poll. Phase 1 **promoted that poll to the primary full run**. So 4b went from covering a
residual path with n=2 to covering *the* path, in the same change that was read as shrinking it.

The `0 of 2` was measured on the `schedule` path while it was still a sideline — a rate measured under
a regime that the same change ended. It is not evidence about the path's failure rate now, and it was
the only number behind "far too small to justify urgency".

So the standing summary needs amending: **4b is the one part of stage ④ that needs no decision from
Roland**, and its target grew rather than shrank. What it is blocked on is *validation*, not a call:
while [#4142](https://github.com/Systemorph/MeshWeaver/issues/4142) has every satellite's publish-bake
failing at preflight (66 of 66 jobs from 2026-09-12T18:11Z), "the newest sealed set" resolves against
a population that has not moved, so a resolver written against it cannot be exercised. Sequence 4b
after #4142, not after the decision.

#### 🚨 Second correction (2026-09-13, 22:xxZ): 4b was ALREADY IMPLEMENTED, and it does not remove the red

Both sections above argue about 4b's **target** and neither checked whether 4b's **subject** was
already built. It was. Three readings, each of which could have falsified this and did not:

**1. The satellites resolved the newest platform-SEALED set on `schedule` before this row was
written.** `MeshWeaver.Manufacturing`'s `ci.yml` at `e050bb1994` (the copy in force on 2026-09-10)
and `MeshWeaver.Crm`'s at `ea933f597` (2026-09-10T06:10Z) both carry a step named, literally,
**"Resolve the newest retained sealed platform set"**, running `scripts/resolve-platform.py` on every
trigger, with `--wait-for-seal 900` added on `schedule` and `repository_dispatch`. The row claiming
"NOT DONE" was written on 2026-09-12 09:21Z, two days later. Core's own reusable lanes got the same
canonical resolver on 2026-09-12T14:14Z (`39caeed993`), for callers that pass empty digests.

**2. The control: a `schedule` run that resolved exactly that way, and reddened anyway.**
`MeshWeaver.Manufacturing` run **34433928561**, event `schedule`, 2026-09-10:

```
03:39:39Z  job "Platform pins name one build"
           main-cd #8240 (core e29d40aad) = 3.0.0-ci.8240: sealed, images resolved by tag
           set=3.0.0-ci.8240
           plugins-sealed=absent
           source=the newest sealed platform set — 1 newer run(s) passed over, see the log
03:41:56Z  job "test-repos / Gate shard 1/1"
           framework-identity: MATCH — '/portal' resolves sc7894b5af0202eca75c4394987c96477
03:41:58Z  ##[error]upstream 'plugins' has no SEALED publication at https://memex.meshweaver.cloud
           for framework identity sc7894b5af0202eca75c4394987c96477 … This run's event was schedule.
```

So 4b's mechanism was in force, on the path 4b is about, and the gate red is unchanged.

**3. Why it cannot help, stated as a mechanism rather than as a measurement.** 4b resolves the newest
set whose **PLATFORM** is sealed. The gate asks whether this repository's **UPSTREAM** has a sealed
publication *for that set's identity*. Those are two different facts, and the resolver's treatment of
the second is deliberately narrow rather than absent: a set whose `plugins` seal is **still running**
IS passed over (or waited for on a release trigger — the measured Manufacturing 2026-09-10 case), while
a seal that is **terminal or absent** — FAILED, SKIPPED, CANCELLED, or simply never made — does *not*
hold the set back. That is stated in `resolve-platform.py` and it is why `plugins-sealed=absent` is
**printed by the resolver itself** in the run above and the set is taken anyway: the lane's upstream
fetch is where that becomes RED, by name. The rationale is sound — a Plugins-side red must not pin the
whole fleet to an old platform — and it means no resolution change reaches this case: the publication
the gate wants **does not exist for any recent identity**, which is stage ④'s condition, options 1–4.

**What made it look open, and it is the instrument's own sentence.** Both lanes print, in the
"Upstreams not ready" step summary and in the `::error` beside it, *"the `schedule` poll … re-resolves
a **moving tag**"*. That wording dates from 2026-09-08 (`c6161c0a96`), predates the resolver, and is
the sole textual basis for "only the `schedule` path resolves a moving release tag". Its *conclusion*
survives — the poll does resolve a different set next time, so it never retries this identity — but
its *mechanism* does not, and reading the mechanism as a defect put an implemented item on the open
list twice. Corrected in `node-repo-gate.yml` and `node-repo-publish-bake.yml` in the same change as
this section.

🚨 **The stronger reading is not a bigger 4b — it is already REFUSED, by name.** "Resolve the newest
set for which every declared upstream has *also* published" is implementable — the resolver already
prints the datum it would branch on — and [CI Content Bake](../CiContentBake) rules it out in those
words: *"Do NOT 'fix' this by having the poll walk back to the newest identity that HAS a complete
publication. It reads like a narrowing and it is a fallback: an upstream that stops publishing would
leave every dependent's poll green forever."* It also contradicts the rule quoted at the top of
`resolve-platform.py` (maintainer, 2026-09-12: *"for compile always find latest package of platform
and plugins"*), since satisfying an upstream means taking an **older** platform set. So the honest
disposition is that nothing on the resolution side is open at all: what is left is options 1–4.

### Re-measured 2026-09-13 on a fresh window — the rate went UP, the ratio did not move

Independently derived over **2026-09-12T19:02Z → 2026-09-13T19:02Z**, `git log --first-parent` on
`origin/main` with per-merge diffs taken as `<merge>^1..<merge>` (a plain `diff-tree` on a merge
commit prints nothing, which reads as a confident zero):

| | this window | the window above |
|---|---|---|
| merges to `main` | **58** | 43 |
| touching `src/` at all | 35 | — |
| touching any of the 26 content-surface projects | **23** | 17 |
| identity moves (one per merge, by the mechanism) | **58** | ≥ 18 observed, 43 by mechanism |
| **moves with no content reason** | **35 of 58 (60 %)** | ≥ 38 of 43 (88 %) |

The ratio is of the same order and the *rate* is **35 % higher**: 58 identities a day against 43. The
three premises behind it were re-checked on `bb65e1bdbe` and all hold — `Directory.Build.props` still
lets the SDK append the commit to `AssemblyInformationalVersion`; `FrameworkBuildIdentity` still
hashes reference assemblies over `ContentSurfaceAssemblies` with `FullMvidAssemblies` contributing
full MVIDs; and `MeshWeaverSurfaceManifest.targets` still sits at the **repository root**, imported by
every host, which is what makes option 3 fleet-coordinated rather than local.

Read against the options table: option 3 is *more* justified than when it was costed (the churn it
removes is larger), and option 1's convergence problem is *worse* (at 58 merges/day the identity moves
every ≈ 25 min against a ≈ 22 min bake chain plus a ≈ 48 min image build). Neither conclusion changes
sign; both get sharper. The decision is still Roland's.

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

### Measured one day later (2026-09-13): the report did not stop either, and the bake stopped

Phase 1 merged on 2026-09-12. Counted by job over 2026-09-12T12:00Z → 09-13T07:08Z:

- **The wake is still per build.** Core CD's `plugins-bake` registers a `plugins` publication on
  every core build (20 of 20 register jobs), and the control instance fans
  `meshweaver-upstream-published` to every satellite that declares `plugins`: Crm 23, SocialMedia
  24, Manufacturing 23 runs in 19 h, one per core register-publication; Education 42
  `framework-released` on top (Education#320 unmerged). The event type changed; the run count did
  not. Detail and the per-repo table: [The Release Wave](../TheReleaseWave) → *After phase 1*.
- **Reinsurance, the repository measured above, received 0 wakes** — so the 23-of-33 gate reds
  did stop there, but not by the mechanism phase 1 intended. Its four publish-bake jobs in the
  window (3 `push`, 1 `schedule`) all failed: two at the upstream gate (`crm` unsealed for
  `s1765294…` and `s759c3e6…` — the condition, on a `push` trigger now), two at a NEW preflight.
- **That preflight is #4142**: #3760 (18:11Z) made `bundle-registry-publisher-password` an input
  of the reusable lane, no satellite provisions `MW_REGISTRY_PUBLISHER_PASSWORD` or passes it, and
  **66 of 66** satellite publish-bake jobs started at or after 18:20Z failed there — the five
  03:xxZ daily runs included (22 of 24 had succeeded before the merge). So on 2026-09-13 the
  condition this page is about is not partial but total: **no satellite has sealed anything for any
  identity since 18:11Z**, and every portal rolling onto a set from that window compiles all
  satellite content locally.

The two are different facts and are recorded separately on purpose — one is a broken lane, the
other a design expectation that did not hold.

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

- [Bundle Delivery Stages](../BundleDeliveryStages) — the four stages and which issue owns each
- [CI Content Bake](../CiContentBake) — §"an identity nobody baked"
- [Module Adoption Policy](../ModuleAdoptionPolicy) · [Rolling Update Build Tolerance](../RollingUpdateBuildTolerance) (#4071)
- [Publication Seal Starvation](../PublicationSealStarvation) (#4063)
