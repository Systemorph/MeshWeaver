---
Name: Platform and Module Deploy
Category: Architecture
Description: The platform deploy and the module deploy are two separate deliveries. Every successful platform build goes to the control instance first, and the fleet is offered it only after control runs it. Modules publish and auto-update on their own lanes, never through a platform build, seal or pair. This page covers what guards each delivery, the alarm that keeps control on the latest build, the coupling that was measured and removed, and the migration order.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="8" height="16" rx="1"/><rect x="13" y="4" width="8" height="7" rx="1"/><rect x="13" y="13" width="8" height="7" rx="1"/></svg>
---

# Platform and Module Deploy

> *"after platform we always deploy control"* · *"we separate platform deploy 100% from module deploy"*
> · *"control will always be on latest platform and will always update to latest modules."*

Three policies cover this. The register is [Policy Not Prose](../PolicyNotProse):

| policy | the rule |
|---|---|
| `platform-deploy-control-first` | Every platform build that passes the control image's own acceptance becomes the control instance's next image, with no approval, in the run that built it. The fleet is offered the build only after control is running it and healthy. |
| `platform-module-deploy-separate` | The platform deploy ships the host image only: the core platform plus the boot-time infrastructure modules. It does not pack, bake or seal any module, and no module verdict gates it. Modules publish on their own lanes, and every instance updates them on its own (`packages-auto-update`). |
| `control-always-latest` | The control instance runs the newest platform build it was given and the newest compatible version of every module it has installed. If it falls behind on either for longer than a declared bound, an alarm names the lag. |

## The two deliveries

```
PLATFORM (core main-cd)                               MODULES (each module's own lane)
core merge ─► gate ─► images ─► promote               module merge ─► pack ─► test ─► publish
                 │                                    (MeshWeaver.Plugins ci.yml, satellites)
                 └─► control image ─► acceptance ─► control-promote        │
                                                    │                       ▼
                                                    ▼             registry: newest version + floor
                              control-first: memex-control:<version>        │
                                                    │                       │  every instance, on its
                          control self-update rolls to it (no approval)     │  own schedule:
                                                    │                       │  newer version published
                                   control runs it, /health 200             │  AND floor ≤ running
                                                    │                       │  ⇒ lands + activates
   arm (any later run): ladder green + control runs it                      │  (live, or one restart)
                                                    │                       │
                         memex-portal-ai:<version>, release event           │
                                                    ▼                       ▼
                         the fleet rolls the platform              the fleet updates modules
```

Neither side waits for the other. A platform roll keeps the module bytes an instance already runs.
This is the ladder in policy `platform-backwards-compatibility`. A module update never waits for a
platform build, seal, pair tag or framework identity.

## What guards a platform roll now

The fleet's arming lives in `arm-promoted-set.py`, function `judge`. It reads three things, and
none of them comes from a module lane:

1. **The platform's own tests.** A set is promoted only from a commit whose required check is
   green. This is `gate` in `main-cd.yml`.
2. **The compatibility ladder** (`platform-ladder-compat`). It takes the module set the fleet
   runs and links it, unchanged, against the promoted portal image, checking types, assembly
   versions and every member by signature. The bytes are the published ones: `published-modules`
   reads them from the sealed `plugins` publication for this run's identity, and never builds
   them. A platform change that breaks a module's declared compatibility turns this step red. The
   fix is to restore compatibility or declare an epoch bump, never to re-bake.
3. **Control first.** The control instance named in `.github/control-instance.json` must be
   running a build that contains the set's core commit. Containment is checked by ancestry, using
   the GitHub compare API on its public `/api/version`. Control's `/health` must also answer 200.

How each state is read:

- A ladder that is still running is **waiting**.
- A red ladder is **refused**.
- A missing ladder job, or several jobs answering to the ladder's name, is **refused**.
- A control that has not taken the set yet is **waiting**. So is one whose `/health` is not 200, and one whose state cannot be read.
- A newer green set supersedes all of these.
- No reading ever counts as a pass when there was silence.

On the module side, two things still guard what an instance runs:

- **Floors.** A module whose declared floor (`minMeshVersion`) is above the running platform is
  declined by name, and nothing else is blocked (`package-min-mesh-version`). A module that needs
  a newer platform therefore holds nothing back on the platform side.
- **Ceilings.** Policy `platform-backwards-compatibility` lets a declared break set a ceiling on
  the affected modules.

## Control first, in practice

- `control-first` (`main-cd.yml`) tags `memex-control:<version>` and moves the line pointers. It
  does this for every build that passed `control-acceptance` (empty database, a broken NodeType
  row, and N−1 on the migrated database), in the same run, and never moves the pointers backwards.
  It needs `control-promote` and does not need `arm`.
