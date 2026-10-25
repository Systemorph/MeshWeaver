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
by a call of the follow lane. 🚨 **A reusable call renames the context**: called from job
`modules-follow`, the lane publishes `modules-follow / modules`, not the `modules` context a
repository's protection may require. The old context is kept by a SHIM job with the old name that
asserts the follow job's result ([Renaming A Required Check](../RenamingARequiredCheck)) — the
route that leaves no unguarded moment:

```yaml
modules-follow:
  uses: Systemorph/MeshWeaver/.github/workflows/build-follow.yml@main
  permissions: { contents: read, id-token: write }
  with:
    api-url: ${{ vars.MW_BUILD_API }}   # https://<operations instance>/api/builds
    stage: modules
    sha: ${{ github.event.pull_request.head.sha || github.sha }}
modules:                                # the REQUIRED context, still published by name
  name: modules
  needs: [modules-follow]
  if: ${{ !cancelled() }}
  runs-on: ubuntu-latest
  timeout-minutes: 5
  steps:
    - run: '[ "${{ needs.modules-follow.result }}" = success ] || { echo "::error::build/modules did not succeed on the host"; exit 1; }'
```

It reads `GET {api}/{repo}/{sha}/stages/{stage}/log?after=<seq>&channel=<c>` every 10 s with the
run's **GitHub OIDC token** as the bearer (audience = the API's origin unless `audience` is given),
prints the lines inside a `::group::`, and exits with the stage's verdict. The rules, each pinned by
`build-follow.py --self-test` (run on every core pull request):

- **only `succeeded` is green.** Failed, `not-run` (the run ended before the stage started),
  cancelled, skipped and any state the follower does not know are RED, named. A skipped or
  never-run stage is not a passed one.
- **a timeout is RED** (`timeout-minutes`, default and maximum 43 — the job is hard-cut at 45). A
  stage still pending when the window ends is not judged. 🚨 **The follow window bounds a JOB, not a
  RUN.** A run's budget is the host's (below — up to 720 min), because a repository's stages run
  sequentially and may sum past one job (MeshWeaver.Plugins declares compile-check 40 + gate 45 =
  85 min). So when the window ends with the follower's stage's verdict already IN but the run not
  terminal, the follower exits with that verdict and a warning that the stage's **Check Run** is the
  authority (a later restart of the stage changes the Check Run, not the finished job). A repository
  whose single stage can outlast the window requires the instance's `build/<stage>` Check Runs
  instead of the follow job's context.
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

A **stage name is one path segment** (`[a-z0-9][a-z0-9._-]*`); its Check Run is `build/<name>` on
`latest` and `build-<channel>/<name>` otherwise, either accepted as input and normalised. The
run's AGGREGATE check is `build` (`build-<channel>` off latest) — no slash, so the two namespaces
are disjoint: no stage, not even one named `verdict`, can publish under the aggregate, and a
follower given the aggregate's name is refused rather than matched to a stage. A Check Run's
`external_id` is the instance's opaque `mwb_<32 hex>`; nothing here parses it.

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
   installs (the index core's `module-fetch` reads; its `frameworkMvid` must name this host's
   framework identity — a missing or different one is no claim), with the host's read-only `mwi_` instance key
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
   `TMPDIR` (and `HOME`) is its sandbox's tmpfs scratch. A PROBE run's claim carries its id (`probe`, beside `isProbe`): it
   rides as `&probe=<id>` on every events POST and on the token request, and the follower and the
   artifact fetch take it as `--probe` for their reads. A `409 stale-claim-id` on the claim means the
   id is spent and nothing was handed out — the host claims again ONCE with a fresh id; a `409
   stale-set` means the host's set is no longer its channel's — it DRAINS (no further claims).
3. **Check out** `repo@sha` — the HOST does it, through the shared git MIRROR
   `/cache/git/<repo>.git`: `git clone --mirror` on first use, `git fetch --prune` (plus the run's
   sha and refs) before every checkout, under an exclusive file lock shared by every replica, then
   a clone `--reference`-ing the mirror into the run's fresh directory. The mirror is written by
   the host alone, only from GitHub, and bound read-only into every sandbox. The credential is the
   run's token from `POST /token {host, path, repo, sha}` → `{token}`: the instance mints it with
   `GitHubAppTokenService.GetScopedInstallationToken(owner, [repo], {"contents":"read"})` — ONE
   repository, read-only, one hour — and only for the host that holds the run's claim. It never
   leaves the host process; git receives it through the environment, never argv or a URL.
   **One merged tree per attempt** (policy `suites-test-fresh-merge`): on a pull-request run the
   host merges the head onto ONE base before any stage — the claim's `mergeBaseSha` verbatim (fetched
   if absent), or, when the claim names none, main's tip resolved ONCE from the repository's mirror
   (its HEAD, fetched just before) — and every stage tests that tree; the base is never re-resolved
   per stage. The plan names it as `"mergeBase":"<40 hex>"` (the instance refuses a PR success whose
   plan names another sha, or none). A conflict is red, never a test of the unmerged head. The
   STAGE DECLARATION is read from the MERGED tree, so a command, dependency or timeout the merge brings
   in is the one that runs. Trunk and probe runs are not merged and carry no `mergeBase`.
