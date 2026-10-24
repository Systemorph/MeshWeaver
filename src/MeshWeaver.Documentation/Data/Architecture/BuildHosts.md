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
| the publish-side fetch (H9) | `.github/workflows/build-host-publication.yml` + `.github/scripts/build-artifacts-fetch.py` | a host-executed run's modules, fetched by receipt and verified, staged for the unchanged module publish lane |
| the host runner | `.github/scripts/build-run.py`, shipped in the `mw-build-host` image (`deploy/build-host/Dockerfile`) | refresh the plugins layer → claim → check out → run each declared stage in a fresh `bwrap` sandbox → promote trunk outputs → post progress events |
| the chart | `deploy/helm/templates/build-host/` (`buildHosts.*`, gate `memex.buildHosts`) | per channel a Deployment + KEDA `ScaledObject`; one ServiceAccount, one NetworkPolicy |
| the CD hand-over | `main-cd.yml` jobs `build-host-image`, `build-host-latest`; `.github/workflows/build-host-stable.yml`; `.github/scripts/build-host-announcement.py`, declared by `.github/build-hosts.json` | the runner image per set; in the run that arms a set, a signed event that moves `build-latest` to it; daily, `build-stable` to the main-passed ceiling |

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
    sha: ${{ github.event.pull_request.head.sha || github.sha }}
