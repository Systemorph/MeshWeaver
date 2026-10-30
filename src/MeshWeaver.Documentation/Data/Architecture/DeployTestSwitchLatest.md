---
Name: Deploy, Test the Deploy, Switch Latest
Category: Architecture
Description: The target build shape. Each platform set is deployed as its own versioned build-server deployment, the core suites and the dependents' affected gates run ON that deployment, and only a green deployment becomes `latest`, which is the switch that makes a set available to modules. Plugins and satellites build against `latest` unless a package's authored upper cap forbids it. A cap is debt that a daily triage item tracks. Covers retention, failure semantics, the phased migration from today's channel-based hosts, and cost.
---

# Deploy, Test the Deploy, Switch Latest

> **Status: DECIDED DESIGN — nothing on this page is wired yet, except the readiness contract
> (§2a).** It is the target shape for
> policy `deploy-test-switch-latest` and policy `package-mesh-version-cap`. Both are DECIDED and
> both stay `proposed` in [Policy Not Prose](../PolicyNotProse) until their wiring (§8) lands,
> because the register marks a row `in force` only once its mechanism exists. The
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
                                    │                                 └─▶ build-<v> complete, pinned probe CLAIMED
                                    ├─▶ build-host-test ─────▶ follow lane ◀── core suites + dependents'
                                    │                                          affected gates run here
                                    └─▶ build-host-switch ───▶ green: alias latest := <v>
                                                               red:   latest unchanged, build-<v> torn down
```

## 1. Today, measured

| fact | where |
|---|---|
| Build hosts are merged and switched off in core's CD | `.github/build-hosts.json:3` (`"enabled": false`) |
| The chart renders nothing by default | MeshWeaver.Deploy `deploy/helm/values.yaml:1113-1114` (`buildHosts.enabled: false`); the build-host chart lives there, core's is frozen (`BuildHosts.md` §3) |
| The control record declares no hosts | Memex `mesh/Deployments/control.json` holds no `buildHosts` block |
| The host model is per CHANNEL (`latest`, `stable`), never per version | MeshWeaver.Plugins `src/MeshWeaver.SelfUpdate.Aks/Deployment/BuildHosts.cs:25-41`; MeshWeaver.Plugins `Hosting/StandingBuildHosts.md:52-69` |
| A channel's set is one mesh-owned token per channel, and it never moves backwards | `BuildHosts.cs:90-101`; MeshWeaver.Plugins `Hosting/Deployment/Source/BuildHostUpdates.cs:105-136` |
| CD's order is gate → images → promote → ladder → control-first → arm | `.github/workflows/main-cd.yml:402,1350,2675,4658,2329,2908` (`arm` needs `platform-ladder-compat`, line 2910) |
| `build-host-latest` moves the channel to the armed set. Nothing exercises that set ON a host before the move | `main-cd.yml:4038-4177` (needs `arm, build-host-image`, line 4045) |
| Core's tests run on GitHub-hosted `ubuntu-latest`, against binaries the run builds itself, in the suite's shards (as many as `CI_SHARD_TOTAL` declares) | `.github/workflows/dotnet-test.yml:5,253,658-733,928-965` |
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
   record change applies itself (policy `record-change-applies-itself`). 🚨 **Readiness is
   `complete` plus a CLAIMED probe — there is no `ready` field** (the contract below, §2a). The
   deploy step waits until `GET /api/builds/hosts` lists the version with `complete: true`, then
   submits a probe pinned to that version (`POST /probes` with `version`) and waits for a host to
   CLAIM it. A claim proves a host of that version is up and serving; no claim inside the deploy
   window is a deploy FAILURE (§7). The probe's verdict is a test like any other. 🚨 **A deploy
   carries its own warm-up demand.** Scaling demand comes only from attempts (§3) and an idle
   version scales to zero (§9), so before any test attempt exists nothing would keep a replica up
   while the deploy step waits for the claim. The `v/<version>` entry the action writes is
   therefore marked `warming`, and the chart renders a `warming` version's ScaledObject with
   `minReplicaCount: 1`. Step 3 admits the test attempts BEFORE the action clears `warming`, so
   the floor drops back to zero only once the attempts' own demand holds the replica.

### 2a. The readiness contract

Implemented in MeshWeaver.Deploy#8 and core #7200; the design of record is MeshWeaver.Plugins
`Hosting/StandingBuildHosts.md` §7b.

```text
GET /api/builds/hosts
{ "aliases":  { "latest": "<v>", "candidate": "<v>", "previous": "<v>" },
  "versions": [ { "version": "<v>", "deploymentName": "build-<v>", "maxReplicas": N,
                  "aliases": ["latest", …], "pinned": N, "complete": true|false } ] }