- The control record (Systemorph/Memex `mesh/Deployments/control.json`) is Continuous on
  `3.0.0-ci*` and carries **no `rollGate`**. Its self-update therefore hands the roll to the
  control lane, and the routed `Roll` runs unattended: `ActionsExecutor.AdmittedUnattended`
  requires a Continuous policy, a matching pattern and `rollGate == null`, and the Memex
  classifier's `is_continuous_ci_roll` applies the same rule.
- **The minimum governance that remains:** a roll **along the record's own Continuous line** is
  unattended. That is the exception that already existed for every working instance. Any other
  roll of control still needs a mesh approval: a different tag, a rollback, or a `RollBack` or
  `SetEnv` through the break-glass `control-recovery` lane.
- The record also declares `moduleUpdatePolicy: Auto`, so control takes every newest compatible
  module (`packages-auto-update`).

## The alarm (`control-always-latest`)

**Platform half.** The core workflow `.github/workflows/control-always-latest.yml` runs every 30
minutes and calls `arm-promoted-set.py control-lag`. It finds the newest promoted set whose
`Deploy control first` job succeeded, and when that job ran, then checks whether control's running
commit contains that set. The results:

- **ok**: control contains the set, however long ago it was given.
- **converging**: control does not contain it yet, and the build was given inside
  `platformLagBoundMinutes`. This is reported, not red.
- **lag**: control does not contain it past the bound, control's state cannot be read, or control
  runs it but `/health` is not 200 (the arming offers the fleet nothing from an unhealthy control,
  so that state blocks every roll). The
  check goes **RED**, and the one open `control-lag` issue is commented with control's commit, the
  newest build and how long ago it was given. The next green reading closes the issue.

**Module half.** This half runs in MeshWeaver.Plugins as `FleetTarget.ControlBreaches` (Hosting).
It reads the control instance's own module inventory report. A module whose served version is
newer than the installed one is a breach once it has been behind for longer than the bound. This
applies when the served version's floor is at or below control's running platform, and it does
not matter whether a hold reason exists. A module held because its floor is above control's
platform is the floor working as designed, not lag. Each breach is filed through the existing
`FleetTargetIntake` as a `fleet-behind-target` triage item that names the module, both versions
and the hours behind.

## What was measured before the change

The measurements were taken on the live system. They are evidence and are not to be updated.

**Coupling points that were in `main-cd.yml`:**

| job / step | what it coupled |
|---|---|
| `gate` → "Is `plugins` sealed for the identity this set resolves?" (`plugins_seal_due`) | a platform reconcile re-attempted a MeshWeaver.Plugins seal |
| `plugins-bake-image` | resolved digests for the Plugins bake |
| `plugins-modules` ("Plugins: pack the module bundles the bake composes") | packed AI, Markdown.Collaboration, Maps and Payments.Stripe from Plugins source in core CD |
| `plugins-bake` ("bake + seal the publication for this identity") | re-sealed the `plugins` publication on every platform build, although the identity (`c003e001`) is per epoch, not per build |
| `report-plugins-seal` + the `cd-plugins-seal` ledger | judged the above |
| `arm` → "Mint a MeshWeaver.Plugins token" + `select` | the fleet was armed only on a green Plugins dependent-suites verdict for the exact pair `<core7>-p<plugins7>` |
| `control-arm` | `memex-control:<version>` followed the fleet's arming, so control received a build only after Plugins' verdict, which made control **last** |
| `satellite-compat`, ladder baseline | read the module bundles that core CD packed |
| Memex `control.json` `rollGate: {after: [memex-cloud, memex], soakMinutes: 120, approval: required}` | control rolled last, with an approval |

**How long a platform roll waited on module work.** The figures come from CD run 9965
(`260b3c4`), which promoted at 07:12Z:

- The Plugins pack, bake and seal inside core CD ran from 07:12 to 07:47, which is 35 minutes of
  runner time per platform build. It did not gate the arm.
- The arm itself waited for the Plugins dependent-suites verdict. The six most recent pairs that
  got a verdict wrote it **21–95 minutes after promotion** (66, 95, 50, 79 and 61 minutes, plus
  one at 21). The arm then picked the verdict up only at the next CD run.
- The two newest sets (ci.9963 and ci.9965) still had no verdict about 90 minutes later.
- Control itself sat on the armed ci.9939 (`060afe8`) while three newer sets were promoted.
  Running the new `select` read-only against the live state shows exactly that: every newer set
  is *waiting — control runs 060afe8eb*.

