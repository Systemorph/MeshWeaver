---
Name: Deploy, Test the Deploy, Switch Latest
Category: Architecture
Description: The target build shape. Each platform set is deployed as its own versioned build-server deployment, the core suites and the dependents' affected gates run ON that deployment, and only a green deployment becomes `latest`, which is the switch that makes a set available to modules. Plugins and satellites build against `latest` unless a package's authored upper cap forbids it. A cap is debt that a daily triage item tracks. Covers retention, failure semantics, the phased migration from today's channel-based hosts, and cost.
---

# Deploy, Test the Deploy, Switch Latest

> **Status: DECIDED DESIGN — nothing on this page is wired yet.** It is the target shape for
> policy `deploy-test-switch-latest` and policy `package-mesh-version-cap`, both `in force` in
> [Policy Not Prose](../PolicyNotProse) as decisions, with the wiring listed as owed (§8). The
> owner's answers are recorded in §10; four questions there are still open. Today's mechanism is [Build Hosts](../BuildHosts), with
> its design of record in MeshWeaver.Plugins at `Hosting/StandingBuildHosts`, under the in-force
> policy `build-hosts-follow-latest`. Every *today* claim below cites a file and line on `main`.

**The shape in one line:** image set → deploy `build-<version>` → test ON that deployment → on
green, switch `latest` to it. A set is available to modules only once `latest` points at it, and
`latest` only ever points at a deployment that passed the tests.

```text
 main-cd              control instance (operator)            build-<v> deployment
 ───────              ───────────────────────────            ────────────────────
 gate ─▶ images ─▶ promote ─▶ ladder ─▶ arm
                                    │
                                    ├─▶ build-host-deploy ──▶ InstanceAction BuildHostDeploy(v)
                                    │                           └─▶ record: buildHostSets["v/<v>"]
                                    │                                 └─▶ Deployment build-<v> Ready
                                    ├─▶ build-host-test ─────▶ follow lane ◀── core suites + dependents'
                                    │                                          affected gates run here
                                    └─▶ build-host-switch ───▶ green: alias latest := <v>
                                                               red:   latest unchanged, build-<v> torn down
```

## 1. Today, measured

| fact | where |
|---|---|
| Build hosts are merged and switched off in core's CD | `.github/build-hosts.json:3` (`"enabled": false`) |
| The chart renders nothing by default | `deploy/helm/values.yaml:1108-1109` (`buildHosts.enabled: false`) |
| The control record declares no hosts | Memex `mesh/Deployments/control.json` holds no `buildHosts` block |
| The host model is per CHANNEL (`latest`, `stable`), never per version | `src/MeshWeaver.Deployment.Contract/BuildHosts.cs:25-41`; MeshWeaver.Plugins `Hosting/StandingBuildHosts.md:52-69` |
| A channel's set is one mesh-owned token per channel, and it never moves backwards | `BuildHosts.cs:90-101`; MeshWeaver.Plugins `Hosting/Deployment/Source/BuildHostUpdates.cs:105-136` |
| CD's order is gate → images → promote → ladder → control-first → arm | `.github/workflows/main-cd.yml:402,1350,2675,4658,2329,2908` (`arm` needs `platform-ladder-compat`, line 2910) |
| `build-host-latest` moves the channel to the armed set. Nothing exercises that set ON a host before the move | `main-cd.yml:4038-4177` (needs `arm, build-host-image`, line 4045) |
| Core's tests run on GitHub-hosted `ubuntu-latest`, against binaries the run builds itself, in 6 shards | `.github/workflows/dotnet-test.yml:5,658-733,928-959` |
| A host composes an already-built image and runs a repository's checks against it. Core's own CI is a different workload and is not wired | `BuildHosts.md` §5, *Status* |
| Plugins resolves its platform at run time: the newest SEALED main-cd set, then the images by tag in ACR. The resolver is core's script, which Plugins fetches at core `main` | `.github/scripts/resolve-platform.py:24-45`; MeshWeaver.Plugins `.github/workflows/ci.yml:488-723` |
| Plugins' gates run inside the `mw-plugin-test` container on the `aks-silos` / `aks-silos-dind` runner pools | MeshWeaver.Plugins `ci.yml:46-49,723` |
| A package declares only a FLOOR, `minMeshVersion` | `src/MeshWeaver.PluginCatalog/Package.cs:236-248`; `PackagePlatformFloorGate.cs:13-32` |
| The only upper bound is on BUILT BYTES across a declared epoch break, never authored per package | `src/MeshWeaver.Compiler/platform-compatibility.json:3-4`; `.github/scripts/previous-epoch-ceiling.py:1-28` |
| Nothing flags a package that is stuck behind a cap | there is no cap to flag |