```

It reads `GET {api}/{repo}/{sha}/stages/{stage}/log?after=<seq>&channel=<c>` every 10 s with the
run's **GitHub OIDC token** as the bearer (audience = the API's origin unless `audience` is given),
prints the lines inside a `::group::`, and exits with the stage's verdict. The rules, each pinned by
`build-follow.py --self-test` (run on every core pull request):

- **only `succeeded` is green.** Failed, `not-run` (the run ended before the stage started),
  cancelled, skipped and any state the follower does not know are RED, named. A skipped or
  never-run stage is not a passed one.
- **a timeout is RED** (`timeout-minutes`, at most 43 — the job is hard-cut at 45). No verdict in
  time means the run is not judged.
- **an unreadable endpoint is RED, naming why**: 403 at once (the token's repository or run does not
  match the build), 401 after one token refresh, 404 after a five-minute grace (the run may not be registered
  yet), transport errors and 5xx after six consecutive failures.
- **a `skipped` gap is a warning with the count** — the host keeps the newest 2,000 lines.
- **the log is drained before the verdict**, so the last error lines print first.
- **the RUN ends the follow, not the stage.** The log page carries `runState` and `runTerminal`;
  while `runTerminal` is false the follower keeps polling even after its stage's verdict — the run
  can restart the stage (a lost host, a re-queue), and the restart is printed — and at
  `runTerminal` it exits with the stage's FINAL `state` (`succeeded` → 0, anything else, `not-run`
  included → 1).
- **only `::error`, `::warning` and `::notice` pass through** as workflow commands (annotations on
  the Files tab). Every other `::` line is defused with a leading space: a build must not be able to
  steer the runner that prints its log (`::add-mask::`, `::stop-commands::`, …).

🚨 **The `sha` is the PR HEAD, never the merge commit.** On `pull_request` GitHub's `github.sha`
is the merge commit, while the build is keyed by the commit the PR's workflow requested — its head.
So callers pass `sha: ${{ github.event.pull_request.head.sha || github.sha }}` (and an empty `sha`
means exactly that). The endpoint binds the OIDC token to the build by the token's `run_id` (the
Actions run recorded on the run node), not by its `sha` claim, so the two never have to agree.

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
   **With `buildHosts.registry.url` set (H8) the layer comes from the REGISTRY's bundle index
   instead** — `GET /api/plugins/bundles/index.json`, what an instance on this framework identity
   installs (the index core's `module-fetch` reads), with the host's read-only `mwi_` instance key
   (a file in the host container, never bound into a sandbox). The layer's KEY is the digest of the
   sorted `package@version[@bundle-hash]` list; a changed bundle is fetched with core's
   `meshweaver-plugin-build module-fetch` (key through `--key-env`, never argv) into
   `/cache/bundles/<h>/`, the set linked under `/cache/plugin-sets/<key>/`, and the key presented
   as `pluginsKey`. An unreadable index, an index for another framework identity, a missing key or
   a failed fetch means no claim this cycle.
2. **Claim** with `{host, channel, platformVersion, frameworkIdentity, pluginsDigest, pluginsKey,
   claimId}`. `claimId` is a fresh uuid per claim attempt; a network error or a 5xx is retried
   with the SAME claimId, so the instance answers the assignment it already made. A host keeps no
   claim across a restart — it claims afresh, never resumes an old one. 204 → sleep; 200 → the run.
   The claim's `path` is authoritative (a non-latest run's node id is `<sha>-<channel>`).
   Right after the stages are known the host posts the PLAN — `{"kind":"plan","stages":[…]}`,
   host-emitted, never accepted from a stage — and the instance refuses a host-run success unless
   every planned stage reported success.
   **The lease.** The instance's lease on a claim lasts 10 minutes past the host's last batch, so
   the host posts at least every 45 s for the whole run (the checkout included): an empty batch
   `{"host","claimId","seq":n,"events":[]}` is the heartbeat. Every batch carries the `claimId` of the
   assignment it belongs to; each stage route's batches are numbered 1, 2, … (`seq`) within that
   assignment, and a retried batch keeps its number. A 409 — not held by this host, or a
   `stale-claim` batch of an earlier assignment — ends the run on this host: the stage is killed
   and nothing more is posted. A 503 (the instance cannot ingest, e.g. it states no redaction
   terms) is retried with backoff, same batch, same `seq` — never a run failure. Every stage's
   `TMPDIR` (and `HOME`) is its sandbox's tmpfs scratch.
3. **Check out** `repo@sha` — the HOST does it, through the shared git MIRROR
   `/cache/git/<repo>.git`: `git clone --mirror` on first use, `git fetch --prune` (plus the run's
   sha and refs) before every checkout, under an exclusive file lock shared by every replica, then
   a clone `--reference`-ing the mirror into the run's fresh directory. The mirror is written by
   the host alone, only from GitHub, and bound read-only into every sandbox. The credential is the
   run's token from `POST /token {host, path, repo, sha}` → `{token}`: the instance mints it with
   `GitHubAppTokenService.GetScopedInstallationToken(owner, [repo], {"contents":"read"})` — ONE
   repository, read-only, one hour — and only for the host that holds the run's claim. It never
   leaves the host process; git receives it through the environment, never argv or a URL.
4. **Run every stage** the repository declares in `.github/build-stages.json`:

   ```json
   {"stages": [{"name": "compile-check", "kind": "build", "command": "python3 …", "timeoutMinutes": 40},
               {"name": "modules", "kind": "test", "command": "…"}]}
   ```

   A stage may declare `needs` — `plugins`, `outputs`, `nuget` — the shared-cache entries it reads.
   Stages run in declaration order, fail-fast: after a failed stage the rest are finished `failed`
   with "not run". When the claim names `stages`, only those run, and a claimed stage the repository
   does not declare is RED; so is a repository that declares none. A run past
   `MW_BUILD_RUN_DEADLINE_SECONDS` finishes its remaining stages `failed`.
5. **Every stage runs in an ENFORCED SANDBOX — a child process is not one.** PR code is untrusted
   and the host is long-lived, so each stage is `bwrap --die-with-parent --new-session
   --unshare-user --unshare-pid --unshare-ipc --unshare-uts --clearenv` with a fresh `/proc`, the
   system directories and every cache (`/cache`, the composed host, the portal and tester trees)
   bound **read-only**, and only three writable places: the run's checkout, its outputs directory
   (`MW_BUILD_OUTPUTS`) and a `tmpfs` scratch (`HOME`, `TMPDIR`, the writable NuGet folder; the
   shared NuGet cache is the read-only fallback folder). There is no network namespace — a build
   restores packages — and the NetworkPolicy bounds where it can go.
   - **The environment is an allow-list**, set with `--setenv` after `--clearenv`: `PATH`, locale,
     the .NET switches, `MeshWeaverRefs`, the composed gate host (`MW_BUILD_GATE_HOST`,
     `MW_BUILD_PORTAL_DOTNET`), and the run's facts (`MW_BUILD_{REPO,SHA,CHANNEL,STAGE,RUN_PATH,
     IS_TRUNK,PUBLISH,PLATFORM_VERSION,FRAMEWORK_IDENTITY}`, `MW_PLUGINS_{DIR,DIGEST}`,
     `MW_BUILD_CACHE`, `MW_BUILD_CACHE_OUTPUTS`, `MW_BUILD_OUTPUTS`). A credential-shaped key is
     refused even if the host itself adds it.
   - **The worker token is a FILE** (`BUILD_TOKEN_FILE`, the mounted Secret) read into the host
     process; `BUILD_TOKEN` in the environment makes the host refuse to start. No sandbox binds the
     file, and the pod mounts no Kubernetes token.
   - **Nothing survives a stage.** `--die-with-parent` and the PID namespace end the whole tree with
     the stage; the host then checks no process remains in another pid namespace, kills any it
     finds, and **exits** (the container is replaced) if one still survives — before it claims again.
   - **The cache is bound per stage, never at its root.** A stage's sandbox is bound, read-only,
     ONLY its own repository's git mirror (`/cache/git/<repo>.git`) and what the stage declares in
     `needs`: the plugins layer (the set and the bundles its links point to), its repository's OWN
     compile outputs (`/cache/outputs/<repo>/` — outputs are scoped by repository) and the NuGet
     cache. At start the host proves it: from a sandbox built for a public scratch repository it must
     read its own mirror and FAIL to read a private scratch repository's mirror and outputs and a file
     at the cache root, or the host refuses to start, naming which.
   - **The reference set is keyed by BOTH digests.** The composed gate host is staged once into
     `/cache/refsets/<sha256(tester|portal)>/host` and bound read-only from there
     (`MW_BUILD_REFSET_KEY` names it); a change of either image is a new entry, never a reuse.
   - **The network boundary is checked at start.** From INSIDE a sandbox the host probes the
     instance metadata service (`http://169.254.169.254/metadata/instance`, `Metadata: true`), a
     cluster-private address (`MW_BUILD_BOUNDARY_PRIVATE_URL`, default `http://10.0.0.1/`) and the
     Kubernetes API (`https://kubernetes.default.svc`). If ANY answers — any HTTP status — the host
     refuses to start, naming which; a probe that cannot report is a refusal too.
   - **No sandbox, no host.** At startup the host creates the namespaces once; if the node refuses
     them (seccomp, AppArmor, a kernel without unprivileged user namespaces) it exits non-zero
     naming why. It never runs a stage unsandboxed.
   - **The hosts' ONE cloud credential is read-only**: Azure workload identity on the `build-host`
     ServiceAccount, granted **AcrPull only**, to pull the plugins publication. Its projected token
     (`AZURE_FEDERATED_TOKEN_FILE`) is mounted into the HOST container alone (the pod's
     `azure.workload.identity/skip-containers` names every other container) and is bound into no
     sandbox; the host exchanges it (Entra client assertion → ACR `/oauth2/exchange`) for a pull
     credential written to a host-private file for the one ORAS pull, then deleted.
   - `MW_BUILD_PUBLISH=true|false` (from the claim) is **informational**: a host holds no
     publication credential and mounts no bake share. Publication stays in the publish lane, which
     consumes `build/verdict` — "only latest publishes" is that lane's property.