**Image composition.** Measured on `memex-control` `staging-bd3ea73` (linux-x64, 10 layers, 1.20 GB
compressed). The app layer is 472.6 MB uncompressed, of which `modules/` is 251.8 MB:

| module | MB | kind |
|---|---|---|
| MeshWeaver.Hosting.Cosmos | 39.1 | boot-time (storage backend) |
| MeshWeaver.Fleet.Control | 37.8 | boot-time (control only) |
| MeshWeaver.Hosting.Snowflake | 25.7 | boot-time (storage backend) |
| MeshWeaver.Hosting.Instance | 14.5 | boot-time (gates the registry it would arrive through) |
| MeshWeaver.SelfUpdate.Aks | 11.8 | boot-time (control only: rolls itself) |
| MeshWeaver.Blazor.Chat | 23.5 | **not boot-time** |
| MeshWeaver.Mcp | 22.5 | **not boot-time** |
| MeshWeaver.AI | 22.2 | **not boot-time** |
| MeshWeaver.Markdown.Export | 14.0 | **not boot-time** |
| MeshWeaver.Markdown.Collaboration | 13.5 | **not boot-time** |
| MeshWeaver.Blazor.Graph | 13.2 | **not boot-time** |
| MeshWeaver.Blazor.EntityViews | 13.1 | **not boot-time** |

That makes 122.0 MB of the image module seeds that are not boot-time. The registry already
replaces each seed in place when it serves a newer version.

## Migration with no unguarded moment

Every step below keeps the platform roll guarded, and the steps run in this order:

1. **Core: control first, and the platform verdict.** This is in this change.
   - `control-first` tags control on every accepted build.
   - `arm` judges ladder plus control instead of the Plugins verdict.
   - `published-modules` reads the sealed publication in place of `plugins-modules`.
   - `plugins-bake`, `report-plugins-seal` and the gate's seal probe are removed.
   - `control-always-latest.yml` starts alarming.

   No moment is unguarded. The ladder already ran on every build, so it has been part of the
   verdict from the first run. The control half can only make the arm **stricter**: until control
   rolls, the fleet waits and the alarm says so. Because the published-modules artifacts keep the
   `module-bundle-<Module>` names, the satellite baselines and every core pull request's ladder
   (`fetch-deployed-plugin-set.sh`) read them unchanged.
2. **Memex: the control record goes first.** Drop control's `rollGate` and declare
   `moduleUpdatePolicy: Auto`. Until this lands, control-first tags are rolled only with an
   approval, so the fleet waits and `control-always-latest` stays red, naming the lag. That is
   loud, not unguarded.
3. **MeshWeaver.Plugins: the module half of the alarm** (`FleetTarget.ControlBreaches`, the
   carried `BehindSince` clock, and `FleetTargetIntake.AllBreaches`).
