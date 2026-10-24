---
Name: Build Hosts
Category: Architecture
Description: The platform half of the standing build hosts — one warm host per release channel (build-latest, build-stable) that claims runs from the operations instance's build queue and runs every build and test stage in a fresh process. The follow lane that streams a stage's log into the Actions log and exits with its verdict, the host runner, the build-host chart, and how core's CD hands build-latest each set it arms. The design of record lives in MeshWeaver.Plugins at Hosting/StandingBuildHosts.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="3" width="20" height="7" rx="1"/><rect x="2" y="14" width="20" height="7" rx="1"/><line x1="6" y1="6.5" x2="6.01" y2="6.5"/><line x1="6" y1="17.5" x2="6.01" y2="17.5"/></svg>
---

# Build Hosts

**A build host is a warm Deployment that follows one release channel and runs the fleet's builds in
fresh processes.** `build-latest` runs the newest sealed and armed set; `build-stable` runs the
`stable` pointer (until its promoter exists, the set MeshWeaver.Plugins' `main` has passed on). Each
claims runs from the operations instance's build queue, keeps what is immutable and
content-addressed warm on a shared volume, and starts a new child process for every stage of every
run. Only a run on `build-latest` publishes.

This page is the PLATFORM reference: the four pieces that live in this repository. The design of
record — why, the two layers, routing, the wire contract, what is owed — is
`Hosting/StandingBuildHosts` in MeshWeaver.Plugins (`get Hosting/StandingBuildHosts` on the control
instance's MCP), which builds on `Hosting/BuildService` there. The instance side (the
`/api/builds/*` endpoints, the claim, the stage log, the live Check Runs, the OIDC verification) is
MeshWeaver.Plugins' `Hosting/Build`. See also [Release Channels](../ReleaseChannels),
[Module Build Architecture](../ModuleBuildArchitecture) and [Build Server](../BuildServer).

| piece | file | what it does |
|---|---|---|
| the follow lane | `.github/workflows/build-follow.yml` + `.github/scripts/build-follow.py` | one thin job per stage: streams the stage log into the Actions log, exits with the stage's verdict |
| the host runner | `.github/scripts/build-run.py` (shipped by the chart as `deploy/helm/files/build-run.py`) | refresh the plugins layer → claim → check out → run each declared stage in a fresh process → post progress events |
| the chart | `deploy/helm/templates/build-host/` (`buildHosts.*`, gate `memex.buildHosts`) | per channel a Deployment + KEDA `ScaledObject`; one ServiceAccount, one NetworkPolicy |
| the CD hand-over | `main-cd.yml` job `build-host-latest` + `.github/scripts/build-host-announcement.py`, declared by `.github/build-hosts.json` | in the run that arms a set, a signed event that moves `build-latest`'s record fields to that set |

## 1. The follow lane

While GitHub Actions is still the front door, a heavy leg a repository moves to a host is replaced
by a call of the follow lane, under the **same job name**, so the required context does not change:

```yaml
modules:
  uses: Systemorph/MeshWeaver/.github/workflows/build-follow.yml@main
  permissions: { contents: read, id-token: write }
  with:
    api-url: ${{ vars.MW_BUILD_API }}   # https://<operations instance>/api/builds
    stage: modules
```

It reads `GET {api}/{repo}/{sha}/stages/{stage}/log?after=<seq>&channel=<c>` every 10 s with the
run's **GitHub OIDC token** as the bearer (audience = the API's origin unless `audience` is given),
prints the lines inside a `::group::`, and exits with the stage's verdict. The rules, each pinned by
`build-follow.py --self-test` (run on every core pull request):

- **only `succeeded` is green.** Failed, cancelled, skipped and any state the follower does not
  know are RED, named. A skipped stage is not a passed one.
- **a timeout is RED** (`timeout-minutes`, at most 43 — the job is hard-cut at 45). No verdict in
  time means the run is not judged.
- **an unreadable endpoint is RED, naming why**: 403 at once (the token's claims do not name this
  run), 401 after one token refresh, 404 after a five-minute grace (the run may not be registered
  yet), transport errors and 5xx after six consecutive failures.
- **a `skipped` gap is a warning with the count** — the host keeps the newest 2,000 lines.
- **the log is drained before the verdict**, so the last error lines print first.
- **only `::error`, `::warning` and `::notice` pass through** as workflow commands (annotations on
  the Files tab). Every other `::` line is defused with a leading space: a build must not be able to
  steer the runner that prints its log (`::add-mask::`, `::stop-commands::`, …).