6. **Cache promotion is the host's: trunk-only, BUILD stages only, recomputed keys.** Only
   compilation is reused across runs — never a test verdict — so only a SUCCEEDED `build` stage of
   a trunk run (`isTrunk: true`) has its outputs promoted. A stage writes each reusable output to
   `$MW_BUILD_OUTPUTS/<key>/` with a `.cache-key.json` naming its inputs, and the host promotes it
   into `<cache>/outputs/<key>/` only when it RECOMPUTES that key from those inputs itself:
   - **a module** (`{"kind":"module","entry":{…}}`): core's `module-build-key.py` — the sources, the
     dependency closure, `Directory.Build.props`/`.targets`, `Directory.Packages.props`, the project
     file and the toolchain identity — run with the HOST's tester and portal digests;
   - **a NodeType** (`{"kind":"nodetype","sources":[…],"options":"…","allowList":"…"}`): the
     declared source set (each file's hash), the reference-set identity (framework identity, portal
     digest, the plugins key), the canonical compile-check options and allow-list, and the compiler
     (the tester digest).
   A stage computes the same key with `python3 /opt/build-host/build-run.py --cache-key spec.json`.
   An existing key is kept; a name that is not the recomputed key, a symlink, or a path outside the
   checkout is not promoted. A pull request's outputs **never** enter the shared cache.
   **PostgreSQL** is never shared: a stage that needs one runs
   `eval "$(/opt/build-host/sandbox-postgres.sh)"` — a disposable server inside its own sandbox, data
   on the stage's tmpfs, a random per-stage password, a Unix socket only (`listen_addresses=''`, no
   TCP), torn down with the stage's process tree.