## 2. The pipeline

Five steps, in this order. Each step is a job with a positive success signal. None of them is
skipped on missing input (§6).

1. **Image set.** Unchanged: `gate`, the images, `promote`, the compatibility ladder and `arm`
   produce an immutable set (version, tester, portal and runner digests). The set is already the
   unit the hosts run (`BuildHostChannel.ToSetToken`, `BuildHosts.cs:90`). The set gains a
   FIFTH digest, the `mw-core-tests` image: core's test projects built from the set's commit with
   the set's SDK, in the same CD run (§10, D3).
2. **Deploy `build-<version>`.** Job `build-host-deploy` replaces the move half of today's
   `build-host-latest`. It sends the signed event `build-host-deploy-requested` into the control
   plane's inbox, the same path and the same HMAC as today (`main-cd.yml:4158-4176`). The
   receiver turns the event into a **governed `Hosting/InstanceAction`**, `BuildHostDeploy`, on
   control. The in-cluster operator executes it. Never `kubectl`, never a hand-edited Deployment,
   never `helm` by hand. The action writes the version's entry in the mesh-owned set (§3). The
   record change applies itself (policy `record-change-applies-itself`). The action completes when
   the new Deployment reports Ready, and Ready already means the plugins layer is staged
   (`BuildHosts.md` §3).
3. **Test the deployment.** Job `build-host-test` queues a run whose claim names the CANDIDATE
   version, never a channel. It follows each stage through the existing follow lane
   (`build-follow.yml`: only `succeeded` is green, a timeout is red, an unreadable endpoint is
   red). The run executes:
   - **the core suites**, as stages of a new core declaration `.github/build-stages.json`, run
     from the set's `mw-core-tests` image: today's 6 shards, the doc gate and the ladder's link
     probe;
   - **the dependents' affected gates**: MeshWeaver.Plugins' and each satellite's `compile-check`
     and `gate` stages (H7, `BuildHosts.md` §5), for the modules the platform delta affects,
     against the same candidate. Their verdict GATES the switch (§10, D1). That is a register
     exception to `dependents-verify-on-their-side`, scoped to the module-facing `latest` switch
     only.
4. **Switch `latest`.** On a run where the core suites AND every affected dependents' gate are
   green, job `build-host-switch` sends
   `build-host-switch-requested`. Its InstanceAction `BuildHostSwitch` moves the alias `latest`
   to the candidate version in ONE record write, never backwards, and only to a version whose run
   is recorded green for every planned stage. The receiver re-reads that run node; the event's
   claim alone is not trusted.
5. **Available to modules.** Whatever builds "against latest" (§4) resolves the alias at the
   moment it runs. So the switch is the publication of the set to modules. Nothing before it
   offers the candidate to any module build, and a red set is never offered.

🚨 **`latest` is a pointer to a tested deployment, never a deployment that is re-rendered.** A
switch moves no image, stages nothing and restarts nothing: the candidate is already warm,
because it was just tested. That is the property *"first deploy, then test the deploy, then switch
latest"* buys. What runs after the switch is byte-for-byte what was tested.

## 3. What a build-server deployment is

Reuse, generalise, add nothing parallel:

| piece | today | target |
|---|---|---|
| the set | one token per CHANNEL in `DeploymentContent.BuildHostSets["latest"]` | one token per VERSION in `BuildHostSets["v/<version>"]`. The token keeps its shape (`BuildHosts.cs:90`) and gains the `mw-core-tests` digest as its fifth part (D3) |
| the alias | the channel name IS the host | `BuildHostSets["alias/latest"] = "<version>"`, one scalar leaf, so a stale switch conflicts as a whole, the same reason the set is one token (`BuildHosts.md` §4) |
| the authored block | `BuildHostsBlock { Enabled, Channels }` (`BuildHosts.cs:25-41`) | `Enabled` stays. A new init-only `Retention` (§5) and a per-version default `MaxReplicas`. `Channels` is kept for `stable` (Q4). Init-only only: the binary-break rule (`BuildHosts.cs:2-4`) |
| the Deployment | `build-host-<channel>` | `build-<v>`, where `<v>` is the version with dots as dashes (`build-3-1-10412`), so it is a valid DNS label, with labels `meshweaver.io/build-version=<version>` and `meshweaver.io/build-alias=latest` on the one the alias names |
| the ScaledObject | one per channel, demand from `/queue?channel=<c>` | one per version, demand from `/queue?version=<v>`. Demand for `latest` is counted against the version the alias names, so only that host scales for it |
| the claim | `{channel, platformVersion, …}` | `{version, alias?, …}`. A run queued for `latest` is resolved to a version WHEN IT IS QUEUED, and the run node records the version (StandingBuildHosts §1: *"a run records a set"*) |
| the receiver | `build-host-update-available` → `BuildHostSets[channel]` (`BuildHostUpdates.cs:105-136`) | three events → three InstanceActions: `BuildHostDeploy`, `BuildHostSwitch`, `BuildHostTeardown`. Each one is authorized as the caller (the signed CD event) and executed as System (policy `authorize-as-caller-execute-as-system`) |

GitSync keeps stripping `buildHostSets` on export (`BuildHosts.md` §4), so git still never names a
build. The record stays under the estate's NO PINS rule (`BuildHosts.md` §4): the versions live only
in the mesh-owned member.

## 4. Resolution for Plugins and satellites

**Rule:** a node repository builds and tests against the deployment `latest` names. A package
whose authored upper cap (§5) forbids `latest` builds against the **highest retained deployment the
cap allows**.