4. **Owed — removing the non-boot seeds from the image**, guarded by a boot-time declaration and a
   shrink-only ratchet test ("the image contains no non-boot module"), neither of which is built yet. This follows the live module update
   (`packages-auto-update`, core #6124 and #6121/#6123, Plugins #2893). An instance whose
   `Modules:Required` names a module must be able to land it from the registry before readiness.
   Until #6124's reload lands that, a seed is the bootstrap copy. The ratchet shrinks one entry
   per module as each is proven to land on a fresh instance.
5. **Done — the pair tag `<core7>-p<plugins7>` is retired.** main-cd no longer mints it on
   `memex-portal-ai` (`promote` phase A) or `memex-control` (`control-promote`). See
   "The pair tag, retired" below.
6. **Owed — Plugins' `promotion-candidate.yml` / `core-candidate.yml`.** They still run the
   dependent suites against each promoted pair, and `arm-promoted-set.py pending` still answers
   them, but no verdict they write decides anything. They report only, and they can be retired by
   Plugins.

## The pair tag, retired

`<core7>-p<plugins7>` named the MeshWeaver.Plugins commit a portal image's HOST was built from. After
the separation it answered no delivery question, but three mechanisms still leaned on it. Each now
reads something else:

| What leaned on it | What it reads now |
|---|---|
| `gate`'s completeness probe: a set was "stale" when Plugins `main` had moved at all, so the portal was rebuilt on every Plugins merge (MeshWeaver#4688) | Core's sha only. A host change reaches the image through MeshWeaver.Plugins' `portal-image-rebuild.yml`, which classifies each push (`scripts/portal-image-relevance.py`) and dispatches main-cd with `rebuild: true` only when a changed path can enter the image |
| `arm`'s source for `memex-portal-ai:<version>`: the one tag that names THIS build after a rebuild of the same core commit moves the bare `<core7>` tag | The build's staging tag `staging-<core7>-<run id>`, recorded as `staging` in the promotion record. It is unique per run, and `arm` refuses a staging tag that does not name the selected core |
| Recovering the core commit of the newest ARMED manifest (`arm-promoted-set.py armed-base`), resolving an unarmed set (`resolve-platform.py`), and a tagged release (`release.yml`) | The bare `<core7>` tag or the staging tag on the same manifest. A legacy pair tag is still read, so sets promoted before the retirement resolve unchanged |

The host commit is still recorded: the promotion record keeps `plugins_sha`, and the release event
carries `pluginsSha`. Readers outside core (Memex `scripts/image-contains.py`) answer from the
promotion record first and treat a pair tag as a legacy fallback.

**Consumer sweep before the retirement** (repositories at `origin/main`, the live mesh read through
the control instance):

| Where | Readers of a pair tag |
|---|---|
| core `main-cd.yml` | 4 writers/readers: `promote` phase A, `control-promote`, `arm`'s source tag, `gate`'s probe (via `check-image-set.sh`) |
| core scripts | `check-image-set.sh`, `arm-promoted-set.py` (`armed_commit`), `resolve-platform.py` (portal identity), `release.yml`, `lock-pinned-digests.py` (protects `<core7>-p*` as part of a commit's closure; kept for legacy manifests) |
| MeshWeaver.Plugins | 0 code readers. `portal-image-rebuild.yml` and `portal-image-relevance.py` mention it in comments; `promotion-candidate.yml`, `core-candidate.yml` and `core-release-attribution.py` use the record's verdict KEY `pair-<core7>-p<plugins7>`, which is a record field, not a registry tag |
| Memex | 1 code reader: `scripts/image-contains.py`. Comments and historical notes in `helm-release.yml`, two `values.*.public.yaml` files and `mesh/Deployments/memex.json` |
| Education, Reinsurance, SocialMedia, Manufacturing, Crm | 0 |
| live `Deployments/*` on the control instance (5 `Hosting/Deployment` records) | 0 pins; 1 prose mention in `Deployments/memex` |

No Hosting/Deployment record pins an image tag of either shape, and none may.

## Tests and self-tests

- `arm-promoted-set.py --self-test` covers the following, each with negative controls. A mutated
  `judge` that ignores containment fails four cases.
  - control first: newer-but-not-on-control waits, unhealthy waits, unreadable waits, and unknown
    containment is never read as contained;
  - the ladder: red is refused, running waits, missing is refused;
  - separation: a set is armed with no Plugins verdict in the call at all, and the signatures
    carry none;
  - `control_first`: it takes no arming input, never moves backwards and is idempotent;
  - `control_lag`: ok, converging, lag past the bound, and unreadable counts as lag.
- `test-cd-steps.py` → `separation_problems`, with seven mutation controls. It catches:
  - a module lane back in `main-cd`;
  - a Plugins credential on `arm`;
  - `arm` without the ladder;
  - a module job gating `arm`;
  - `control-first` needing `arm`;
  - an unpaid `always()`;
  - a Plugins checkout outside the host builders.
- `PlatformDeliveryNeverWaitsOnPluginsGuard` (any `plugins-*` job in `main-cd` is a finding, with
  its own mutation) and `NodeRepoLaneHostGuard.ThePlatformDeploy_CallsNoModuleLane`.

## What is not established

- No end-to-end run on the cluster: a platform build actually rolling control unattended, and the
  fleet then arming. This depends on the Memex record change landing and control's self-update
  routing a `Roll` with no `rollGate`. The rule is in the code (`AdmittedUnattended`); the live
  run has not been observed.
- That the sealed `plugins` publication for each identity carries all four module bundles. It did
  for `c003e001` when `plugins-bake` sealed it inside core CD. After this change only
  MeshWeaver.Plugins' own `publish-bake` reseals it. If that lane does not publish for a new epoch,
  `published-modules` is red and the fleet is not armed, which is loud but is a stall.
- `platformLagBoundMinutes` (180) is a starting bound, not a measured one. No unattended control
  roll had been timed when it was set.

## Related

- [One Promotion Gate](../OnePromotionGate): the arming mechanics this page builds on (cursor,
  completion marker, resume, base).
- Module Reload (`Doc/Architecture/ModuleReload`, arriving with core #6124): `packages-auto-update`, the module half of the separation.
- [Module Adoption Policy](../ModuleAdoptionPolicy): floors (`package-min-mesh-version`).
- [Self-Update on the Control Lane](../SelfUpdateControlLane): how a roll reaches control.
- [Module Versioning](../ModuleVersioning): the epoch identity and the ladder.