7. **Artifacts are handed over by the HOST (H9).** A stage leaves every module bundle (and its
   `<module>.publication.json` record, in the module pack lane's format) and the gate bake under
   `$MW_BUILD_ARTIFACTS/<unit>/` (writable; outside the outputs cache). After the stage exits, the
   host uploads each file to the shared CI artifact store with core's `ci-artifact-store.py`
   (`MW_BUILD_ARTIFACT_STORE`, a `file:` store on the runners' shared share), READS IT BACK and
   re-hashes it, and only then posts `{"kind":"artifact","unit","key","sha256","bytes"}` — before
   the stage's verdict; a failed upload or read-back fails the stage, and artifacts with no store
   configured fail it too. The run's workspace is removed only after every receipt was
   acknowledged; a workspace whose receipts were not is kept and swept after two run deadlines.
   On the publishing side, `build-host-publication.yml` (OIDC, the same gate as the log) reads
   `GET /api/builds/{repo}/{sha}/artifacts?channel=`, fetches every object by its receipt
   (`build-artifacts-fetch.py`), verifies hash AND size against the receipt, and uploads them as
   `module-publication-<lane>-host` — so the UNCHANGED `node-repo-module-publish.yml` publishes a
   host-executed run's modules from the same staged shape as an Actions-built one. It is its own lane
   because it needs `id-token: write`, and adding that to the publish lane would be a new hard
   requirement on every existing caller.
8. **Progress events**, batched (≤ 200 per POST, every 2 s) to
   `POST {api}/{repo}/{sha}/stages/{stage}/events?channel=<c>` with the worker token:
   compiler `file(line,col): error|warning CODE: message` and `::error file=…,line=…::` lines become
   `diagnostic` (deduplicated — MSBuild repeats each in its summary — paths made repository-relative,
   at most 500 per stage); vstest `Passed/Failed/Skipped` and xunit `[FAIL]` lines become
   `test-finished` (a failure carries its first message line and the first repository source line of
   its stack); a `::mw-event::{json}` line is passed through for `unit-started`, `unit-finished`,
   `diagnostic`, `test-finished`, `count` and `line` — **never** `stage-finished` or `run-finished`,
   which only the host decides. Everything else the stage prints stays in a 60-line local tail,
   forwarded only when a stage failed with no diagnostic or failed test to explain it. 409 = the run
   is claimed by another host: the stage is killed and nothing more is posted.
9. **SIGTERM** (a roll or a scale-in) stops claiming; the run in hand finishes within the pod's
   grace period, which equals the run deadline.

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
  `terminationGracePeriodSeconds` = `buildHosts.runDeadlineSeconds` (default 3600, also the
  runner's run deadline) so a replaced or scaled-in replica finishes its run, the `cibuild`
  nodeSelector and tolerations, run as the runner image's unprivileged user (1654, `fsGroup`
  1654), and the composition of Build service §1.4 without a Docker daemon: init containers copy
  the tester's `/app` and the portal's `/app` + `dotnet` out of the set's two images **by digest**
  and ORAS out of its pinned image; the main container is the set's `mw-build-host` **by digest**
  (`runnerRepository` + the channel's `runnerDigest`), which composes the gate host and `exec`s its
  own `build-run.py --host` — the chart carries no copy of either script. Readiness is the staged
  marker the runner writes once the plugins layer is in place — a new replica is Ready only when
  staged. The worker token is the `workerTokenSecret` mounted as a FILE at
  `/run/build-worker/token`; with `workloadIdentity.clientId` set, the ServiceAccount carries
  `azure.workload.identity/client-id` and the pod `azure.workload.identity/use: "true"`, the
  identity token restricted to the `build` container. The container's seccomp / AppArmor profiles
  can be stated (`buildHosts.sandbox.*`) for what `bwrap` needs on the pool's nodes — measured, not
  assumed;
- a **ScaledObject** `build-host-<c>-scaler`, `minReplicaCount` 0, `maxReplicaCount` per channel,
  ONE `metrics-api` trigger on `<apiUrl>/queue?channel=<c>` with `valueLocation: demand`
  (= claimable + running, so the metric never drops to zero while a host holds a run) and a
  scale-down stabilization window (`scaleDownStabilizationSeconds`, default 300) longer than a
  claim's round trip.

**A host holds no publication credential, no registry token and no bake share.** The gate refuses
`buildHosts.pluginsPullSecret` and `buildHosts.bakeShares`; `imagePullSecret` is the kubelet's image
pull and never enters the pod's filesystem. The plugins publication is pulled with the workload
identity above (AcrPull, read-only) or anonymously.

Shared: a ServiceAccount `build-host` with no token mounted and no rights, a NetworkPolicy with no
ingress and exactly three egress rules — DNS to the CLUSTER RESOLVER only (`kube-system`
`k8s-app=kube-dns`), the build API's pods on their port, and 80/443 outside the excluded ranges — the
cluster-private ranges, link-local `169.254.0.0/16` (the instance metadata service, where a node
identity's tokens are minted) and `168.63.129.16/32` (WireServer). Stages share the pod's network
namespace, so that policy — not the sandbox — is the network boundary. There is no shared database
container: the gate refuses `buildHosts.postgres` (a stage's database is disposable, in its own
sandbox). With `buildHosts.registry.url` the read-only `mwi_` instance key is mounted from
`registry.instanceKeySecret` as a FILE at `/run/registry-key/key`.
The gate host is composed into `/host/app` (the composer replaces its output directory; the
emptyDir's mount point cannot be removed). The chart carries
no ConfigMap. The cache is an **existing** RWX claim
(`buildHosts.cacheClaimName`, default `build-cache`); the chart never creates or deletes it.

`deploy/aks/scripts/test-build-hosts.sh` (Chart Gate) asserts the runner ships in ONE place (the
Dockerfile copies `.github/scripts`' scripts, installs bubblewrap, runs the self-test, names no
`:latest`; the chart has no copy), the defaults render nothing, the fixture renders the
shape above for both channels, and the refusals hold; the fixture also runs through every portal
invariant in `check-chart-invariants.sh`.

## 4. Core's CD deploys the host

**The runner image.** `main-cd.yml` job `build-host-image` builds `deploy/build-host/Dockerfile`
(the .NET 10 SDK, bash, python3, git, bubblewrap, and THIS commit's `build-run.py` +
`compose-gate-host.sh`, whose self-test runs in the build) for linux/amd64 and pushes
`mw-build-host:<version>` — the version every image of the set carries — for every published
platform build. It is outside the promotion (the set is complete without it) and under
`alert-on-failure`. Nothing names it by a moving tag: the hosts get it BY DIGEST with the rest of the
set.

**`build-latest` follows the armed set.** In the run that **arms** a set, job `build-host-latest`
hands `build-latest` that set (policy `build-hosts-follow-latest`, [Policy Not Prose](../PolicyNotProse)):
it resolves the set's `mw-plugin-test`, `memex-portal-ai` and `mw-build-host` digests from the
version tag (never a moving name), re-checks the set is still the newest armed one (never
backwards), and POSTs a signed `build-host-update-available` event into the control plane's inbox
`Hosting/PlatformBuilds` — the same supported record path `control-first` hands control its next
image through:

```json
{"event":"build-host-update-available","deployment":"control","channel":"latest",
 "platformVersion":"3.1.10412","testerImage":"…/mw-plugin-test","testerDigest":"sha256:…",
 "portalImage":"…/memex-portal-ai","portalDigest":"sha256:…",
 "runnerImage":"…/mw-build-host","runnerDigest":"sha256:…","coreSha":"…","pluginsSha":"…",
 "recordFields":{"buildHosts.latest.platformVersion":"3.1.10412","buildHosts.latest.testerDigest":"sha256:…",
                 "buildHosts.latest.portalDigest":"sha256:…","buildHosts.latest.runnerDigest":"sha256:…"},
 "policy":"build-hosts-follow-latest","trigger":"build-host-latest", …}
```

**`build-stable` follows the main-passed ceiling — daily.** Until core's `stable` promoter exists,
`stable` IS the newest sealed set MeshWeaver.Plugins' own `main` has passed on. Its own workflow,
`.github/workflows/build-host-stable.yml` (daily `schedule`, 05:41 UTC; `stable` is refused on any
other trigger), resolves that ceiling with `resolve-platform.py --no-registry --passed-on-main
Systemorph/MeshWeaver.Plugins` through a fleet-reader App token, resolves the same three digests by
the set's version, and sends the same event for channel `stable`. 🚨 It is deliberately NOT a job of
`main-cd.yml`: the platform deploy reads no MeshWeaver.Plugins verdict (policy
`platform-module-deploy-separate`, held by `test-cd-steps.py`'s separation guard), and the
main-passed ceiling is one; here it only decides which set a build host runs and gates no platform
roll. The ceiling is read from MeshWeaver.Plugins' PRIVATE Actions runs and their annotations, so
the token is requested with `permission-actions: read` and `permission-checks: read` — an App that
does not hold them fails the token step, naming it. A red roll reports itself on the one open
`build-host` issue.

**The receiver** (the operations instance, MeshWeaver.Plugins) writes the event's set into
`DeploymentContent.BuildHosts.Channels[<channel>]` (the typed record block,
`MeshWeaver.Deployment.Contract` `BuildHostsBlock` / `BuildHostChannel`) — only when the record's
`BuildHosts.Enabled` is already true, and never backwards — and the record change then applies
itself (policy `record-change-applies-itself`); the roll is ordinary Deployment semantics.
`recordFields` is informative. 🚨 **Never `kubectl set image`, never a hand-edited Deployment.**

**A no-op until declared, twice.** `.github/build-hosts.json` `enabled: false` (the shipped value)
makes both jobs write one summary line and send nothing, using no credential; `stable` must also be
listed in its `channels`. The receiver guards the same fact on the record. Turning the hosts on is
two acts, in order: the record's `BuildHosts` block through its approval path, then
`enabled: true` here.

`build-host-image` and `build-host-latest` are outside `delivery-verdict` (the fleet's delivery does
not depend on a build host) and UNDER `alert-on-failure` (a host left on an old set is paged, not
unattended); `build-host-stable.yml` reports on its own `build-host` issue.

## 5. Not done here

- **The receiver of `build-host-update-available`** (the record write) and the **`POST /token`
  route** (minting `GetScopedInstallationToken(owner, [repo], {"contents":"read"})` for the ONE
  repository of the run, only for the host that holds its claim) are the operations instance's, in
  MeshWeaver.Plugins. Until the receiver exists the event must stay unsent — which `enabled: false`
  guarantees.
- **The fleet-reader App's grant** is Contents / Metadata / Pull requests today; the stable roll
  needs Actions: read and Checks: read on MeshWeaver.Plugins. Widening it is a maintainer act;
  until then `stable` stays out of `.github/build-hosts.json` `channels`.
- **Whether the `cibuild` nodes allow `bwrap`'s namespaces** is not measured on AKS. What WAS
  measured (the `mw-build-host` image built from this tree, run under Docker on an Ubuntu 24.04
  kernel 6.8): the startup probe REFUSED in every configuration — the default seccomp profile
  ("No permissions to create new namespace"), and seccomp + AppArmor unconfined, as root, and even
  privileged ("setting up uid map: Permission denied") — because that kernel sets
  `kernel.apparmor_restrict_unprivileged_userns=1`, which strips an unconfined process's new user
  namespace of the capabilities to write its uid map. So the host fails closed as designed, and a
  node image on that kernel line needs either that sysctl at 0 on the build pool or an AppArmor
  profile that grants `userns` to the build container (`buildHosts.sandbox.appArmorProfile`
  `Localhost`) — a node-pool decision, not a chart default.
- **Retention of pinned digests.** A host runs digests the record pins for as long as its channel
  stays on a set; the ACR purge counts newer builds, so a quiet `stable` channel's images must be
  protected the way other pinned digests are (`lock-pinned-digests.py`,
  [Pinned Image Retention](../PinnedImageRetention)) — not wired for `mw-build-host` or the
  build-host record's digests yet.
- **Keying compile outputs** by (source hash, reference-set hash) is the stages' work: the runner
  promotes what a trunk stage writes under `$MW_BUILD_OUTPUTS/<key>/`, and reads nothing itself.