🚨 **The `sha` is the token's.** The endpoint answers only for the repository and sha the OIDC
claims name, and on `pull_request` the token's `sha` is the MERGE commit (`GITHUB_SHA`), not the PR
head. The lane therefore defaults `sha` to `GITHUB_SHA`; the instance must key a PR's run the same
way, or every follow reads 403.

A **stage name is one path segment** (`[a-z0-9][a-z0-9._-]*`); its Check Run is `build/<name>`.
`build/<name>` is accepted as input and normalised.

## 2. The host runner

`build-run.py --host` is the host's main process (`--once` for one cycle, `--self-test`). The loop:

1. **Plugins layer first.** `GET /hosts/inputs` names the sealed plugins publication. When its digest
   differs from the staged one, the host pulls it by digest with ORAS
   (`<registry>/plugins/<source>@<digest>`) into the content-addressed cache
   `/cache/plugins/<hex>/` — an existing complete directory is reused — and only then claims. An
   unreadable `inputs` or a failed pull means **no claim this cycle**: `claim` stamps the host's
   `pluginsDigest` on the run, and a run records exactly what compiled it.
2. **Claim** with `{host, channel, platformVersion, frameworkIdentity, pluginsDigest}`. 204 → sleep;
   200 → the run. The claim's `path` is authoritative (a non-latest run's node id is
   `<sha>-<channel>`).
3. **Check out** `repo@sha` into a fresh work directory (git credentials through the environment,
   never argv or a URL; the shared git mirror as `--reference-if-able` when present).
4. **Run every stage** the repository declares in `.github/build-stages.json`:

   ```json
   {"stages": [{"name": "compile-check", "kind": "build", "command": "python3 …", "timeoutMinutes": 40},
               {"name": "modules", "kind": "test", "command": "…"}]}
   ```

   in a **fresh child process** (its own session, killed as a group on timeout). Stages run in
   declaration order, fail-fast: after a failed stage the rest are finished `failed` with "not run".
   When the claim names `stages`, only those run, and a claimed stage the repository does not declare
   is RED; so is a repository that declares none.
5. **The child's environment** is the host's minus every `*TOKEN*`/`*SECRET*`/`*PASSWORD*`/
   `*CREDENTIAL*` variable and the worker token, plus the run's installation token (`GITHUB_TOKEN`),
   `MW_BUILD_PUBLISH=true|false` (from the claim — a `stable` run never hands anything to a
   registry), `MW_PLUGINS_DIR`, `MW_BUILD_{REPO,SHA,CHANNEL,STAGE,RUN_PATH,IS_TRUNK}`,
   `MW_BUILD_PLATFORM_VERSION`, `MW_BUILD_FRAMEWORK_IDENTITY`, `MeshWeaverRefs` and the composed gate
   host (`MW_BUILD_GATE_HOST`, `MW_BUILD_PORTAL_DOTNET`).
6. **Progress events**, batched (≤ 200 per POST, every 2 s) to
   `POST {api}/{repo}/{sha}/stages/{stage}/events?channel=<c>` with the worker token:
   compiler `file(line,col): error|warning CODE: message` and `::error file=…,line=…::` lines become
   `diagnostic` (deduplicated — MSBuild repeats each in its summary — paths made repository-relative,
   at most 500 per stage); vstest `Passed/Failed/Skipped` and xunit `[FAIL]` lines become
   `test-finished` (a failure carries its first message line and the first repository source line of
   its stack); a `::mw-event::{json}` line is passed through for `unit-started`, `unit-finished`,
   `diagnostic`, `test-finished`, `count` and `line` — **never** `stage-finished` or `run-finished`,
   which only the host decides. Everything else the child prints stays in a 60-line local tail,
   forwarded only when a stage failed with no diagnostic or failed test to explain it. 409 = the run
   is claimed by another host: the child is killed and nothing more is posted.
7. **SIGTERM** (the old replica of a roll) stops claiming; the run in hand finishes.

`run-finished` is posted on the route of the last stage that ran (the route is per stage); a run
refused before any stage posts its refusal on the first stage the claim named.

## 3. The chart

`buildHosts.enabled: false` by default — nothing renders on any estate until its record turns it
on. Every key is read through the `memex.buildHosts` gate, which refuses a half-declared block in
`helm template` naming the key: a channel's tester or portal not a `sha256:` digest, a missing
`platformVersion`, an API URL that is plain http to a non-cluster-local host, a repository with a
tag, no enabled channel.

Per enabled channel (`buildHosts.latest`, `buildHosts.stable`):

