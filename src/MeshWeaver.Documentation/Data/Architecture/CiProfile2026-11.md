---
Name: CI Profile, November 2026
Category: Architecture
Description: "Measured CI profile of core (MeshWeaver) and MeshWeaver.Plugins over 3.25 days (2026-11-12 12:00Z to 2026-11-15 18:00Z, REST only, about 1,800 runs with their jobs). Runner queues are not the bottleneck: the waits are the CI dispatch hold in Plugins and the serial job chains. The ranking puts first the core shell self-tests that hold every core build for 5 minutes, the combo verifier that spends 1,700 runner-minutes a day mostly on runs that fail by timeout, the gate in Plugins that recompiles 123 NodeTypes on every run, and the dispatch hold in Plugins that adds about 7 minutes before admission."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 3v18h18"/><rect x="7" y="12" width="3" height="6"/><rect x="12" y="8" width="3" height="10"/><rect x="17" y="5" width="3" height="13"/></svg>
---

# CI profile, November 2026

This page holds the measurement and a ranked list of proposals. **Nothing here changed a workflow, a
branch protection rule, a runner set or any infrastructure.** The previous profile is MeshWeaver.Plugins
[`Hosting/CiProfile.md`](https://github.com/Systemorph/MeshWeaver.Plugins/blob/main/Hosting/CiProfile.md)
(24 hours ending 2026-11-07 ~21:00Z). Section 7 compares the two.

Related pages: [Staged Pull Request Pipeline](../StagedPullRequestPipeline) ·
[Runner Pools and Dispatch Queues](../RunnerPoolsAndDispatchQueues) ·
[Core Test Selection](../CoreTestSelection) · [Sharding the Node-Repo Gate](../NodeRepoGateSharding) ·
[Build Scope Narrowing](../BuildScopeNarrowing) · [Reading CI Signals](../ReadingCiSignals).

## Method

- **Window:** runs *created* 2026-11-12 12:00Z to 2026-11-15 18:00Z (3.25 days). For Plugins, `main`
  is v4 and moved to net11 at 2026-11-15 12:06Z. `main3` is v3. Cohorts are split on that boundary
  where it matters.
- **Source:** GitHub REST only. Run listing per workflow
  (`actions/workflows/{id}/runs?created=a..b`), jobs and steps per run
  (`actions/runs/{id}/jobs?filter=all`), and annotations for a 30-job sample
  (`check-runs/{id}/annotations`). 31 job logs were read for timing lines, at least 1 s apart. No
  GraphQL was used. The control instance's `Hosting/Builds/...` records were read through MCP.
- **Jobs fetched** for the heavy workflows: core `MeshWeaver Build and Test` (260 runs),
  `Continuous Delivery (main)` (293), `Combo verification` (199), `Template feed` (199),
  `control-always-latest` (156). For Plugins: `Plugin Catalog CI` (524), `Portal image rebuild` (137)
  and `Shadow trunk` (3). The light workflows (review, stage advance, retry, arm) are counted from the
  run listing only. Each job takes about 0.5 min of wall time.
- **Definitions.** *Queue* is a job's `created_at` to `started_at`. GitHub creates a job once its
  `needs` are met, so this is pure runner wait. *Run* is `started_at` to `completed_at`.
  *Runner-minutes* is the sum of job run times. *To required-green* is the run's `created_at` to the
  completion of the last required context the run produces. Core requires `Consolidate test results`.
  Plugins requires the 8 contexts its branch protection lists, plus the review check.
- 🚨 **One trap was caught while measuring.** The Plugins dispatcher re-runs a held pull-request run
  with *re-run failed jobs*. GitHub then lists attempt 1's successful jobs again under attempt 2. The
  copies have a new `created_at`, but they carry the ORIGINAL `started_at` and `completed_at`, so
  `started_at < created_at`. A naive sum counts them twice: before the fix it read 8,852 runner-minutes
  a day for all Plugins PR runs, against 6,974 after it (`main` 6,232 + `main3` 704 + 38 for 4 runs
  against the feature base `feat/net11-v4`). Every number below excludes the copies. The same
  trap makes `run_started_at` useless as "when attempt 2 began" (it can precede attempt 2's first
  job by about 9 minutes), so the hold is measured from job timestamps.
- **Counts that do not add up exactly.** A run's ✔ / ✘ / cancelled can sum to one less than its
  run count when that run was still in progress at listing time (conclusion `null`): one push to
  `main` in Plugins, one core PR run and one core push. "Plugins PR runs" (384) also includes the 4
  runs against `feat/net11-v4`, which section 1 does not list. Path medians use the runs whose final
  attempt has executed jobs, so a path's *n* can be one below the ✔ count (core: 85 of 86).

## 1. Headline numbers

| Repository · workflow · event | Runs | ✔ / ✘ / cancelled | To required-green, median / p90 | Runner-min / day | Runner-min on failed + cancelled runs (window total, 3.25 d) |
|---|---|---|---|---|---|
| **Plugins** · CI · PR → `main`, heavy (gate ran) | 69 of 357 | (all ✔) | **54.2 / 90.6 min** | — | — |
| **Plugins** · CI · PR → `main`, light | 101 of 357 | (all ✔) | 19.5 / 38.0 min | — | — |
| Plugins · CI · PR → `main`, all | 357 | 191 / 80 / 86 | — | **6,232** | 7,180 (35 %) |
| Plugins · CI · PR → `main`, heavy, after net11 | 7 | (all ✔) | 55.5 / 72.4 min | — | — |
| Plugins · CI · PR → `main3`, heavy | 13 of 23 | 17 / 0 / 6 | 59.8 / 118.9 min | 704 | 91 |
| **Plugins** · CI · push `main` | 131 | 33 / 20 / 77 ¹ | 68.2 / 121.0 min | **3,976** | 4,999 |
| Plugins · CI · repository_dispatch + schedule | 9 | 7 / 2 / 0 | 61 min | 715 | 433 |
| **Core** · Build and Test · PR → `main` | 136 | 86 / 27 / 22 | **18.4 / 21.7 min** | **2,370** | 2,001 |
| Core · Build and Test · PR → `main3` | 38 | 26 / 5 / 7 | 20.7 / 21.9 min | 750 | 508 |
| Core · Build and Test · push (`main` + `main3`) | 86 | 81 / 3 / 0 | 6.2 (light) / 19.9 (heavy) | 985 | 163 |
| **Core** · Continuous Delivery (all events) | 293 | 127 / 108 / 56 | 26.4 min (dispatch, ✔) | **2,825** | 3,895 |
| **Core** · Combo verification | 199 | 99 / 66 / 34 | — | **1,733** | 5,625 (≈ all) |
| Core · Template feed, control-always-latest | 355 | 341 / 14 / 0 | — | 115 | 12 |

¹ The 77 cancelled push runs are the concurrency group replacing its pending run. They cost **345**
runner-minutes, almost nothing, but each push waits for the run ahead of it (section 3).

**Volume:** about 115 Plugins PR runs a day (206 on 11-07), 54 core PR runs a day (31 on 11-07).
Core's GitHub-hosted jobs run in a public repository, so they bill nothing, but they still count
against the concurrency ceiling. Plugins runs on the fleet's own AKS runner pools.

## 2. Runner queue is not the bottleneck — the dispatch hold and the job chain are

| Repository | Runner label | Jobs | Queue median / p90 / p99 / max | Jobs over 2 min |
|---|---|---|---|---|
| Plugins | `aks-silos` | 15,439 | **0.25 / 0.52** / 1.6 / 4.9 min | 100 |
| Plugins | `aks-silos-dind` | 3,090 | **0.42 / 0.87** / 8.7 / 17.3 min | 93 |
| Core | `ubuntu-latest` | 11,162 | 0.03 / 0.05 / 0.1 / 2.0 min | 1 |
| Core | `ubuntu-24.04-arm` | 99 | 0.07 / 0.08 / 0.1 / 0.1 min | 0 |

The waits that matter are elsewhere:

- **The Plugins dispatch hold** (policy `ci-two-pools-priority-queues`,
  [Runner Pools and Dispatch Queues](../RunnerPoolsAndDispatchQueues)). 264 of 384 Plugins PR runs
  reached attempt 2 or later. In a sample of 30 admission failures, 27 read `HELD FOR DISPATCH (queue
  ci)` and 3 read `STAGE 2 HELD (stage 0 red)`. From the failed attempt-1 admission to attempt 2's
  first job, the hold took **median 4.0 min, p75 11.0, p90 22.1, max 241.9** (n=257). In heavy runs
  the first attempt ends at minute 10.7 (median), but admission starts only at minute **17.9** (p90
  50.7). The hold therefore adds about **7 minutes** to the median heavy PR and much more to its tail.
  Its runner cost is small, because attempt 1's successful jobs are re-used.
- **The `main` push concurrency group in Plugins.** A push run's first job starts **17.8 min** after
  the run is created (median, p90 67.8), because `main` runs are never cancelled and wait for the run
  ahead. The time from merge to `Register the publication with memex` is about **92 min** median.
- **Core CD:** the first job of a dispatched CD run starts **8.5 min** after the run (median, p90
  19.6). That is the CD concurrency group.

## 3. Critical paths

### Plugins pull request, heavy (the Tests-area gate ran), `main`, before net11, n=69

The minute is counted from the run's `created_at` and includes the hold. Each row is a median over
the runs in which that job ran.

| Start → end | Job | Note |
|---|---|---|
| 0.7 → 3.5 | `validate / Validate node repos` | |
| 1.9 → 5.5 | `Repo policy gates` | 3.3 min run (6.0 on 11-07) |
| 1.6 → 6.3 (p90 17.1) | `Classify the change set` | p90 run is 10.3 min. The ABI probe step carries the tail (1,019 min in the window, median 0) |
| 6.8 → 8.3 | `Select compiled validation and publication scope` | end of stage 0 |
| **→ 18.0** | `Admitted by the build queue` (attempt 2) | **the hold: about 7 min after stage 0 ended** |
| 19.3 → 21.1 | `Module bundles / Select the affected bundles` | |
| 21.4 → 22.9 | `Warm the shared build environment` | 1.6 min (3.4 on 11-07) |
| 23.3 → 29.3 | `Build the workspace (one global build)` | 8.2 min median run over all runs |
| 30.1 → 32.6 | `Module bundle` legs (single and batch) | |
| → **34.5** | `All selected bundles built` | the gate's inputs need the whole lane |
| 35.2 → 36.0 | `test-repos / Plan the gate's shards` | |
| **37.1 → 51.7** | `test-repos / Gate shard 1/1` | **14.6 min**, the last long job on the path |
| → 54.2 | `Tests-area ratchet over the gate log` | last required context |

Off the path: `portal-hosts` unit build (→ 27.9) and its test legs (→ 37.3), then `Build + test the
portal hosts` at 37.7. The compile-check chain (`Assemble the compile-check reference set` 36.2 →
`compile-check` 38.8 → `Compile every NodeType` 42.0) and `Compile Code nodes + the in-mesh lane`
(→ 40.4) are also off it.

**Inside the gate shard** (logs `132901787289` on v4 and `132900718420` on v3):
`bake: compiled 123 NodeType(s) in 336.8 s wall (1576.1 s of compile work, max-parallel=6)`, and
`122 … in 351.9 s (1689.1 s)` on v3. So **5.6–5.9 of the gate's 14.5 minutes are a NodeType
recompile**, run on every gated head. The four modules the gate composes
(`MeshWeaver.AI`, `Markdown.Collaboration`, `Maps`, `Payments.Stripe`) arrive `package=unstamped`
with a fresh MVID each run, so a cache keyed on module identity cannot hit. The gate always ran as
one shard (156 of 156). The previous profile measured sharding at no saving.

### Core pull request → `main`, n=85 successful

| Start → end | Job |
|---|---|
| 0.1 → 0.2–0.6 | the eleven body and policy gates (`Pull-request body`, `Cross-repo pair`, `Interface additions`, …) |
| 0.1 → 3.1 | `Clients workflow verdict` (a 3-minute poll of another workflow) |
| 0.1 → 4.4 | `CI's own shell` |
| 0.1 → **5.6** | `CI's own shell (executions)`, the slowest need of `stage-gate` |
| 5.7 → 5.8 | `stage-gate / Stage gate: may the suites start` |
| 5.8 → 6.0 | `Build executor (host or Actions)` |
| 6.1 → 9.3 | `Build solution (once)` (3.3 min) |
| 9.3 → **17.9** | `Run tests (shard k)`, 8 shards |
| 17.9 → 18.5 | `Consolidate test results` (required) |

**Nothing heavy starts until the shell self-tests finish: 5.7 of 18.5 minutes (31 %).** On 11-07
that wait was 5.0 minutes.

### Core CD, dispatched, n=20 successful

The first job runs at 8.5 (concurrency wait). The images build from 10.4 until `Build + push control
image` ends at **25.4**. That job is the long pole: 17.0 min median run, against 4–10 min for the
other images. Then come promote (27.6), the satellite compatibility compiles (29–32.6), the bake
(30.8) and the arm (33.7).

## 4. Test shards and slowest suites

**Core, 136 successful runs, always 8 shards:**

| Shard | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 |
|---|---|---|---|---|---|---|---|---|
| Median run (min) | 6.3 | **9.6** | **9.1** | 4.8 | 8.6 | 5.0 | 5.6 | 7.9 |

Per run, the slowest shard is 1.29× the median shard (p90 1.87), and the spread from max to min is
5.2 min. The mean shard is 7.1 min, so a perfect balance would end the test stage about **2.5 min
sooner**. Core's logs print no `[timing]` lines. They print `[CI] <project> exit=N elapsed=Ns` per
project, plus `TEST CLASS INIT` / `DISPOSE` markers. From all 8 shards of run `43742571208`:

- **Slowest projects:** `MeshWeaver.Hosting.Test` 276 + 326 s (two halves), `Graph.Test` up to 221 s,
  `FaultInjection.Test` 163 s, `Messaging.Hub.Test` up to 110 s, `Data.Test` 90 s.
- **Slowest classes** (summed INIT→DISPOSE seconds): `AHungImportCannotHoldTheRebootTest` 215,
  `ShutDownPhaseStallVerdictTest` 161, `DisposalStallWatchdogTest` 101, `OrleansCompileActivityAccessTest` 71,
  `ABakeCompilesTheDefinitionItResolvedTest` 67, `EventSubscriptionExactPathDeletionTest` 64,
  `AOneShotSignalOutlivesTeardownTest` 55, `DurableStreamConsumerTest` 54,
  `AWarmUpSweepRerunsWhenTheStoreRecoversTest` 52, `LifecycleRequestsAlwaysReachAVerdictTest` 49.

**Plugins legs:**

| Leg family | Runs | Legs / run | Slowest leg (median) | Median leg | Ratio |
|---|---|---|---|---|---|
| `portal-hosts (network-* · leg k/N)` | 59 | 9 | 12.3 min | 9.6 | 1.27 |
| `Module bundle (batch k/N)` | 79 | 7 | 3.4 | 2.6 | 1.23 |
| `Module tests (…)` (excluding `build once`) | 40 | 8 | 6.8 | 4.3 | 1.56; the slowest is `MeshWeaver.AI` in 31 of 40 |

From the `[timing]` lines of the 9 network legs of run `43734535974` and the 8 module-test legs of
run `43706214929`:

- **Slowest suites (wall):** `MeshWeaver.AI.Test` 306 s, `Fleet.Control.Test` 3 × 250–278 s,
  `Blazor.Views.Test` 2 × 236–272 s, `Hosting.Monolith.Test` 3 × 226–242 s, `Memex.Portal.Shared.Test`
  231 s, `Hosting.PostgreSql.Test` 2 × 214–220 s.
- **Slowest classes** (class-seconds): `Blazor.Views.Test.MapMarkersBindingTest` 234,
  `ButtonPendingStateTest` 182, `Fleet.Control.Test.OperatorPagesRenderTest` 164,
  `GridTemplateColumnVisibilityTest` 148, `UserActionSubmissionFromViewsTest` 146,
  `Memex.Portal.Gui.Test.PlanGatedAppAcceptanceTest` 132, `MarkdownLateExecutableBlockTest` 111,
  `StreamOwnerGoneBindingTest` 109, `ContentEditorDeletedNodeTest` 109,
  `AI.Test.CrossHubPatchAtomicityTest` 96.

`Blazor.Views.Test` holds 7 of the 10 slowest classes (1,759 class-seconds in total).

## 5. Where the runner-minutes go, and repeated work

**Plugins `Plugin Catalog CI`, all events, top jobs (3.25 days):**

| Job family | Runs | Run median / p90 | Total min | Per day |
|---|---|---|---|---|
| `portal-hosts` (unit builds, unit legs, network build, network legs) | 1,509 | 6.5–12.0 | **11,925** | **3,670** |
| `Module bundle` (batch and single legs) | 952 | 2.6 / 14.2 | 5,033 | 1,549 |
| `Module tests` (build once, batches, AI) | 481 | 4.0–10.4 | 2,706 | 833 |
| `test-repos / Gate shard 1/1` | 185 | 15.4 / 18.3 | 2,459 | 757 |
| `Classify the change set` | 439 | 3.0 / 10.3 | 1,897 | 584 |
| `Repo policy gates` | 421 | 3.3 / 3.7 | 1,374 | 423 |
| `Build the workspace` | 213 | 8.2 / 11.9 | 1,358 | 418 |
| `validate / Validate node repos` | 439 | 2.5 / 3.0 | 1,117 | 344 |
| `RN app + web clients` (11.0 min of it is *Web export smoke + E2E*) | 107 | **15.1** / 16.8 | 1,086 | 334 |
| `Compile Code nodes + the in-mesh lane` | 223 | 4.3 / 6.7 | 796 | 245 |

**Repeated work measured:**

- **Generic setup in Plugins** (`Set up job`, `actions/checkout`, `setup-dotnet`, the platform mount,
  downloads and restores) totals 7,657 min: **2,356 per day, 14.2 min per run**. `Set up job` alone is
  2,812 min over 21,481 job starts (41 jobs per run, 0.12 min each).
- **`scripts/git-full-history.sh` runs in 6 jobs of every run** (classify, validate, repo gates,
  select scope, select compile-check units, settle locks): 1,333 min, **about 410 per day**.
- **NodeType compiles happen three times per gated head.** `Compile Code nodes + the in-mesh lane`
  (4.3 min), `compile-check` (1.8 min median per unit) and the gate's bake (5.6–5.9 min) compile
  overlapping sets of NodeTypes against the same reference set.
- **`portal-hosts` unit builds:** `Build the unit's network` is 6.9 min median × 358 legs, and the full
  network build is 9.4 min × 73. Together that is **about 790 runner-min/day of compile**. Each unit
  (for example `Fleet.Control.Test`, `Hosting.PostgreSql.Test`) builds its own closure, so shared
  projects are compiled once per unit. Every network test leg spends about 2 min on setup
  (9.6 min leg against a 7.6 min test step).
- **Core:** setup is small, 994 min in total (306 per day, 4.6 min per run). The repeated cost is
  the **`CI's own shell` family**: 2,835 min (**872 per day**) on every PR run, whatever the PR
  changes. Next is `Clients workflow verdict`, a 3.1 min poll on every run (**257 per day**).
- **Combo verification:** each of 7 slots builds the verifier itself (2.2 min × 7 per run, about
  **340 per day**).

## 6. Waste

- **Plugins PR:** failed runs 5,515 min (27 %), cancelled 1,665 min (8 %; it was 34 % on 11-07, so the
  authored-change supersede works). The first failing job in the 81 failed PR runs was: `validate` 23,
  `Repo policy gates` 14, `portal-hosts` 13, `Module tests` 11, `Select the affected bundles` 9, gate 3.
  Cheap gates an author can run locally account for 37 of the 81.
- **Plugins push `main`:** 20 failed runs cost **4,654 min** (255 min median each). The first failing
  jobs were spread out: `Module bundle` 5, `validate` 3, settle locks 2, publish-bake 2, portal-hosts 2.
- **Core CD:** 108 failed runs. The first failing job was `Compatibility input: the published module
  set` in 31, `Resolve the target commit` in 27, `Arm the fleet` in 23 and `CD delivered` in 17.
  The 31 compatibility-input failures come **after** every image has been built: the
  `workflow_run` failures alone cost 1,131 min (69 min each).
- **Combo verification:** 5,300 of 5,632 runner-minutes were on failed runs. 89 slot jobs ran past
  30 min (max 39.4). One read (`132432200108`): a 25-minute `GATE_TIMEOUT`, then the verdict
  `NotVerifiable`. The same slot fails the same way run after run (slot 4 first-failed in 48 runs,
  slot 1 in 15). *Correction, measured while implementing R2:* that slot's six retired packages did
  not cause the verdict. A retired package is reported and folded out of the verdict. The caveat
  that decided it was "the gate run exceeded what the bake left of the run's 00:25:00 budget": the
  candidate did not complete inside the gate. See
  [Combo Gate Wiring](../ComboGateWiring) for what R2 changed and what it could not.
- **Re-runs:** genuine re-runs (attempt 3 or later) were 24 Plugins PR runs and 1 core run. Attempt 2
  is the dispatcher, not a person.
- **The 45-minute cap:** no CI job neared it. Only `Shadow trunk — follow the host's modules stage`
  (43.2–43.4 min, 3 of 3 runs, all failed) and five combo slot jobs (38–39 min) came within 7 minutes.
- **HELD FOR DISPATCH:** about 270 holds in the window. Each costs about 0.3 runner-min plus the wall
  time measured in section 2.

## 7. Build host and Actions

- **Every host build is still `shadow: true`.** The Actions legs give the verdict. The control
  instance held about 200 `Hosting/Builds/*` records in the last 27 hours (113 Plugins, 54 core, the
  rest satellites). Since 2026-11-12, 97 Plugins host builds ended `Succeeded`, and most of them were
  `ci/settle-manifest-locks` and documentation heads. Today a host build is extra work on top of
  Actions, not a replacement for it.
- **The net11 cut-over broke the Mac host for v4.** Since 2026-11-15 ~12:00Z every host build of a
  `main` (v4) head has failed in its `modules` stage before running:
  `NETSDK1045: The current .NET SDK does not support targeting .NET 11.0`, while the host built the
  shelf from the default branch. Example: `Hosting/Builds/MeshWeaver.Plugins/39e3d85e…` (#5527),
  also #5528, #5525, #5524 and #5522. `main3` heads still succeed (#5520). The warm-cache fill fails the
  same way (`MWPREP003`).
- **Plugins #4478, owner decision (b):** compile-check and gate move to the host together once the gate
  shows parity. The parity read of 2026-11-14 ~09:30Z did not prove gate parity. **That does not change
  the ranking today:** nothing moved, the host adds no saving, and v4 parity cannot be measured until
  the host SDK targets net11. If the gate moves later, proposal R3 matters even more, because the
  host's gate would repeat the same 5.6-minute recompile.
- **Core** runs entirely on Actions in the window. `Host shard results` and `Host build outputs` were
  skipped in all but 7 runs.

## 8. Ranked opportunities

These are estimates from the window. *Runner-min/day* are the minutes the change would stop spending.
*PR wall* is the change in median time to required-green for the PRs it touches. None of these is
implemented here.

| # | Change | Evidence | Runner-min / day | PR wall |
|---|---|---|---|---|
| **R1** | **Core: take the `CI's own shell` family off `stage-gate`, and run it only when its inputs change.** Let `stage-gate` need the cheap body and policy gates only. The four shell jobs still run in parallel, red the run and cancel the heavy legs on a red, which keeps stage 0's fail-fast purpose. Then path-select them in `precheck` on what they actually test (`.github/**`, `scripts/**`, the composite actions, the canonical scripts satellites fetch). | Section 3 core: `stage-gate` at 5.7 of 18.5 min. 2,835 min of shell jobs in 273 runs. E.g. run `43742571208` | up to **~870** (depends on the share of PRs that touch CI inputs; not measured) | **−5.2 min** (18.4 → ~13.2), every core PR |
| **R2** | **Combo verification: decide `NotVerifiable` before the 25-minute wait, and build the verifier once per run.** A verdict that depends only on (candidate, the instance's installed set) can be cached and re-used until either changes. A retired package is known before any gate starts. | Section 6: 5,300 of 5,632 runner-min on failed runs, 89 slot jobs > 30 min, log `132432200108`. Verifier built 7× per run (2.2 min each) | **~1,000–1,400** | none (not on a PR path) |
| **R3** | **Plugins gate: stop recompiling 123 NodeTypes per run.** Key the bake's compile cache on the composed modules' public API (the ABI key the module lane already computes), not their MVID. Or reuse the NodeType assemblies `Compile Code nodes + the in-mesh lane` / `compile-check` produced in the same run against the same reference set. | Section 3: `bake: compiled 123 NodeType(s) in 336.8 s` (`132901787289`), `122 … 351.9 s` (`132900718420`). The gate shard is the last long job of every heavy PR | **~320** (5.7 min × 185 shard runs), more if compile-check shares it | **−5 min** on heavy PRs (54 → ~49) |
| **R4** | **Plugins dispatch: admit a PR run in place when the `ci` queue has capacity, and hold (red, then re-run) only when it does not.** A trunk run already waits in place for `MeshWeaver/admission`. Alternatively, enqueue at the run's FIRST job so the wait overlaps stage 0 instead of starting after it. | Section 2: hold median 4.0 / p90 22.1 min (n=257). Heavy runs: stage 0 done 10.7 → admission 17.9 (p90 50.7). Sample annotation: 27 of 30 `HELD FOR DISPATCH (queue ci)` | ≈ 0 | **−4 to −7 min** median, about −20 min at p90, every Plugins PR |
| **R5** | **Core: rebalance the 8 test shards** by measured project weight (`measured-weights.tsv` is already written per shard). Move `Hosting.Test`'s halves and `Graph.Test` apart from the 9-minute shards 1, 2 and 4. | Section 4: shard medians 4.8–9.6 min, mean 7.1 | 0 | **−2.3 min**, every core PR |
| **R6** | **Plugins stage 0: one job instead of six for the shell-only checks** (classify's selectors, select scope, select compile-check units, settle locks). Each repeats set-up, checkout and `git-full-history.sh`. | Section 5: `git-full-history.sh` 1,333 min over 6 jobs per run, setup 0.25 min per job | **~400–650** | −0.5 to −1 min (less fan-in before admission) |
| **R7** | **Plugins `portal-hosts`: give the unit builds the module lane's content-addressed compile cache** (unchanged project, same reference set → reuse its output). The 13–14 small networks and the big network each rebuild shared projects today. | Section 5: about 790 runner-min/day of unit and network builds; 3,670/day for the whole family | **~300–400** | −3 to −5 min on light PRs, where portal-hosts is the path; ≈ 0 on heavy PRs |
| **R8** | **Core CD: fail before the images.** Run `Compatibility input: the published module set` and the target-commit decision before the six image builds, not after them. | Section 6: 31 runs first-failed at compatibility input after the images were built. `workflow_run` failures 69 min each | **~300–450** | none (CD) |
| **R9** | **Plugins `RN app + web clients`: path-select the Playwright web E2E** (11 min of a 15.1 min job, 4.5 min on 11-07) to client changes. | Section 5: 107 runs, 1,086 min | **~250–330** | none (off the required path) |
| **R10** | **Core `Clients workflow verdict`: read the Clients check once at `Consolidate` time instead of polling for 3 minutes in every run.** | Section 5: 3.1 min × 273 runs | **~250** | 0 |
| **R11** | **Build host: install a net11 SDK on the Mac host (and pin the host's SDK to the repository's `global.json` line)** before any v4 routing or parity read. | Section 7: every v4 host build since 12:00Z fails with `NETSDK1045` | 0 today (host builds are shadow) | unblocks #4478 |

**Ranked by PR wall-clock:** R1 (core, −5.2), R3 (Plugins heavy, −5), R4 (Plugins, −4 to −7 median
and −20 at p90), R7 (Plugins light, −3 to −5), R5 (core, −2.3).
**Ranked by runner-minutes:** R2 (~1,200), R1 (≤ 870), R6 (~500), R8 (~400), R7 (~350), R3 (~320).

Together, R3 and R4 take a heavy Plugins PR from about 54 to about 43 minutes. R1 and R5 take a
core PR from 18.4 to about 11 minutes.

## 9. Compared with the 2026-11-07 profile

| Measure | 2026-11-07 (24 h) | 2026-11-12..15 (3.25 d) | Change |
|---|---|---|---|
| Plugins PR runs per day | 206 | ~115 | −44 % |
| Plugins PR runner-min per day | 10,645 | 6,936 (`main` + `main3`) | −35 % |
| Plugins PR runner-min on cancelled runs | 34 % | 8 % | supersede only on authored changes ✓ |
| Plugins `Repo policy gates` run | 6.0 min | 3.3 min | §8 #3 of the old page landed ✓ |
| Plugins `Warm the shared build environment` | 3.4 min | 1.6 min | §8 #4 landed ✓ |
| Plugins `Build the workspace` | 9.4–10 min | 8.2 min | compile cache, partly |
| Plugins `Gate shard 1/1` | 13.6 min | 15.4 min | **worse** (the NodeType recompile, R3) |
| Plugins heavy PR to required-green | 48.3 min (from `run_started_at`) | 54.2 min (from `created_at`, includes the hold) | **the dispatch hold is new** (R4) |
| Plugins `RN app + web clients` | 4.5 min | 15.1 min | **new Playwright E2E** (R9) |
| Plugins push `main` runner-min per day | 3,739 | 3,976 | ≈ |
| Core PR to required-green | 19 / 21 min | 18.4 / 21.7 min | ≈ |
| Core PR runner-min per day | 1,572 | 3,120 | **×2.0** (54 runs a day against 31; 57 min per run against 51) |
| Core time held by the shell self-tests | 5.0 min | 5.7 min | **worse** (R1) |
| Core CD runner-min per day | 1,950 (dispatch + workflow_run + schedule) | 2,825 | +45 %; 108 of 293 runs failed |
| Combo verification | not profiled | 1,733 per day | **new** (R2) |
| Runner queue (Plugins) | 0.2–0.4 min median | 0.25–0.42 min median | unchanged; still not the bottleneck |

## What this does not establish

- **How many PRs touch CI inputs** (R1's runner-minute saving). PR file lists were not read.
- **Per-package gate times.** The gate log prints the bake's total and the install order, but no
  per-package duration. The rest of the gate's 14.5 minutes after the bake is not broken down here.
- **The post-net11 cohort is small** (7 heavy Plugins PRs). The "no change" reading is weak.
- **Class rankings come from one run each** (core `43742571208`; Plugins `43734535974` and
  `43706214929`). They name the heavy classes, not a stable order.
- **Light workflows** (review answered, stage advance, retry, arm, merged-tree guard) are counted from
  the run listing. A few 6-hour listings exceeded GitHub's 1,000-result cap. Their run counts are
  floors, and their runner-minutes were not fetched (each run is under 1 min).
- **Build-host stage timings.** Records were read as a list, plus one in full. Host stage durations
  were not compared per head, and the parity instrument (`scripts/build-parity-report.py`) was not run.
- **Billing.** Core runs in a public repository on GitHub-hosted runners, where minutes are free.
  The core runner-minutes here measure capacity, not cost.