4. **Run every stage** the repository declares in `.github/build-stages.json`:

   ```json
   {"stages": [{"name": "compile-check", "kind": "build", "command": "python3 …", "timeoutMinutes": 40},
               {"name": "modules", "kind": "test", "command": "…"}]}
   ```

   A stage may declare `needs` — `plugins`, `outputs`, `nuget` — the shared-cache entries it reads.
   Stages run in declaration order, fail-fast: after a failed stage the rest are finished `failed`
   with "not run". When the claim names `stages`, only those run, and a claimed stage the repository
   does not declare is RED; so is a repository that declares none. A PROBE whose claim carries
   `requestedStages` (empty = every declared stage) runs and plans EXACTLY those; one the
   repository does not declare is RED.

   **The run budget.** A stage's `timeoutMinutes` is a PER-STAGE CAP. The run's deadline is the
   host's `MW_BUILD_RUN_DEADLINE_SECONDS` (`buildHosts.runDeadlineSeconds`, default 7200 s, refused
   outside [600, 43200]) and must COVER the declared stages: the default fits MeshWeaver.Plugins'
   40 + 45 minutes with room for checkout, merge and hand-over. The run's effective budget is the
   smaller of that deadline and the selected stages' summed caps + 10 minutes, and the plan
   DECLARES it as `"budgetMinutes": <10..720>` — the instance sets the attempt's deadline to claim
   time + budget + 15 min. Each stage runs for at most min(its cap, the budget left). When the sum
   of the caps exceeds the deadline the run still starts (stages usually finish well inside their
   caps); the stages the deadline does not reach finish `failed` with "not run — the run deadline
   passed" — red, never skipped.
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
     consumes the aggregate check `build` — "only latest publishes" is that lane's property.
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
     digest, the plugins layer — the registry index's key, else the sealed publication's digest), the canonical compile-check options and allow-list, and the compiler
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
   host uploads each file to the shared CI artifact store with core's `ci-artifact-store.py` (under a
   key carrying the file's sha256, so another attempt's different bytes never replace an
   acknowledged receipt's object)
   (`MW_BUILD_ARTIFACT_STORE`, a `file:` store on the runners' shared share), READS IT BACK and
   re-hashes it, and only then posts `{"kind":"artifact","unit","key","sha256","bytes"}` — before
   the stage's verdict; a failed upload or read-back fails the stage, and artifacts with no store
   configured fail it too. The hand-over is INSIDE the run budget: every store call gets only the
   time left until the run's end, and a hand-over the budget does not cover fails the stage ("the run
   deadline passed during the hand-over") — a stage finishing near the limit never holds its claim
   past the pod's grace. The run's workspace is removed only after every receipt was
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
  `terminationGracePeriodSeconds` = `buildHosts.runDeadlineSeconds` (default 7200, also the
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

### Versioned build servers (contract and chart)

Besides the channel hosts, the chart renders one **versioned build server** per entry in
`buildHosts.versions` (the chart half lands in MeshWeaver.Deploy). The entries come from the record's mesh-owned `buildHostSets`, never typed by
hand. The design is Deploy, Test the Deploy, Switch Latest (the design page in core PR #7182; linked once it lands) §3.

- **Keys.** `v/<version>` holds that version's set token. `alias/<name>` holds the VERSION an alias
  names (`latest`, `candidate`, `previous`; one scalar leaf). `BuildHostSetKeys` parses both;
  `DeploymentContent.BuildHostAliasSet` / `BuildHostVersionSet` resolve them.
- **Token.** The token gains an optional fifth part, the `mw-core-tests` digest. A four-part token
  reads back byte-identically.
- **Chart** (the build-host chart lives in Systemorph/MeshWeaver.Deploy; core's is frozen). Each entry renders Deployment `build-<v>` (dots as dashes, a DNS label) and ScaledObject
  `build-<v>-scaler`, with demand from `/queue?version=<v>`. Its runner (which moves to MeshWeaver.Deploy
  along with the chart) claims, reports and requests its token by `MW_BUILD_VERSION`, never by channel. The selector and pod labels carry the version
  ONLY; the aliases label the Deployment, so a switch moves a label and restarts nothing. An alias
  that names two versions, a repeated version, or a malformed version is refused.
- **Retention.** `BuildHostRetention.Keep` holds every alias target (`previous` while
  `KeepPreviousLatest`), every attempt's recorded version, and every capped package's version;
  `Teardown` is the rest.

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
does not hold them fails the token step, naming it. A red, timed-out or cancelled roll reports
itself on the one open `build-host` issue — from its OWN job (`build-host-stable-report`,
`if: always()`), because a step inside the job it monitors dies with that job's timeout.

**The receiver** (the operations instance, MeshWeaver.Plugins) writes the event's set into
`DeploymentContent.BuildHostSets[<channel>]` — the record's MESH-OWNED member, never the authored
`BuildHosts` block (`MeshWeaver.Deployment.Contract` `BuildHostsBlock` / `BuildHostChannel`) — only
when the record's `BuildHosts.Enabled` is already true and the block runs that channel, and never
backwards; the record change then applies itself (policy `record-change-applies-itself`); the roll is
ordinary Deployment semantics.

🚨 **Why a mesh-owned member, not the authored block.** The record is GitSynced ONE-WAY from its
estate's repository, and the estate's NO PINS rule forbids a record in git from naming a build. So
the set follows the ownership rule a NodeType's compile bookkeeping follows
(`NodeTypeOperationalContent.DeploymentMemberNames`): an export strips `buildHostSets`, an import keeps
the LIVE value — a later authored change to the record can neither roll a host back nor blank it —
and a create starts without it. The author states only `Enabled` and each channel's `MaxReplicas`;
set fields written into the authored block are not read. The claim barrier and the chart both read
the mesh-owned set. 🚨 Each channel's set is stored as ONE scalar token
(`<platformVersion>;<testerDigest>;<portalDigest>;<runnerDigest>`, `BuildHostChannel.ToSetToken`), never
as separate fields: a cross-hub write is a three-way merge that decides every leaf on its own, so with
separate fields a stale hand-over whose version is refused could still land a digest the newer one did
not change. As one leaf, a stale write conflicts as a whole and its retry is refused as older.
`recordFields` is informative. 🚨 **Never `kubectl set image`, never a hand-edited Deployment.**

**A no-op until declared, twice.** `.github/build-hosts.json` `enabled: false` (the shipped value)
makes both jobs write one summary line and send nothing, using no credential; `stable` must also be
listed in its `channels`. The receiver guards the same fact on the record. Turning the hosts on is
two acts, in order: the record's `BuildHosts` block through its approval path, then
`enabled: true` here.

`build-host-image` and `build-host-latest` are outside `delivery-verdict` (the fleet's delivery does
not depend on a build host) and outside `alert-on-failure` (whose `ci-failure` issue recovers on
platform delivery, not on a build host): job `build-host-report` files or comments on the ONE open
`build-host-latest` issue when either fails or is cancelled, and closes it only after a run that actually HANDED
build-latest a set. `build-host-stable.yml` reports on its own `build-host` issue, the same way.

## 5. Every repository on the hosts (H7) — dormant until the instance says so

**Who runs a run's heavy legs is decided ONCE, where the run is admitted** —
`.github/scripts/build-executor.py`, called from MeshWeaver.Plugins' `admission` job and, for a
repository with no admission job of its own, from the `node-repo-build-executor.yml` lane. It answers
`executor=host` only when all three hold:

1. the repository **declares** its stages (`.github/build-stages.json`, at least one) and **claims them
   CI-equivalent** (`"ciEquivalent": true` — a reviewed statement that they check everything the Actions
   legs they replace check; without it the stages can be PROBED on a host, never routed — see *Before a
   repository claims equivalence* below);
2. the operations instance **says** host for it: `GET /api/builds/queue?channel=<c>&repo=<name>`
   answers `"executor":"host"` (the instance's `Hosting:Builds:Executor`, narrowed per repository by
   `Hosting:Builds:HostRepos`);
3. the run is **queued**: one signed `build-request` — the same inbox and HMAC pair as the CI queue's
   `ci-enqueue` (`BUILD_WEBHOOK_URL` / `BUILD_WEBHOOK_SECRET`) — is answered 2xx with its signature
   `verified`. The request carries the run id the follower's OIDC token binds to, and the admission's
   base tip as `ref.baseSha`, so the host merges onto the same main the Actions legs would.

🚨 **Every failure answers `actions`** — today's behaviour — and says why (a `::warning::` for a
failure, a summary line for a deliberate `actions`). The build API base is `vars.MW_BUILD_API` when
set, else the inbox's origin + `/api/builds`; it AND the inbox are https only (plain http only to
localhost), never with credentials in the URL — an http inbox would let an on-path party answer
`verified` and turn the Actions legs off with no host build. Only channel `latest` is queued: a
`build-request` carries no channel yet, so any other channel answers `actions`. Only a pull request or a trunk run
(push / schedule / dispatch on the default branch) may go to a host: the host merges a non-trunk run
onto a base, while Actions tests every other event's commit unmerged, so a branch push or a manual
run on a branch stays on `actions`.

**The required contexts do not change name.** A heavy leg is not replaced by a differently named
job; the caller runs a SIBLING follow job (`build-follow.yml` with `executor:`) and passes its result
into the shared lane as `host-verdict`. 🚨 The follow job **always runs**: with `executor: actions` it
is a no-op success with output `followed=false`. A SKIPPED follow job would skip every job downstream
that still reads `success()` — GitHub propagates a skipped ancestor (actions/runner#2205) — so tagging,
publication and the green ledger would vanish on the very path where nothing runs on a host:

| lane | input | with `executor: host` |
|---|---|---|
| `node-repo-compile-check.yml` | `executor`, `host-verdict` | every heavy step is skipped; the job (the required `… / Compile every NodeType (vs core)`) is green only for `host-verdict == success` |
| `node-repo-gate.yml` | `executor`, `host-verdict` | `plan` and the shards do not run; `verify` (the required `… / Compile + render node repos (MeshWeaver from ACR)`) is green only for `host-verdict == success`, and refuses a plan or shard that ran anyway; output `executed-on: host`, no gate log — a caller's ratchet over that log takes the followed verdict (the host's gate stage runs the ratchet itself) |

A follow job that did not run, was cancelled or failed is RED in the lane — a skipped need is never
a pass. Any `executor` other than `actions` / `host` is RED. With the defaults (`actions`, no
verdict) every lane behaves exactly as before, so a caller that passes nothing is unchanged.

**Before a repository claims equivalence** (review of MeshWeaver.Plugins#4180), its stages must cover
what the Actions legs they replace check — each item is a reason a host green would otherwise mean less:

- **the run's own module bundles as references**, not only `/portal/app` — a NodeType using an API this
  pull request changes in `MeshWeaver.AI` / `Markdown.Collaboration` / `Maps` must compile against it;
- **Code nodes** (`check-scripts.py`), not only NodeTypes;
- **the canonical scripts** through `MW_PLATFORM_SCRIPTS` (read-only, at the host's resolved
  platform) — the sandbox has no GitHub credential for the launcher's fallback fetch;
- **the tester through the host's paths** (`MW_BUILD_GATE_HOST`, `MW_BUILD_PORTAL_DOTNET`), never a
  hard-coded `/host/…`;
- **the warning ratchets** (`--warning-baseline` over the in-mesh compile);
- **the affected selection**, including a verified no-op when nothing is owed;
- **the Tests-area evidence handed back**: the host's gate log as an H9 artifact, so the caller's
  ratchet RE-RUNS the check on it. Until then every caller's `tests-ratchet` is RED on a host run —
  command text is not execution evidence.

**Status (2026-10-25).** Wired, dormant, in MeshWeaver.Plugins (#4180) and the four satellites
(SocialMedia #254, Reinsurance #273, Manufacturing #136, Education #411): every run answers `actions`
until a declaration claims `ciEquivalent` AND the instance says `host` for that repository. No
declaration claims it yet: MeshWeaver.Plugins owes the seven items in its `Hosting/StandingBuildHosts`
(H7), and the satellites declare no stages. **This repository's own CI is NOT wired**, and must not be
until its heavy legs can be stated as stages: they build the platform from source and test it
(`dotnet-test.yml`'s build and shards), while a host composes an already-built platform image and
runs a repository's checks against it — a different workload, not a missing switch. Core declares
no `.github/build-stages.json`, so `build-executor.py` would answer `actions` for it regardless.

**The caller grants.** `build-follow.yml` demands `id-token: write` (the OIDC token is the follower's
credential), so each follow job grants `{contents: read, id-token: write}`; `node-repo-build-executor.yml`
demands `contents: read` only. The arriving callers are recorded as `pending:` rows in
`.github/lane-caller-grants.yml` before any repository wires them (MeshWeaver#3878's ordering).

**Not covered by this switch:** a gate lane's `upload-bake` (the merged `bake-<sha>`
MeshWeaver.Education's install legs download) — a host run hands its artifacts over through H9
receipts instead, so a repository that consumes the bake stays on `actions` until its consumers read
receipts. A re-run of a failed FOLLOW job re-reads the same run's verdict; to rebuild, re-run the
whole workflow (the admission queues a new attempt).

## 6. Not done here

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
- **A host-only publication graph.** `module-publication.py check-callers` (run by
  `node-repo-validate.yml`) requires the publisher's lane and selection outputs to come from
  `node-repo-module-pack.yml`, so an adopter keeps that lane's `select` beside
  `build-host-publication.yml`. Replacing the pack job outright needs the host-publication output
  contract and the caller validator extended together — owed with H7.
- **Keying compile outputs** by (source hash, reference-set hash) is the stages' work: the runner
  promotes what a trunk stage writes under `$MW_BUILD_OUTPUTS/<key>/`, and reads nothing itself.