- a **Deployment** `build-host-<c>` with no `replicas` (KEDA owns it), surge-first rolling update,
  `terminationGracePeriodSeconds` 3600 so a replaced replica finishes its run, the `cibuild`
  nodeSelector and tolerations, and the composition of Build service §1.4 without a Docker daemon:
  init containers copy the tester's `/app` and the portal's `/app` + `dotnet` out of the set's two
  images **by digest** and ORAS out of its pinned image; the runner composes the gate host
  (`compose-gate-host.sh`) and `exec`s `build-run.py --host`. Readiness is the staged marker the
  runner writes once the plugins layer is in place — a new replica is Ready only when staged;
- a **ScaledObject** `build-host-<c>-scaler`, `minReplicaCount` 0, `maxReplicaCount` per channel,
  two `metrics-api` triggers on `<apiUrl>/queue?channel=<c>` (`claimable` and `running`) combined
  as `claimable + running` (KEDA scaling modifiers, KEDA ≥ 2.12) — a host is never scaled away
  while it holds a run, and a waiting run does not queue behind a running one.

Shared: a ServiceAccount `build-host` with no token mounted and no rights, a NetworkPolicy with no
ingress and egress only to DNS, the build API's pods and 80/443 outside the cluster-private ranges,
and the `memex-build-host-scripts` ConfigMap. The cache is an **existing** RWX claim
(`buildHosts.cacheClaimName`, default `build-cache`); the chart never creates or deletes it.

`deploy/aks/scripts/test-build-hosts.sh` (Chart Gate) asserts the shipped runner is
`.github/scripts/build-run.py` byte for byte, the defaults render nothing, the fixture renders the
shape above for both channels, and the refusals hold; the fixture also runs through every portal
invariant in `check-chart-invariants.sh`.

## 4. Core's CD deploys the host

In the run that **arms** a set, `main-cd.yml` job `build-host-latest` hands `build-latest` that set
(policy `build-hosts-follow-latest`, [Policy Not Prose](../PolicyNotProse)): it resolves the set's
`mw-plugin-test` and `memex-portal-ai` digests from the version tag (never a moving name), re-checks
the set is still the newest armed one (never backwards), and POSTs a signed
`build-host-update-available` event into the control plane's inbox `Hosting/PlatformBuilds` — the
same supported record path `control-first` hands control its next image through:

```json
{"event":"build-host-update-available","deployment":"control","channel":"latest",
 "platformVersion":"3.1.10412","testerImage":"…/mw-plugin-test","testerDigest":"sha256:…",
 "portalImage":"…/memex-portal-ai","portalDigest":"sha256:…","coreSha":"…","pluginsSha":"…",
 "recordFields":{"buildHosts.latest.platformVersion":"3.1.10412",
                 "buildHosts.latest.testerDigest":"sha256:…","buildHosts.latest.portalDigest":"sha256:…"},
 "policy":"build-hosts-follow-latest","trigger":"build-host-latest", …}
```

The receiver writes `recordFields` on the operations record (`deployment`, the record
`.github/control-instance.json` names) and opens that record's Reconcile; the roll is then ordinary
Deployment semantics. 🚨 **Never `kubectl set image`, never a hand-edited Deployment.**

**A no-op until declared, twice.** `.github/build-hosts.json` `enabled: false` (the shipped value)
makes the job write one summary line and send nothing, using no credential. The receiver guards the
same fact on the record: it changes `buildHosts.*` only when the record's `buildHosts.enabled` is
true, and refuses a `platformVersion` older than the one the record names. Turning the hosts on is
two acts, in order: the record's `buildHosts` block through its approval path, then
`enabled: true` here.

The job is outside `delivery-verdict` and `alert-on-failure`: the fleet's delivery does not depend on
a build host, and a red names itself on the job.

## 5. Not done here

- **The receiver of `build-host-update-available`** (the record write and its Reconcile) is the
  operations instance's, in MeshWeaver.Plugins. Until it exists the event must stay unsent —
  which `enabled: false` guarantees.
- **`build-stable`'s roll** ("the daily full run does the same for build-stable") is not wired: the
  stable pointer's mover does not exist yet.
- **The runner image** (bash, python3, git, the .NET SDK) is named by the record
  (`buildHosts.runnerImage`); this repository does not build one yet.
- **The cross-run compile cache** beyond the plugins layer: the runner hands the stages
  `MW_BUILD_CACHE`; keying compile outputs by (source hash, reference-set hash) is the stages' work.
- **The git mirror** is used read-only when present; nothing populates it yet.
