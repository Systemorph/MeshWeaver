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
         control-first POSTs self-update-available                          │  own schedule:
            → routed Roll for control (no approval)                         │
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
- **`control-first` also delivers the build.** Its last step signs a `self-update-available`
  announcement for the control record (`arm-promoted-set.py control-announcement`: the tag, the
  image on `memex-control`, and the build's own line `3.0.0-ci*` as the admitting pattern) and POSTs
  it to the control plane's inbox (`vars.CONTROL_WEBHOOK_URL`, signed with
  `secrets.CONTROL_WEBHOOK_SECRET`). CD preflight requires that URL to equal the `url` in
  `.github/control-instance.json` plus `/api/hooks/Hosting/PlatformBuilds`; a URL for another
  portal fails before image publication. MeshWeaver.Plugins `SelfUpdateRouting` resolves the record and
  opens `Ops/Actions/selfupdate-roll-control-<version>-…`. A re-run re-announces the same build,
  which the router dedupes. A build older than control's newest tag is never announced, and the
  router refuses a backwards roll anyway. Any answer other than a stored **and verified** delivery
  turns the job red. 🚨 **A stored and verified delivery is still not a roll.** The inbox answers
  before its router runs, and the router drops an announcement whose `deployment` names no record
  the receiving plane holds. So the job claims only that the announcement was stored; whether
  control RUNS the build is read by `arm` and the alarm (see
  [A frozen fleet is red](#a-frozen-fleet-is-red)).
- The control record (Systemorph/Memex `mesh/Deployments/control.json`) is Continuous on
  `3.0.0-ci*` and carries **no `rollGate`**, so that routed `Roll` runs unattended.
  `ActionsExecutor.AdmittedUnattended` requires a routed roll written by the system, a Continuous
  policy, a pattern that admits the tag on both the announcement and the record, and
  `rollGate == null`. The Memex classifier's `is_continuous_ci_roll` applies the same rule. A
  hand-filed `Roll` is a person's request: it carries no `admittedBy` and always waits for an
  approval. **Unattended means the routed lane, never a hand-filed action.** A hand-filed `Roll`
  that is parked at `AwaitingApproval` also counts as a rollout in flight, so the router opens
  nothing for that deployment until the parked roll is approved or rejected.

### Who rolls control (and why it is CD, not control's own self-updater)

The first version of this design said "control's self-update rolls to it". It never did, and no
build after ci.9939 reached control without a hand-filed, approved `Roll`. Because the fleet is
armed only once control runs a build, nothing reached the fleet either. Three independent defects
stood behind that, measured 2026-10-06 on control.systemorph.com and memex.systemorph.com:

1. **The watcher looked at the wrong repository.** The in-pod self-updater lists
   `SelfUpdate:PortalRepository`, which defaults to `memex-portal-ai`, and nothing renders it for
   control. `memex-portal-ai:<version>` is written only by `arm`, and `arm` waits for control to
   run the build. That is a circular wait: control could only ever follow the fleet.
2. **It could not list tags at all.** Control's `Admin/UpdatePolicy` read
   `check FAILED: CredentialUnavailableException` on every hourly check. A record-provisioned
   instance gets the federated credential (`hosting-control` on the portal identity), but the
   rendered values never carry `selfUpdate.azureClientId`, so the pod has no workload identity.
3. **Its hand-over went to its own inbox.** Control lists `Hosting/PlatformBuilds` with a secret
   and declares no `Hosting:ControlInbox:Url`, so its route is local. Its own mesh holds no
   `Deployments` record (a `namespace:Deployments` search answers 0), so a hand-over there could
   open nothing. `Ops/Actions` on the control plane held `selfupdate-roll-*` actions for memex,
   build and memex-cloud, and none for control.

Defects 1 and 2 are now fixed at the source (Systemorph/MeshWeaver.Plugins#2994), and reach the
pod only with a Reconcile (below). Defect 3 needs no fix of its own while CD's hand-over goes to a
plane that holds control's record — but see
[A frozen fleet is red](#a-frozen-fleet-is-red): once CD's hand-over was
pointed at control's own inbox, defect 3 became the whole outage.

- **The repository comes from the record.** `DeploymentPortalConfig.SelfUpdatePortalRepository`
  and `SelfUpdateMigrationRepository` render `SelfUpdate__PortalRepository` and
  `SelfUpdate__MigrationRepository`. The values are the repository path of the record's
  `imageRepository` / `migrationImageRepository`; a blank `migrationImageRepository` is derived
  the way the operator pairs it — `memex-migration` in the portal repository's directory
  (`ghcr.io/systemorph/memex-portal-ai` → `systemorph/memex-migration`), never the bare default.
  They render only where they differ from the image default (`memex-portal-ai` /
  `memex-migration`), so every other record renders byte-identically.
  An explicit `migrationImageRepository` is REFUSED (`EffectiveMigrationRepository` throws) when
  it is not named `memex-migration` (the only name the operator rolls) or when it is on a
  different registry host from `imageRepository`: the updater lists both repositories on ONE
  registry, derived from the portal image.
  The chart writes each key only when it is non-blank: both have real defaults, and an empty
  repository would roll to `<registry>/:<tag>`.
- **The workload identity is wired where the federation is.** `hosting-deploy` reads the client id
  of `$AZ_PORTAL_IDENTITY` (`az identity show … --query clientId`), the identity `hosting-federate`
  binds the namespace's `memex-portal-sa` to, and passes `--set selfUpdate.azureClientId=<id>`.
  That one value renders the ServiceAccount annotation, the pod's `azure.workload.identity/use`
  label and `AZURE_CLIENT_ID`. The record carries only the identity's NAME
  (`operator.environment`), so the id is read at deploy time and is not stored in the record,
  its vault half or the repository (the step does report it as `portal_identity=<id>`, which the
  operator keeps with the action's output). An unreadable
  identity, absent or refused, stops the deploy before helm. No identity named means none is set,
  and the step says `portal_identity=none`.
- **Recycle:** neither change reaches a running pod by itself. The keys arrive with the next
  Provision or Reconcile of the instance (a helm upgrade with the new chart and render). The pods
  that upgrade replaces then boot with the identity and the repository.

The job that tags the control image is the one place that knows, deterministically, that control
has a new build. So that job hands the build over. The record still decides everything that matters:
which record is named, whether the instance takes rolls, the image repository, the line, the gate,
and never-backwards.
- **The minimum governance that remains:** a roll **along the record's own Continuous line** is
  unattended. That is the exception that already existed for every working instance. Any other
  roll of control still needs a mesh approval: a different tag, a rollback, or a `RollBack` or
  `SetEnv` through the break-glass `control-recovery` lane.
- The record also declares `moduleUpdatePolicy: Auto`, so control takes every newest compatible
  module (`packages-auto-update`).

## The alarm (`control-always-latest`)

**Platform half.** The core workflow `.github/workflows/control-always-latest.yml` runs every 30
minutes and calls `arm-promoted-set.py control-lag`. It finds the newest promoted set whose
`Deploy control first` job succeeded, and checks whether control's running commit contains that
set. When it does not, it walks back through the earlier builds given to control until it reaches
one control contains. The **first build control was given and did not take** starts the clock. The
results:

- **ok**: control contains the newest set, however long ago it was given.
- **converging**: control does not contain it yet, and it has been behind for less than
  `platformLagBoundMinutes`. This is reported, not red.

🚨 The clock used to start at the newest build. With a platform build every 30–60 minutes that
clock was reset on almost every tick. On 2026-10-06 control had been on ci.9939 for 22 hours
while the alarm read *"converging — 3.0.0-ci.10052 was given 29 min ago"*. A bound that every new
build resets cannot catch the one state it exists for: control taking no build while builds keep
arriving. When the walk reaches the end of the examined runs, or a containment it cannot read,
the reading is a floor and says *"at least"*.
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

## A frozen fleet is red

Policy `control-first-never-silent`
([register](../PolicyNotProse)). Control-first makes the whole fleet wait for ONE instance. So a
control that takes no build must be a failure on every surface where it is read. It must never be
a green wait.

**What happened (measured 2026-10-07/08).** `vars.CONTROL_WEBHOOK_URL` was moved to control's own
inbox (`https://control.systemorph.com/api/hooks/Hosting/PlatformBuilds`) at 18:04Z. The previous
target, memex.systemorph.com, had answered `404` at 18:03Z, and core #6275 then made preflight
require the declared control URL. From ci.10184 (run 37666055296) on, every hand-over answered
`200 {"status":"accepted","signature":"verified"}`. Control's own mesh holds no `Deployments`
record, because cut-over steps 4–6 are unfinished: on control, `namespace:Deployments` and
`namespace:Ops/Actions` both answer `count: 0`. The router therefore dropped every announcement
(`SelfUpdateRouting`: "the record lookup IS the authorization"). A hand-filed `Roll` of control on
memex.systemorph.com (`Ops/Actions/roll-control-20261007-10184-handoff`) and the identity
`Reconcile` both failed at *Launch operator job*: that plane's operator is disabled. Control
stayed on cac0664de, and control's own self-updater read `check FAILED:
CredentialUnavailableException … The requested identity has not been assigned to this resource`
on every check (Plugins#2994). memex and memex-cloud stayed on 57a6e5fd.

Meanwhile:

- `arm` logged `waiting — control runs cac0664de, which does not contain … yet` on every run and
  stayed green.
- Every main-cd run read success.
- The failing verdict sat on control's `Admin/UpdatePolicy`, an authenticated node nobody watches.
- `control-always-latest` did go red (issue #6277, about 4 hours after the first missed build),
  but it named no cause.

**What now reads it as a failure:**

1. **The arming goes RED past the bound.** `arm-promoted-set.py select` (`arming_frozen`) fails the
   `arm` job when all of these hold:
   - nothing was selected and no override was given;
   - control's running build is readable;
   - that build does NOT contain the newest build control was given;
   - control has been behind for longer than `platformLagBoundMinutes`.

   The bound and the clock (the first build control missed) are the same as the alarm's, through
   the one `control_lag` rule, so the two can never disagree about when waiting stopped being a
   wait. `alert-on-failure` then files the run, with an `arm` line saying the fleet is frozen on
   control. The walk goes back through the whole examined history to the first build control
   contains, never a fixed window: a window of the newest ten would lose the first miss as soon as
   control missed more than ten. Behind past the bound freezes whatever `/health` says in the same
   reading, because the lag is what is clocked. Three readings have no clock behind them, so they
   stay the alarm's job and a single `503` never turns CD red:
   - an unreadable control;
   - a control that runs the newest build but answered one non-200;
   - a run whose own `Deploy control first` is still running. The remedy is control's roll. An arm override stays the maintainer's
   call, and nothing suggests one.
2. **Every sentence names control's own verdict.** The arming's `waiting` line, its RED error and
   the `control-lag` issue body each quote control's `self_update` line off its public `/health`.
   When control publishes none, they say so and point at `Admin/UpdatePolicy.lastCheckVerdict`.
   An absent reading is never read as a clean one.
3. **A failing self-update is public.** The one classification `SelfUpdateVerdict.IsFailure`
   covers a faulted check, a release that could not be applied or handed over, a refused
   migration, a stranded tag and a module that can never be activated. It feeds three things:
   - the Warning log level;
   - `Admin/UpdatePolicy.lastCheckFailed`, which the fleet console can flag without parsing a
     sentence;
   - the census-tagged `self_update` entry on `/health` (`SelfUpdateHealthCheck` over
     `SelfUpdateCheckCensus`).

   The entry reads `self_update: Degraded — <first line of the verdict> [outcome …, trigger …,
   N min ago]`. It is Degraded, never Unhealthy, and it carries no probe tag: a failing
   self-update costs delivery, and pulling the pod delivers nothing.
4. **The hand-over says only what a `200` proves.** `Deploy control first` now writes "stored and
   signature-verified … that is not yet a roll". It no longer claims that a `Roll` opened.

**Can control fix its own identity without a Reconcile? No.** The fix (core #6227) changes the
pod's ServiceAccount annotation, the `azure.workload.identity/use` label and `AZURE_CLIENT_ID`,
and renders `SelfUpdate__PortalRepository`. All of these are helm-rendered. Only `hosting-deploy`
applies them, and it runs as the Reconcile (or Provision) step "Re-apply the record". Neither
image path can carry them:

- The self-updater's own path patches only the image, and it needs the very credential that is
  missing to list the registry, so it is circular.
- The routed `Roll` (CD's hand-over) runs `kubectl set image` and the migration. It re-renders
  nothing.

A newer image on the old pod spec is still a pod with no workload identity.

**The one-time remedy.** These are maintainer acts, and none is taken by CD or by an agent:

1. Finish cut-over steps 4–6, so the control plane that receives CD's hand-over holds
   `Deployments/control` and runs an enabled operator.
2. Run one governed `Reconcile` of `Deployments/control` there.

From then on, CD's hand-over routes a `Roll` for every build, and control's own self-update
check lists `memex-control` under its own identity. Until then the arming is red on every run,
naming the reason.

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
4. **In flight — removing the non-boot seeds from the image** (MeshWeaver.Plugins#2970, a
   draft). Until it merges, the published image still seeds all seven non-boot modules. Plugins
   #2893 (the `reload_module`/`uninstall_package` tools and the `ModuleReload` intake) has merged.
   Two decisions still hold the pull request: how a memex-local self-registry install, which has
   no registry to land the seven from, gets them; and confirming that the registry instance
   (memex.meshweaver.cloud) lands its own required modules from its catalog. That pull request
   makes `Memex.Portal.Distributed` carry only the modules declared in
   `src/Memex.Portal.Distributed/image-boot-modules.txt`, each with its reason:
   - both images: `Hosting.Instance`, `Hosting.Cosmos` and `Hosting.Snowflake`;
   - control only: `Fleet.Control` and `SelfUpdate.Aks`.

   Its guard, `ImageBootModulesTest`, holds the host's `<MeshModuleClosure>` rows equal to that
   declaration for both images, with a negative control for each of these mutations:
   - a registry module seeded back into the image;
   - a declared module with no row;
   - a control module leaking into the portal image;
   - a row under an unknown condition.

   The seven modules that leave become **store-delivered**: no baseline `Modules:Assemblies`
   entry, and all seven under `Modules:Required`. A baseline entry for bytes the image does not
   carry would make `required_modules` read *"the image is supposed to ship it"* (Unhealthy) and
   hold readiness on the registry. Without one, a module that has not landed is named as
   Degraded, the shape Radzen, Analysis and GoogleMaps already have. The DEV portal
   (`Memex.Portal.Monolith`) keeps its seeds. The manual, MeshWeaver.Plugins
   `Hosting/ImageSeededModules.md`, gains the boot-only section in the same pull request.
5. **Done — the pair tag `<core7>-p<plugins7>` is retired.** main-cd no longer mints it on
   `memex-portal-ai` (`promote` phase A) or `memex-control` (`control-promote`). See
   "The pair tag, retired" below.
6. **Done — the promotion poller is retired.** MeshWeaver.Plugins deleted `promotion-candidate.yml`
   (the 10-minute poll that measured every promoted pair), the green-pair nudge of core's CD and
   the held-pair ledger (`core-release-attribution.py`, the `core-release-held` issue). Core then
   deleted the `pending` command of `arm-promoted-set.py` that answered the poller, and the two
   `core-candidate.yml` rows in `.github/lane-caller-grants.yml`. Plugins went first, because a
   satellite asserts its roster row against core's `main`, and a `pending:` row excuses an absent
   caller but not an unrecorded one. `core-candidate.yml` stays for the advisory measurements a core
   pull request asks for (`dependent-suites` label or `Pairs-with:`) and for `paired-core.yml`.
   None of them gates a merge or an arming.

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
- That a fresh pod of a boot-only image (step 4) lands every required module from the registry
  and activates it. The rule is in the code: `required_modules` reports a store-delivered module as
  Degraded, and `packages-auto-update` lands and activates it. Neither a fresh pod nor
  control-acceptance has been observed on a boot-only image.
- That the in-mesh compile reference order holds once the seven arrive only through the landed
  sidecar (step 4). While they are seeded, the baseline list fixes their order (Collaboration,
  then AI, then Chat and Mcp). Radzen, Analysis and GoogleMaps have always arrived through the sidecar.
- That the `self_update` line will read Degraded on control once control runs an image that
  carries it. Control cannot take that image until it takes builds again, so the arming will quote
  "no `self_update` reading" until the one-time remedy above lands. The parsing and the line's
  shape are pinned by `SelfUpdateFailureIsPublicTest` and the `arm-promoted-set.py` self-test.
- Whether control.systemorph.com's operator is enabled. The plane that receives CD's hand-over
  needs both the record and an enabled operator, and neither was read there.
- `platformLagBoundMinutes` (180) is a starting bound, not a measured one. No unattended control
  roll had been timed when it was set.

## Related

- [One Promotion Gate](../OnePromotionGate): the arming mechanics this page builds on (cursor,
  completion marker, resume, base).
- Module Reload (`Doc/Architecture/ModuleReload`, arriving with core #6124): `packages-auto-update`, the module half of the separation.
- [Module Adoption Policy](../ModuleAdoptionPolicy): floors (`package-min-mesh-version`).
- [Self-Update on the Control Lane](../SelfUpdateControlLane): how a roll reaches control.
- [Module Versioning](../ModuleVersioning): the epoch identity and the ladder.