- **The source of `latest` is the control record's alias**, read through a public read endpoint
  on the build API (`GET /api/builds/hosts` → `{latest, versions:[{version, digests, ready}]}`).
  It is no longer "the newest sealed main-cd run". `resolve-platform.py` (core-owned, fetched by
  every satellite at core `main`) gains `--from-build-hosts <api>`, which resolves the alias to
  its set and takes the digests from the record, not from a tag lookup. A missing, unreadable or
  not-Ready alias is a RED job naming what was looked for. It never falls back to "newest sealed",
  because that set may be untested (resolve-platform.py's own rule: *"never a fallback to a moving
  tag nobody named"*).
- **With a cap**, the resolver takes the highest version in `versions` at or below the cap. If no
  retained deployment satisfies the cap, the result is RED, naming the cap, and never a silent
  pick of `latest`. That outcome cannot arise while retention (§5) works, so a red here is a
  retention defect.
- **Per package, not per repository.** A repository's run partitions its modules by the version
  each one resolves to. Uncapped modules run on `latest`. Capped modules run on their cap's
  deployment, each set built once. **Both publish** (§10, D2): an uncapped module publishes from
  its `latest` run, and a capped module builds AND publishes against the highest deployment its
  cap allows, so instances on older platforms keep getting its fixes. Publishing does not retire
  the debt: the capped package still carries its "migrate to latest" triage item (§5).
- **On a host**, the claim names the version, so the follow lane needs no change. **On Actions**
  (until a repository claims `ciEquivalent`), the resolved digests feed the existing `with:`
  inputs of `node-repo-gate.yml` / `node-repo-publish-bake.yml`, unchanged.

## 5. Caps — authored, validated, owed

**The field.** `maxMeshVersion`, next to `minMeshVersion` in the package's `index.json` content
(`Package.cs:236-248`). It is optional. Absent means *"builds against latest"*, which is the normal
case. A cap carries two companion fields, both required when the cap is present: `capReason`
(one line) and `capTracking` (the migration item's node path).

**Validation** (core's `node-repo-validate.yml`, so every satellite inherits it):

- the cap is comparable with the floor and is at or above it. It is decided by the ONE comparator
  `PlatformFloor`, so the version-ordering trap stays closed (policy `package-min-mesh-version`);
- the cap names a sealed set that exists. An unknown version is red;
- **a cap that admits `latest` is red: "the cap is stale, remove it".** A cap that restricts
  nothing would quietly become a pin the day `latest` overtakes it;
- a cap cannot be added without `capReason` and `capTracking`.

**Runtime.** This design gives the cap BUILD-and-TEST meaning only: it selects the deployment a
package's modules compile and test against. What an instance that runs above the cap does with
that package is Q2. Policy `package-min-mesh-version` says a floor never holds a platform roll,
and a cap must not either.

**The cap is debt, not a resting place.** A capped package owes a migration to `latest`
(*"or migrate to latest"*). A daily job on the control instance reads every package that declares a
cap in the registry catalog. For each one it files, or refreshes, ONE triage item: a
`Feedback/Feedback` node whose message names the package, the cap, `latest`, the distance in
platform builds, the age of the cap and `capTracking`. The item is keyed by (package, cap), so a
second day updates the same item and never files a duplicate. Removing the cap closes it. The job
runs as System after the platform-action check (policy `authorize-as-caller-execute-as-system`),
and triage decides the rest, as for every finding ([Incidental Findings](../IncidentalFindings)).

## 6. Retention

**Keep exactly:**

1. the deployment `latest` names;
2. the candidate under test, if any (at most one; a newer candidate supersedes an older one, which
   is torn down);
3. the PREVIOUS `latest`, the deployment the alias named before its last switch (§10, D4);
4. for every package with a cap, the highest version at or below that cap. Several packages with
   the same cap share one deployment.

**Rollback is an instant switch.** Because rule 3 keeps the previous `latest` deployed and its
digests locked, going back is `BuildHostSwitch` moving the alias to that version: one record
write, no redeploy, no staging, nothing restarted. It is the one switch allowed to move the alias
BACKWARDS, it names the version it leaves in its reason, and it reads the record back like every
switch (§7). The version it left is then the previous `latest` and is retained under rule 3 until
the next forward switch.

**Tear down everything else** with `BuildHostTeardown`: the record entry goes, so the Deployment and
the ScaledObject leave on the next reconcile. The `build-cache` claim is never touched by a
teardown. Its content-addressed entries for a torn-down set age out under the cache's own sweep.
A teardown is refused for any version that rule 1, 2, 3 or 4 still holds. The rule is computed from
the record and the catalog, both live, never from a list the CD run carries.

**Digests.** Every digest of a retained deployment (tester, portal, runner, core tests) is a PINNED digest in
the sense of [Pinned Image Retention](../PinnedImageRetention): `lock-pinned-digests.py` reads the
record's `buildHostSets` and locks them, so the ACR purge, which counts newer builds, never deletes
an image a capped package still compiles against. A teardown unlocks them. This closes the
retention gap `BuildHosts.md` §6 lists for `mw-build-host` and the hosts' digests.

## 7. Failure semantics

- **A red test on the candidate:** `latest` does not move. The candidate is torn down. The run
  node keeps every stage's log and Check Run, so the evidence outlives the pods. The failure is
  reported on ONE open `build-host-candidate` issue, which is closed only by a run that switched
  `latest`, the same pattern as today's `build-host-report` (`main-cd.yml:4184`). The next set
  that passes supersedes.
- **A deploy that fails** (the action fails, is refused, or the Deployment is not Ready inside the
  job's window) is RED. It is never "skipped" and never a pass that the next run repairs. The
  candidate's partial record entry is torn down.
- **No skip-trapdoors.** Once the hosts are declared on (`.github/build-hosts.json`), a `preflight`
  job asserts the webhook URL and secret and the build API. The deploy, test and switch jobs
  `needs:` it and run unconditionally. No `continue-on-error`, and no `if:` that asks whether a
  secret is set. `enabled: false` remains the one declared off-switch, and it prints itself.
- **A switch is never inferred from a 200.** The switch job reads the record back and is green only
  when the alias names the candidate (today's lesson, `main-cd.yml:4167-4169`: *stored and
  verified is not delivered*).
- **Every job is cut at 45 minutes.** A run longer than one job is followed through the Check
  Runs, as the follow lane already does (`BuildHosts.md` §1).
- **Platform delivery is not held by any of this** (policy `platform-module-deploy-separate`
  stays in force unchanged): promote, verify, bake and `delivery-verdict` keep their order, and a
  platform roll never waits on a module verdict. A red candidate, whether core's suites or a
  dependent's gate went red, holds only the module-facing `latest`.

## 8. Migration, as phased pull requests

| phase | repo | change | owner-gated? |
|---|---|---|---|
| P0 | core | this design, plus the two `proposed` register rows | no |
| P1 | core | contract: init-only additions to `BuildHostsBlock` (`Retention`, the per-version default), the `v/` and `alias/` key forms, `Package.MaxMeshVersion` + `CapReason` + `CapTracking`, and the cap validator in `node-repo-validate.yml` | no |
| P2 | core | chart: render one Deployment and one ScaledObject per `v/` entry, plus the alias label, with `test-build-hosts.sh` fixtures for 0, 1 and 3 versions | no |
| P3 | MeshWeaver.Plugins | the receiver and the operator: the three events → `BuildHostDeploy` / `BuildHostSwitch` / `BuildHostTeardown`, the version-keyed claim and queue, `GET /api/builds/hosts`, the retention computation | no |
| P4 | core | `main-cd.yml`: replace `build-host-latest` with `build-host-deploy` → `build-host-test` → `build-host-switch` + `build-host-report`, a core `.github/build-stages.json`, and the `mw-core-tests` image as the set's fifth digest (D3) | no |
| P5 | core + MeshWeaver.Plugins | `resolve-platform.py --from-build-hosts` (core) and the platform-ref adoption in Plugins `ci.yml` and the satellites, including per-package resolution under a cap | no |
| P6 | MeshWeaver.Plugins + core | the daily cap-debt job (control), the capped-run publish path (D2), the retention of the previous `latest` and the backwards rollback switch (D4), and the `lock-pinned-digests.py` reading of `buildHostSets` (core) | no |
| P7 | Memex | the `buildHosts` block on `mesh/Deployments/control.json`, through the record's approval path | **yes** |
| P8 | core | `.github/build-hosts.json` `enabled: true`, AFTER P7 (the order `BuildHosts.md` §4 fixes) | **yes** |
| P9 | control | the roll that renders the first `build-<v>`, then the first green switch. Verify by reading `GET /api/builds/hosts` and the run nodes' recorded version, never the CD job's green | **yes** |

`deploy-test-switch-latest` and `package-mesh-version-cap` are `in force` as decisions, with
P1 to P9 listed as owed on their register rows. When P9 is verified, `build-hosts-follow-latest`
is retired in the register.

## 9. Cost

- **CPU moves from GitHub to the cluster.** Core's 6 test shards run on GitHub-hosted runners today
  (`dotnet-test.yml:959`). On a host they consume `cibuild` pool vCPU, and each shard is a
  replica of the candidate (its `MaxReplicas` must cover the shard count plus the dependents'
  gates). Per CD run that adds one full core test pass plus the dependents' affected gates. The
  PR-time `dotnet-test.yml` is unchanged, so this is an ADDED pass, not a moved one, until Q6
  decides otherwise.
- **Idle cost is low.** KEDA scales every version to zero (`minReplicaCount: 0`, `BuildHosts.md`
  §3). A retained but idle deployment costs its record entry, its locked images and its cache
  entries, not a node. The price is a cold start when a capped package's run arrives.
- **Staging per version.** Each deploy copies the tester and portal `/app` by digest into the
  composed host, and builds the reference set once. One extra deploy per armed set.
- **One more image per set.** `mw-core-tests` (D3) is built and pushed in every CD run and locked
  for as long as its set is retained (§6).
- 🚨 **Quota is the binding constraint, not money.** At its last measurement the dind pool could
  not schedule at all: `Standard Dsv6 Family vCPUs` were at their regular limit and
  `Total Regional Low-priority vCPUs` at their spot limit, with Plugins `main` queued behind it
  (Memex #695, open). The build hosts run on `cibuild`, not `cidind`, but in the SAME regional
  Dsv6 family quota. Adding a candidate deployment that runs core's suites per CD run competes
  for that quota with every satellite's CI. P4 must not be enabled before the quota and node-pool
  change Memex #695 asks for has landed, through the governed CI-runner path.

## 10. Decided, and still open

### Decided

- **D1 — the switch gate.** `latest` moves only when core's suites AND the affected
  MeshWeaver.Plugins and satellite gates are green on the deployed build server. This is a
  register exception to `dependents-verify-on-their-side`, scoped to the module-facing `latest`
  switch ONLY: core pull requests still merge on core's own checks, and no other core job waits
  on a dependent. `platform-module-deploy-separate` stays in force: a platform roll never waits
  on a module verdict (§7).
- **D2 — capped runs publish.** A package whose cap forbids `latest` builds AND publishes against
  the highest deployment its cap allows (§4). It still carries its "migrate to latest" triage
  item (§5), so publishing never turns the cap into a resting place.
- **D3 — core's tests run from a test image built with the set.** `mw-core-tests` becomes the
  fifth digest of the image set (§2), built in the same CD run, locked with the set's other
  digests (§6). The host never builds core's tests from source.
- **D4 — rollback.** Retention keeps `latest`, the candidate, the PREVIOUS `latest`, and every
  version a cap needs (§6). Rollback is an instant switch of the alias to the previous `latest`,
  never a redeploy.

### Open

- **Q2 — what a cap means at runtime.** Build-time only, as designed here? Or does an instance
  above the cap hold that package's update the way a floor holds one? A runtime hold must never
  hold a platform roll.
- **Q4 — `stable`.** Keep `build-stable` as a second alias over the same versioned deployments
  (a retained version, like a cap), or retire it?
- **Q6 — does `arm` move after the deploy test?** The owner's order puts tests after the image.
  Today `arm` (the fleet's roll target) is decided before any host exists. Should the fleet's
  `<line>-latest` pointer and the module-facing `latest` become the same switch, with the PR-time
  ubuntu shards then reduced to a pre-merge subset?
- **Q8 — the identity of a deployment.** Versioned Deployments inside the control record's
  `buildHostSets`, as designed, or a `Deployments/build-<v>` record per version on control? The
  first keeps the NO PINS rule trivially. The second makes each version an operable instance in
  the Fleet Console.

## Related

[Build Hosts](../BuildHosts) · [The Build Server](../BuildServer) ·
[Release Channels](../ReleaseChannels) · [Module Adoption Policy](../ModuleAdoptionPolicy) ·
[Pinned Image Retention](../PinnedImageRetention) · [One Promotion Gate](../OnePromotionGate) ·
[Module Build Architecture](../ModuleBuildArchitecture) · [Policy Not Prose](../PolicyNotProse).