```

- **A version is deployable once it is `complete`** — its set is whole and its Deployment and
  ScaledObject are rendered. `complete` says nothing about a running replica: a complete version
  scaled to zero is a valid target, and a pinned attempt scales it up.
- **There is NO `ready` field.** What proves a ready host is a CLAIM: a probe pinned to the version
  (`POST /probes` with `version`) that a host of that version claimed. Nothing reads a readiness
  flag, so nothing can trust a stale one.
- `pinned` counts the attempts recorded against the version (the retention rule 4 input, §6);
  `aliases` lists every alias that names it.
3. **Test the deployment.** Job `build-host-test` queues runs whose claims name the CANDIDATE
   version, never a channel. It follows each stage through the existing follow lane
   (`build-follow.yml`: only `succeeded` is green, a timeout is red, an unreadable endpoint is
   red). The runs execute:
   - **the core suites**, as stages of a new core declaration `.github/build-stages.json`, run
     from the set's `mw-core-tests` image: the suite's shards (as many as `dotnet-test.yml`
     declares in `CI_SHARD_TOTAL`), the doc gate and the ladder's link probe. 🚨 **One pinned run
     PER SHARD, never one run holding every shard.** A host runs one run's stages one after
     another (`.github/scripts/build-run.py:1682-1699`: a `for st in stages` loop, each stage
     awaited before the next), so shards declared as stages of ONE run would execute serially on
     one replica and `MaxReplicas` would parallelise nothing. Each shard is therefore its own run,
     pinned to the candidate version, and the runs spread across the version's replicas (§9);
   - **the dependents' affected gates**: MeshWeaver.Plugins' and each satellite's `compile-check`
     and `gate` stages (H7, `BuildHosts.md` §5), for the modules the platform delta affects,
     against the same candidate. Their verdict GATES the switch (§10, D1). Once the policy is in
     force, that is a register exception to `dependents-verify-on-their-side`, scoped to the
     module-facing `latest` switch only; until then that policy holds unexcepted.
4. **Switch `latest`.** The verdict is COMBINED over the candidate's runs: every shard run, the
   doc-gate run and every affected dependents' gate run must be `Succeeded`, and one run that is
   red, missing or unfinished holds the switch. The P4 deploy workflow's `build-host-test` job owns that verdict (§8). When it
   is green, job `build-host-switch` sends `build-host-switch-requested`, naming EVERY run the
   verdict covered (or one parent node that lists them). Its InstanceAction `BuildHostSwitch` moves
   the alias `latest` to the candidate version in ONE record write, never backwards, and only when
   each named run is recorded green for every planned stage AND recorded the candidate version.
   The receiver re-reads every one of those run nodes; the event's claim alone is not trusted, and
   an event that names fewer runs than the plan is red.
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
| the ScaledObject | one per channel, demand from `/queue?channel=<c>` | one per version, demand from `/queue?version=<v>`. Demand is counted by each attempt's RECORDED version, never by the alias: the alias is consulted once, when a `latest` run is admitted (next row), so an attempt queued on V before a switch to V+1 still scales V |
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
  on the build API. It is no longer "the newest sealed main-cd run". `resolve-platform.py`
  (core-owned, fetched by every satellite at core `main`) reads it with `--from-build-hosts <api>`,
  ONCE per run, so the alias and the versions cannot disagree about a switch that happened between
  two reads. The fields it reads are exactly these (the §2a shape):

  ```text
  GET /api/builds/hosts
  { "aliases":  { "latest": "<v>", … },
    "versions": [ { "version": "<v>", "complete": true|false, … } ] }
  ```

  `latest` is `aliases.latest`, never a field at the response root, and it must name a row of
  `versions` whose `complete` is `true`. `complete` is RECORD completeness (the record holds that
  version's full set, so the chart renders its Deployment); it is not pod readiness, and the
  resolver reads no `ready` field. A complete version scaled to zero is a valid target. **The
  digests do not come from this answer**: the resolver takes the version `aliases.latest` names and
  resolves it through the existing freeze path, exactly like a pinned `MW_PLATFORM_REF` — that one
  sealed CD run, its images present, never a substitute. A missing or unreadable answer, a missing
  alias, or an alias naming a version that is absent or not `complete`, is a RED job naming what
  was looked for. It never falls back to "newest sealed", because that set may be untested
  (resolve-platform.py's own rule: *"never a fallback to a moving tag nobody named"*).
- **With a cap**, the resolver takes the highest version in `versions` that satisfies BOTH bounds:
  at or above the package's `minMeshVersion` and at or below its cap, compared by the one
  comparator `PlatformFloor`, among versions that are `complete` (record completeness, never pod readiness: an incomplete
  version is skipped; a complete one scaled to zero is a valid target). If no such version lies in that range, the result is
  RED, naming the floor, the cap and the retained versions, and never a silent pick of `latest` or
  of a version below the floor. Once a cap is admitted (§5) retention holds its deployment (§6),
  so a red after admission is a retention defect; a red before the cap's deployment is `complete` is
  the admission still in progress, and says so.
- **Per package, not per repository.** A repository's run partitions its modules by the version
  each one resolves to. Uncapped modules run on `latest`. Capped modules run on their cap's
  deployment, each set built once. **Both publish** (§10, D2): an uncapped module publishes from
  its `latest` run, and a capped module builds AND publishes against the highest deployment its
  cap allows, so instances on older platforms keep getting its fixes. Publishing does not retire
  the debt: the capped package still carries its "migrate to latest" triage item (§5).
- **On a host**, the claim names the version, and the follow lane must select by it. Today it
  does not: it keys a stage log by repository, SHA and `channel` (`build-follow.yml:83-84`,
  `build-follow.py:211,245-246`), so the same commit run against two versions is ambiguous. The
  lane gains a `version` input that the follower sends as the log query key in place of
  `channel`, and the build API answers by the run's RECORDED version (P3, P4). **On Actions**
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
- a cap cannot be added without `capReason` and `capTracking`;
- **admission reserves the deployment.** A new cap usually names a version that `latest` has just
  left, so its deployment may already be gone. The validator reads `GET /api/builds/hosts`: if the
  highest version in the cap's range is retained and `complete`, the cap is admitted and rule 5 of §6
  holds it from the moment the cap is on a default branch. If it is not deployed, the cap's
  admission requests `BuildHostDeploy(<v>)` for that SEALED set, followed by the same test run a
  candidate gets (§2 step 3) and NO switch; capped builds of the package are red, naming the
  pending deployment, until it is `complete`, its pinned probe has been claimed, and its run is green. If the set's images are no longer in the
  registry, the cap cannot be admitted, and the validator says so by name.

**Runtime.** This design gives the cap BUILD-and-TEST meaning only: it selects the deployment a
package's modules compile and test against. What an instance that runs above the cap does with
that package is Q2. Policy `package-min-mesh-version` says a floor never holds a platform roll,
and a cap must not either.

**The cap is debt, not a resting place.** A capped package owes a migration to `latest`
(*"or migrate to latest"*). A daily job on the control instance reads every package that declares a
cap in the registry catalog. For each one it files, or refreshes, ONE triage item: a
`Feedback/Feedback` node whose message names the package, the cap, `latest`, the distance in
platform builds, the age of the cap and `capTracking`. The item is keyed by the package's stable
identity alone, never by its cap: a second day updates the same item, and a cap that is RAISED but
still forbids `latest` updates the cap named in that same item in place, so neither ever files a
duplicate. Removing the cap closes it. The job
runs as System after the platform-action check (policy `authorize-as-caller-execute-as-system`),
and triage decides the rest, as for every finding ([Incidental Findings](../IncidentalFindings)).

## 6. Retention

**Keep exactly:**

1. the deployment `latest` names;
2. the candidate under test, if any (at most one; a newer candidate supersedes an older one, which
   is torn down);
3. the PREVIOUS `latest`, the deployment the alias named before its last switch (§10, D4);
4. every version a queued or running attempt recorded (§3), until that attempt ends;
5. for every package with a cap, the highest version inside its floor-to-cap range (§4), including
   one that a cap admission (§5) is deploying. Several packages with the same range share one
   deployment.

**Rollback is an instant switch.** Because rule 3 keeps the previous `latest` deployed and its
digests locked, going back is `BuildHostSwitch` moving the alias to that version: one record
write, no redeploy, no staging, nothing restarted. It is the one switch allowed to move the alias
BACKWARDS, it names the version it leaves in its reason, and it reads the record back like every
switch (§7). The version it left is then the previous `latest` and is retained under rule 3 until
the next forward switch.

**Tear down everything else** with `BuildHostTeardown`: the record entry goes, so the Deployment and
the ScaledObject leave on the next reconcile. The `build-cache` claim is never touched by a
teardown. Its content-addressed entries for a torn-down set age out under the cache's own sweep.
A teardown is refused for any version that rule 1, 2, 3, 4 or 5 still holds. The rule is computed from
the record, the queue and the catalog, all live, never from a list the CD run carries. **Admission
and teardown cannot race:** both are writes to the control record through its owning hub, which
serialises them. A teardown re-checks rule 4 inside its own write, and an attempt's admission
re-checks, inside ITS write, that the version is still in the record; an attempt that finds its
version gone is re-resolved, or red, never queued for a deployment that is leaving.

**Digests.** Every digest of a retained deployment (tester, portal, runner, core tests) is a PINNED digest in
the sense of [Pinned Image Retention](../PinnedImageRetention): `lock-pinned-digests.py` reads the
record's `buildHostSets` and locks them, so the ACR purge, which counts newer builds, never deletes
an image a capped package still compiles against. A teardown removes only THIS deployment's
entry, and with it this deployment's claim on the pins; it unlocks nothing. The lane locks and never
unlocks ([Pinned Image Retention](../PinnedImageRetention): *"IT LOCKS. IT DOES NOT UNLOCK."*), and a
digest another retained deployment or a running portal still names must stay locked, so releasing a
lock is left to that page's guarded, opt-in release arm, which requires a complete inventory. This closes the
retention gap `BuildHosts.md` §6 lists for `mw-build-host` and the hosts' digests.

## 7. Failure semantics

- **A red test on the candidate:** `latest` does not move. The candidate is torn down. The run
  node keeps every stage's log and Check Run, so the evidence outlives the pods. The failure is
  reported on ONE open `build-host-candidate` issue, which is closed only by a run that switched
  `latest`, the same pattern as today's `build-host-report` (`main-cd.yml:4184`). The next set
  that passes supersedes.
- **A deploy that fails** (the action fails, is refused, the version is not `complete`, or no
  host CLAIMS the version-pinned probe inside the job's window, §2a) is RED. It is never "skipped" and never a pass that the next run repairs. The
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
| P0 | core | this design, the owner's answers (§10) and the two register rows | no |
| P1 | core | contract: init-only additions to `BuildHostsBlock` (`Retention`, the per-version default), the `v/` and `alias/` key forms, `Package.MaxMeshVersion` + `CapReason` + `CapTracking`, and the cap validator in `node-repo-validate.yml` | no |
| P2 | MeshWeaver.Deploy | chart (it owns the build-host chart; core's is frozen, `BuildHosts.md` §3): render one Deployment and one ScaledObject per `v/` entry (with `minReplicaCount: 1` while the entry is `warming`, §2), plus the alias label, with `test-build-hosts.sh` fixtures for 0, 1 and 3 versions | no |
| P3 | MeshWeaver.Plugins | the receiver and the operator: the three events → `BuildHostDeploy` / `BuildHostSwitch` / `BuildHostTeardown`, the version-keyed claim and queue, the stage-log endpoint answering by recorded version, `GET /api/builds/hosts` in the §2a shape (§4 lists the fields the resolver reads), the retention computation. **Follow-up (plugins-e6): the switch accepts a SET of runs.** e6's switch receiver checks a single `run` today; it must take every run the event names (or a parent that lists them), re-read each, and refuse when any is not `Succeeded` or did not record the candidate version (§2 step 4) | no |
| P4 | core | `main-cd.yml`: replace `build-host-latest` with `build-host-deploy` → `build-host-test` → `build-host-switch` + `build-host-report`, a core `.github/build-stages.json`, the follow lane's `version` input (§4), and the `mw-core-tests` image as the set's fifth digest (D3). `build-host-test` submits ONE pinned run per shard and owns the COMBINED verdict over the shard runs and the dependents' gate runs; the switch event names all of them or their parent (§2 steps 3–4) | no |
| P5 | core | `resolve-platform.py --from-build-hosts`, including per-package resolution under a cap, IMPLEMENTED and self-tested but INERT: no consumer passes the flag, so Plugins and the satellites stay on today's newest-sealed path | no |
| P6 | MeshWeaver.Plugins + core | the daily cap-debt job (control), the capped-run publish path (D2), the retention of the previous `latest` and the backwards rollback switch (D4), and the `lock-pinned-digests.py` reading of `buildHostSets` (core) | no |
| P7 | Memex | the `buildHosts` block on `mesh/Deployments/control.json`, through the record's approval path | **yes** |
| P8 | core | `.github/build-hosts.json` `enabled: true`, AFTER P7 (the order `BuildHosts.md` §4 fixes) | **yes** |
| P9 | control | the roll that renders the first `build-<v>`, then the first green switch. Verify by reading `GET /api/builds/hosts` and the run nodes' recorded version, never the CD job's green | **yes** |
| P10 | MeshWeaver.Plugins + satellites | adopt `--from-build-hosts` in Plugins `ci.yml` and the satellites, ONLY after P9 is verified, because §4 refuses a missing alias with no fallback and adopting earlier would red every consumer's CI | no |

### Status of the module-facing phases (the cap half of P1, P5, P6's cap debt, P10)

| phase | status | where |
|---|---|---|
| P1, cap half | **implemented.** `Package.MaxMeshVersion`, `CapReason` and `CapTracking` are carried by the node-repo listing. `resolve-platform.py --check-caps` is the validator, and `node-repo-validate.yml` runs it for every satellite. | core #7192 |
| P5 | **implemented, inert.** `resolve-platform.py --from-build-hosts <api> [--packages-root .]` reads `GET /api/builds/hosts` (`{aliases:{latest, …}, versions:[{version, complete, …}]}`; no `ready` is read). It resolves `aliases.latest` through the freeze path and writes `partitions`. It is RED on a missing or unreadable answer, or on an alias naming a version that is absent or not `complete`, and it never falls back. No consumer passes the flag. | core #7192 |
| P6, cap debt | **implemented, not called.** `node-repo-cap-debt.yml` publishes the repository's whole cap set as one signed `build-caps` event, then files one `Feedback/Feedback` "migrate to latest" item per capped package. The control intake keeps `Hosting/BuildCaps/<package>` (`{package, floor, cap, version, reason}`), written as System, for retention's `capVersions`. It deletes the node of a removed cap. For a cap with no complete deployment in range the lane SENDS a signed `build-host-deploy-requested` (reason `cap:<package>`, the set's tester, portal and runner digests from the registry, plus `coreTestsDigest` when the set has one). A `/hosts` 503 is an outage, retried and never read as an empty list, and a refused request (409) is RED. No satellite calls the lane until its inputs are provisioned. | core #7192 and the cap deploy request, MeshWeaver.Plugins (intake, #4221) |
| P6, capped publish (D2) | **owed with P10.** It is the publish lanes consuming `partitions`, and nothing produces partitions until a consumer adopts `--from-build-hosts`. | — |
| P10 | **blocked on P9.** | — |

`deploy-test-switch-latest` and `package-mesh-version-cap` are decided and stay `proposed` on
their register rows, with P1 to P10 listed as owed; each moves to `in force` once its wiring has
landed.
When P9 is verified and P10 has landed, `build-hosts-follow-latest` is retired in the register.

## 9. Cost

- **CPU moves from GitHub to the cluster.** Core's test shards (as many as `CI_SHARD_TOTAL`
  declares) run on GitHub-hosted runners today (`dotnet-test.yml:253,965`). On a host they
  consume `cibuild` pool vCPU. A host runs one run's stages serially
  (`build-run.py:1682-1699`), so the shards parallelise only because each is its OWN pinned run
  (§2 step 3), and KEDA spreads those runs across the candidate's replicas. The version's
  `MaxReplicas` therefore bounds how many shard runs and dependents' gate runs execute at once; a
  smaller value serialises them and lengthens the test step, it never drops one. Per CD run that adds one full core test pass plus the dependents' affected gates. The
  PR-time `dotnet-test.yml` is unchanged, so this is an ADDED pass, not a moved one, until Q6
  decides otherwise.
- **Idle cost is low.** KEDA scales every version to zero (`minReplicaCount: 0`, `BuildHosts.md`
  §3), except a version still `warming` under its deploy (§2 step 2), which holds one replica. A retained but idle deployment costs its record entry, its locked images and its cache
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
  MeshWeaver.Plugins and satellite gates are green on the deployed build server. Once the policy
  is in force, this is a register exception to `dependents-verify-on-their-side`, scoped to the module-facing `latest`
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
