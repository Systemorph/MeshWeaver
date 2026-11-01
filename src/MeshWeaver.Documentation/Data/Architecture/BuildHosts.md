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

> **Target shape:** [Deploy, Test the Deploy, Switch Latest](../DeployTestSwitchLatest) (DECIDED
> DESIGN, not wired; policy `deploy-test-switch-latest`, decided and `proposed` until its wiring lands) generalises the channels on this page into one
> versioned deployment per platform set plus a `latest` alias that moves only after the set passed
> its tests ON its deployment. Until that wiring lands, this page describes what runs.

| piece | file | what it does |
|---|---|---|
| the follow lane | `.github/workflows/build-follow.yml` + `.github/scripts/build-follow.py` | one thin job per stage: streams the stage log into the Actions log, exits with the stage's verdict |
| the publish-side fetch (H9) | `.github/workflows/build-host-publication.yml` + `.github/scripts/build-artifacts-fetch.py` | a host-executed run's modules, fetched by receipt and verified, checked with the publisher's own check, staged for the unchanged module publish lane; the publisher's lane and selection source on a host run (§5) |
| the ratchet-side fetch (H9) | `.github/actions/host-gate-log` + `.github/scripts/host-gate-log.py` | a node repo's host-run gate log, fetched by receipt and verified, for the repository's own Tests-area ratchet (§2 step 7) |
| the host runner | MeshWeaver.Deploy `.github/scripts/build-run.py`, shipped in the `mw-build-host` image (MeshWeaver.Deploy `deploy/build-host/Dockerfile`) | refresh the plugins layer → claim → check out → run each declared stage in a fresh `bwrap` sandbox → promote trunk outputs → post progress events |
| the chart | `deploy/helm/templates/build-host/` (`buildHosts.*`, gate `memex.buildHosts`) | per channel a Deployment + KEDA `ScaledObject`; one ServiceAccount, one NetworkPolicy |
| the hand-over | MeshWeaver.Deploy `build-host.yml` (a 15-minute `schedule` that reads the sets `main-cd.yml` sealed from the registry; jobs `image`, `handover`, `report`), `build-host-stable.yml`, `build-host-announcement.py`, declared by its `.github/build-hosts.json` | the runner image per set; on every hand-over, a signed event that moves `build-latest` to the newest armed set; daily, `build-stable` to the main-passed ceiling |

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
  authority (a later restart of the stage changes the Check Run, not the finished job).
- 🔁 **a window that ends with NO verdict is resumed, not stranded** (the follow lifecycle,
  MeshWeaver.Plugins#4501). The host prepares and then runs the stages one after another, so a stage
  can end hours after admission (MeshWeaver.Plugins: up to 195 min) while its follow job is cut at
  45. Such a window exits `NO_VERDICT_EXIT` (3, never 1), and the lane fails in its own step, named
  exactly `No verdict within the follow window`. That NAME is a wire contract: once the stage's
  verdict is in, the operations instance (MeshWeaver.Plugins `Hosting/Build`, `BuildFollowResume`)
  reads each requesting run's jobs, and when it finds that step failed and the run completed, it
  re-runs the run's failed jobs — once per run and attempt. The re-run follower reads a terminal run
  at its first poll and exits with the stage's verdict at once, and GitHub re-runs its dependents
  with it, so the shim job owning the required context re-judges with the real verdict. A follower
  still running when the verdict lands reads it itself, and needs nothing. No job waits past its
  cap, and the only waiting is the operations instance's own projection pass, in code. A red read IN
  the window (the stage failed) is an ordinary red (1) and is never re-run. `build-follow.py
  --check-workflow .github/workflows/build-follow.yml` (core CI) holds the step and the exit code
  together.
- **an unreadable endpoint is RED, naming why**: 403 at once (the token's repository or run does not
  match the build), 401 after one token refresh, 404 after a five-minute grace (the run may not be registered
  yet), transport errors and 5xx after six consecutive failures.
- 🚨 **a redirect is a refusal, never followed.** Every call of the shared `Http` transport carries a
  bearer (the OIDC token on a log read, the runtime's request token on the OIDC mint), and urllib's
  default handler re-sends the `Authorization` header to whatever a 3xx names — another host, or an
  https→http downgrade; the `https://` check on `api-url` guards the first hop only. So the transport
  never follows one: it answers the 3xx as its status before any byte reaches the target, and the
  follower turns it into a RED at once, without a retry (MeshWeaver.Plugins#4514; the same rule as
  MeshWeaver.Plugins `scripts/build-probe.py`). The self-test proves it against the real transport
  over loopback — a cross-origin 302 whose target records every `Authorization` it receives, with a
  direct call to that target as the control — plus an https→http 301 against the handler and a 302
  against the follower. Every consumer of the transport inherits it (`build-artifacts-fetch.py`,
  `host-shard-results.py`, MeshWeaver.Plugins `scripts/host-gate-log.py`), and each already reads a
  non-200 status as RED.
- **a `skipped` gap is a warning with the count** — the host keeps the newest 2,000 lines.
- **the log is drained before the verdict**, so the last error lines print first.
- **the RUN ends the follow, not the stage.** The log page carries `runState` and `runTerminal`;
  while `runTerminal` is false the follower keeps polling even after its stage's verdict — the run
  can restart the stage (a lost host, a re-queue), and the restart is printed — and at
  `runTerminal` it exits with the stage's FINAL `state` (`succeeded` → 0, anything else, `not-run`
  included → 1).
- **progress is printed even when the log is quiet**: while the stage is pending or running, the
  log page's one-line `heartbeat` — the stage, its elapsed time, units/tests/errors, what is left of
  it, and the run's done/total, percent and estimate (the operations instance's run board) — is
  printed at most every 30 s (`HEARTBEAT_SECONDS`), marked `⏱` so it is never a command. An
  instance that serves no heartbeat gets none printed; nothing is computed on the runner.
- **the step summary carries the run's stage board** — the page's `board`, the same markdown as the
  `build` Check Run's summary — and a link to the run's live page on the operations instance
  (`<origin>/<runPath>`, linked only when `runPath` is `Hosting/Builds/<repo>/<id>` and the API is
  https), on a pass and on a red alike. The admission (`build-executor.py`) links the same page in
  its own summary the moment the run is queued.
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
   **The index is read when a module is HANDED OVER, not on every turn.** `GET /hosts/inputs`
   also answers `modules: {key, packages, via, at}`. The key is a digest of what the operations
   instance's registry reconciler last read as served: a module's lane hands its bundle to the
   registry, the registry broadcasts `module-published`, and the reconciler rewrites
   `Plugins/_RegistryReconcileLedger`. The host reads `inputs` on every loop turn it already makes,
   an idle one included, and reads the index only when `modules.key` moved since it staged its
   layer. So an idle host installs a handed-over module ahead of the next build. Two cases read
   the index although the key did not move:
   - there is no signal (a null `modules`, unreadable inputs): the index is read on every turn;
   - the staged layer is no longer intact on the cache (install-on-miss): it is installed again,
     whatever the key says.

   A START is not a third case. The installed layer is recorded per framework identity at
   `/cache/plugin-sets/current-<identity digest>.json`. A restarted or new host ADOPTS that record
   without reading the index when the layer is intact and its recorded key matches the available
   `modules.key`. It reads the index only when a hand-over moved the key since then, which is the
   ordinary rule above.
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

   **A prepared server, never a prepared run.** A declaration whose command stages, resolves,
   pulls or restores per run is REFUSED, and the refusal names where the step belongs:

   | refused step | where it belongs |
   |---|---|
   | `resolve-platform.py` | the claim's set |
   | `fetch-refs.py`, `platform-mount` | the init containers |
   | `module-fetch` | the plugins layer |
   | an image pull or pre-pull | the host image |
   | `dotnet restore`, a NuGet install | the warm NuGet cache |
   | an apt/apk/dnf, pip or npm install | the host image |

   Every declaration in the fleet passes it.

   **Prepared inputs.** Some inputs are built from the repository's own sources but are not the
   run's subject: MeshWeaver.Plugins' module-libraries shelf (`MW_BUILD_MODULE_LIBS`) and its Razor
   build extension (`MW_BUILD_EXTENSIONS`). A repository declares them beside its stages as
   `prepared: [{name, env, key, build, timeoutMinutes}]`. Before the stages, the host runs the
   repository's own `key` command in the sandbox and looks for the input under
   `/cache/prepared/<repo>/<name>/<sha256(platform|key)>/`, stamped `.source-key`. Two command
   contracts are enforced, and a run whose command misses either is RED, naming the input:

   - `key` must print the source key as the FINAL line of its stdout: exactly 64 lowercase
     hexadecimal characters (a SHA-256 of the sources the input is built from). A 40-character
     `git rev-parse HEAD` does not qualify.
   - `build` must write its output into `$MW_PREPARE_OUT` — at least one non-hidden entry. A build
     that succeeds but writes only to its usual output directory does not qualify.

   With the key in hand:

   - **already installed:** it is bound read-only and nothing is prepared;
   - **a trunk run, key not installed:** it is built once (`build`, against the image's platform
     source `MW_PLATFORM_SOURCE`, which is the set's `src/`, `tools/` and root build files) and
     installed persistently. Every push to main queues a trunk run, so the server follows main;
   - **a pull request whose sources differ:** it is built for that run only, reported as
     `MWPREP002`, and never installed.

   A failed build, a key command that answers no key, or a missing input on a host without
   platform source is RED, naming the input.

   **The run budget.** A stage's `timeoutMinutes` is a PER-STAGE CAP. The run's deadline is the
   host's `MW_BUILD_RUN_DEADLINE_SECONDS` (`buildHosts.runDeadlineSeconds`, default 7200 s, refused
   outside [600, 43200]) and must COVER the declared stages: the default fits MeshWeaver.Plugins'
   40 + 45 minutes with room for checkout, merge and hand-over. The run's effective budget is the
   smaller of that deadline and the selected stages' summed caps, plus each declared prepared input's
   build cap (its `timeoutMinutes`, default 20) and a five-minute key-command allowance — a cache
   miss builds the input inside the run — plus 10 minutes, and the plan
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
   (`MW_BUILD_OUTPUTS`), a `tmpfs` scratch (`HOME`, `TMPDIR`) and the run's own NuGet package
   folder (`NUGET_PACKAGES`, in the run's workspace so the host can see it; the shared NuGet cache
   is the read-only fallback folder). A package a build still had to restore is reported as a
   warning (`MWPREP001`, naming it). On a succeeded TRUNK build stage it is verified and then
   promoted into `/cache/nuget`, so the next run restores nothing. Verification makes two
   separate checks and requires no symlinks: the ARCHIVE's SHA-512 against `.nupkg.sha512`, and
   NuGet's CONTENT hash (which leaves the package signature out, so a signed package's differs
   from its archive hash) against `.nupkg.metadata`'s `contentHash`. What is promoted is rebuilt
   from that verified archive, never copied from the files the stage could write. A pull request never writes the cache. There is no network namespace — a build
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
   🚨 **A receipt carries the OUTCOME of the stage that handed it over** — `stageState`,
   `succeeded` or `failed` (MeshWeaver.Deploy#52). A RED stage hands its artifacts over too, marked
   `failed`, so the evidence of why it is red (a shard's `.trx` files, markers and logs) reaches
   consolidation; its verdict stays red. A receipt never means "the stage succeeded" by itself, so
   every reader reads the marker: `build-artifacts-fetch.py` takes ONLY `succeeded` receipts by
   default — a failed stage's artifacts are never published and never ratcheted as a green gate log,
   and a selection whose every receipt is failed is RED naming the stage — while
   `host-shard-results.py` asks for failed ones explicitly (`include_failed=True`). A value other than
   `succeeded` or `failed` — an explicit `null` included — is refused in EVERY mode, never defaulted to
   green and never dropped silently beside a succeeded receipt. Only a receipt WITHOUT the field
   predates the marker: such a host handed over only a green stage's files, plus a red stage's
   `diagnostics/` unit, which the build API marks from the stage's own recorded verdict. The order the
   halves land in: the readers (this repository and MeshWeaver.Plugins' build API and
   `scripts/host-gate-log.py`) first, then the host (MeshWeaver.Deploy `build-run.py`), which is the
   only half that starts handing a red stage's files over.
   On the RATCHET side, a node repository whose `gate` stage ran on a host reads that stage's
   `gate-log/gate.log` through the composite action `.github/actions/host-gate-log` (`api-url` = the
   build-executor lane's output, `store` = `vars.MW_ARTIFACT_STORE`, the job holding
   `id-token: write`). It fetches the `gate` stage's receipts under the `gate-log/` unit with the same
   `build-artifacts-fetch.py` (`--unit-dir gate-log`: the host hands over every file under
   `$MW_BUILD_ARTIFACTS` after each successful stage, so the gate stage's receipts repeat an earlier
   modules stage's bundles), and `host-gate-log.py` then refuses anything but ONE non-empty `gate.log`
   whose LAST `=== mw-plugin-test summary ===` block ends in the tester's terminal verdict
   (`ALL GREEN.` / `GREEN — …` / `GATE FAILED …`) — nothing handed over, a log handed over by a
   FAILED gate stage (`stageState: failed`), an empty, truncated or foreign log, or a second file in the unit are each RED. The repository then runs the
   SAME `check-test-suites.py --gate-log` it runs over an Actions gate's log, so the ratchet reads the
   tester's own console on both executors — never the host's verdict, never the declared command's
   text. One copy for every satellite; MeshWeaver.Plugins#4482 built the same read for Plugins.
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
- a **ScaledObject** `build-host-<c>-scaler`, `minReplicaCount` the channel's declared floor
  (default 0 — see "Declared host floor" below), `maxReplicaCount` per channel,
  ONE `metrics-api` trigger on `<apiUrl>/queue?channel=<c>` with `valueLocation: demand`
  (= claimable + running, so the metric never drops to zero while a host holds a run) and a
  scale-down stabilization window (`scaleDownStabilizationSeconds`, default 300) longer than a
  claim's round trip.

### Declared host floor

A channel's floor is DECLARED on the record, never derived (policy `declared-not-derived`):
`BuildHostChannel.MinReplicas` (default 0, scale to zero) renders as the channel's `minReplicas`,
which the chart writes as the ScaledObject's `minReplicaCount`; `MaxReplicas` stays the one
declaration of capacity and the admission cap. A floor of one keeps a host warm, so an admitted run
is claimed without first waiting for a node and a pod to start. The chart refuses a floor below 0
or above the channel's `maxReplicas`. A floor is paid for around the clock, so it is a record
change the owner approves like any other, and the record says why it holds the value it does.

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
chart has no copy; the image and its self-tests are MeshWeaver.Deploy's `build-host.yml`), the defaults render nothing, the fixture renders the
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

## 4. MeshWeaver.Deploy deploys the host, from the platform's sealed sets

Core's CD BUILDS and SEALS the product image set and nothing else (core slimming). The build-host
deployment lives in [MeshWeaver.Deploy](https://github.com/Systemorph/MeshWeaver.Deploy), and the platform does not
trigger it: core dispatches to NO repository (policy `dependents-verify-on-their-side`, held by
`PlatformReleaseNotifyGuard.CoreDispatchesToNoRepository`). Deploy PULLS. Every 15 minutes its
`build-host.yml` (`schedule`) reads the sets `main-cd.yml` sealed from the registry they were sealed
into — a set is PROMOTED when `mw-plugin-test` carries its version tag and ARMED when
`memex-portal-ai` does, and both tags sit on the manifest whose `staging-<core7>-<run id>` tag names
the commit and the CD run (`platform-set-dispatch.py discover`). The newest armed, then the newest
promoted, set that still has no `mw-build-host:<version>` becomes the hand-over (schema 1, the same
contract a `workflow_dispatch` re-run reads). Deploy resolves the commit in core's public history,
proves it is on `main`, and resolves every version in the registry before it builds or signs
anything; a red reports on Deploy's own `build-host-latest` issue. The cost of the pull is one tick:
an armed set reaches `build-latest` within about 15 minutes, and no platform roll waits on it — the
build host is outside the platform's delivery.

**The runner image.** Deploy's `build-host.yml` builds its `deploy/build-host/Dockerfile`
(the .NET 10 SDK, bash, python3, git, jq, bubblewrap, Deploy's `build-run.py`, and the platform's
`compose-gate-host.sh`, `sandbox-postgres.sh`, `module-build-key.py` with the sibling it loads,
`node-repo-scope.py`, `ci-artifact-store.py` and `meshweaver-plugin-build`). The Dockerfile fetches
every source BY COMMIT inside the build (`DEPLOY_COMMIT`, and `PLATFORM_COMMIT` = the set's own
commit), so its context is empty and nothing is staged; the runner's and `module-build-key.py`'s
self-tests run in the build. Job `image-arch` builds it natively for linux/amd64 and linux/arm64
(one runner per architecture, pushed by digest), and job `image` joins the two into one index under
`mw-build-host:<version>` — the version every image of the set carries — once per version, mirrored
to the fleet registry. Both architectures are required: the tag is written once per version, so an
amd64-only runner would leave that version unusable on an arm64 host (a leased-back Mac, Plugins
`Hosting/BackLease.md`) for good. It is outside the platform's promotion
(the set is complete without it). Nothing names it by a moving tag: the hosts get it BY DIGEST with
the rest of the set. Every pull request there builds the image against core's `main` and runs every
self-test (job `test`).

**`build-latest` follows the armed set.** On every tick, Deploy's job `handover` (after
`image`) hands `build-latest` the newest armed set (policy `build-hosts-follow-latest`, [Policy Not Prose](../PolicyNotProse)):
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

**The channel names only a SEALED set, read by the same instrument every consumer reads.**
MeshWeaver.Plugins builds on the `latest` channel's set through the resolver's FREEZE path — that one
set, sealed and its images present, or RED — so a channel that names a set the resolver does not call
sealed reds every Plugins run. Two halves keep them in agreement:

- *The seal is read off the attempt that ran.* `resolve-platform.py` (`seal_jobs`) reads a run's trio
  (promote, verify, the platform bake) from its latest attempt, unless that attempt created no job at
  all; then it reads the newest earlier attempt that did, and says so. Measured 2026-10-29 on main-cd
  #11961 and #11966: attempt 1 sealed and armed the set and ended `failure` on a satellite
  compatibility leg outside the trio; the transient-retry lane re-ran it; attempt 2 was cancelled in
  main-cd's one pending slot before it created a job; `/jobs?filter=latest` answered zero records and
  the resolver called the set "cancelled before any seal job ran". Every Plugins run on that channel set
  was RED for hours, although the set was sealed and its images were present. An attempt that DID
  materialise is read exactly as before, so a trio job a re-run re-ran and lost still unseals the set.
  The walk re-reads the run's attempt number AFTER the empty answer and starts AT it, inclusive: the
  listing and the jobs read are two requests, and a re-run created between them would otherwise be
  skipped together with any trio job it lost. `arm-promoted-set.py` (`read_run_jobs`) walks the same way.
- *The hand-over waits for the seal.* Arming does not wait for `verify-images` or the platform bake, so
  the newest ARMED set can be one the resolver does not yet call sealed. MeshWeaver.Deploy's `handover`
  asks core's resolver at `main` (`--no-registry --freeze <set>`) before it signs anything, and holds
  while the answer is not sealed. The receiver never moves a channel backwards, so `build-latest` stays
  on its previous sealed set until the new one seals.

**`build-stable` follows the main-passed ceiling — daily.** Until core's `stable` promoter exists,
`stable` IS the newest sealed set MeshWeaver.Plugins' own `main` has passed on. Its own workflow,
MeshWeaver.Deploy's `.github/workflows/build-host-stable.yml` (daily `schedule`, 05:41 UTC; `stable` is refused on any
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

**A no-op until declared, twice.** MeshWeaver.Deploy's `.github/build-hosts.json` `enabled` gates both
jobs: while `false` they write one summary line and send nothing, using no credential; `stable` must
also be listed in its `channels`. The receiver guards the same fact on the record. Turning the hosts on
is two acts, in order: the record's `BuildHosts` block through its approval path, then `enabled: true`
there. **Both acts are done for `latest`:** the operations record declares `buildHosts.enabled` with
the `latest` channel, and Deploy's `.github/build-hosts.json` has `enabled: true` with `latest` in its
`channels`, so every tick hands the newest armed set to `build-latest`. `stable` is not listed yet.
Starting a host is not running builds on it: the operations instance's build
queue has its own switch (`Hosting__Builds__QueueEnabled`), and until it is on a running host claims
nothing.

Deploy's `image` and `handover` are outside core's `delivery-verdict` (the fleet's delivery does
not depend on a build host): Deploy's job `report` files or comments on the ONE open
`build-host-latest` issue in MeshWeaver.Deploy when either fails or is cancelled, and closes it only
after a run that actually HANDED build-latest a set. `build-host-stable.yml` reports on its own
`build-host` issue there, the same way.

## 5. Every repository on the hosts (H7) — dormant until the instance says so

**Who runs a run's heavy legs is decided ONCE, where the run is admitted** —
`.github/scripts/build-executor.py`, called from MeshWeaver.Plugins' `admission` job and, for a
repository with no admission job of its own, from the `node-repo-build-executor.yml` lane. It answers
`executor=host` only when all three hold:

1. the repository **declares** its stages (`.github/build-stages.json`, at least one) and **claims them
   CI-equivalent** (`"ciEquivalent": true` — a reviewed statement that they check everything the Actions
   legs they replace check; without it the stages can be PROBED on a host, never routed — see *Before a
   repository claims equivalence* below) **with nothing left in `owes`** — a claim beside an open debt is
   void here too, not only in the declaration's own gate (*Moving a repository's tests to the build
   server*, below);
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
| `node-repo-module-pack.yml` | `executor`, `host-verdict` | `select` still runs (it is the independent selection a host run is checked against), the build ledger is not consulted; `prepare` (*Warm the shared build environment (once)*), `build-workspace`, the `pack` legs and the `tests` legs do not run; `verify` (the required `… / All selected bundles built`) is green only for `host-verdict == success`, and refuses a select that failed or a heavy job that ran anyway; outputs `executed-on: host` and `bundles-built: false` (nothing composable was uploaded into the run). A PUBLISHING host run must be `publish-mode: staged` — `select` refuses `direct`, because a host holds no publication credential |

A follow job that did not run, was cancelled or failed is RED in the lane — a skipped need is never
a pass. `host-forced` is the `actions` arm (see "Host-forced routing" below); any `executor` other
than `actions` / `host` / `host-forced` is RED. With the defaults (`actions`, no
verdict) every lane behaves exactly as before, so a caller that passes nothing is unchanged.

### Host-forced routing — shadow until parity (policy `build-server-routes-all-ci`)

The rule above routes a repository to a host only once its stages CLAIM equivalence. The owner
decided to route every code repository's pull requests to the build server BEFORE that. Until a
repository claims parity, the host runs its pull requests in **shadow**: every run goes to the host
and its check runs stay visible, but the required verdict still comes from the GitHub Actions legs, so
fixes can merge while the host closes its gaps one by one. That is a third executor answer,
`host-forced`, and every lane treats it as `actions` for its own work:

- **Where the decision lives.** `.github/build-routing.json` on core's DEFAULT branch, `hostForced`: a
  list of repository names. `build-executor.py` reads it from `main` over the public raw URL, never
  from the pull request under test, so no pull request routes itself on or off the host, and the
  change that adds a repository is not itself routed before it merges. The operations instance must
  ALSO answer `host` for the repository (`Hosting:Builds:HostRepos`); the list alone routes nothing.
- **What it answers.** For a listed repository's pull request, when the instance says host and the
  stages do not claim equivalence, `executor=host-forced`. When stages are declared, the run is
  queued on the host (one signed `build-request`, exactly as for `host`), so the host runs what it can
  and publishes its own `build` / `build/<stage>` check runs. Those check runs are NOT required
  contexts, so they never conclude `failure`: they conclude **`neutral`**, and the title carries the
  would-be verdict (`shadow — would be RED: compile-check failed`, `shadow — would be GREEN: …`), with
  the summary unchanged. A red X on every pull request for a check that gates nothing read as a broken
  fleet; a neutral one with its verdict in the title says exactly what the host found.
- **How the instance knows.** The `build-request` carries the admission's own answer,
  `"executor": "host"` or `"executor": "host-forced"`, so the operations instance projects the check
  runs from the SAME decision the lanes act on (this routing file plus the stages' `ciEquivalent`) and
  never re-derives it. A run every one of whose requests said `host-forced` is a shadow run; one
  `host` request (the repository claims parity) makes its checks conclude `success` / `failure` again.
- **The owed gap is named, never a red.** The executor job prints each `owes` item of
  `.github/build-stages.json` as a `::warning::` annotation (a repository with no declaration owes
  "no host stages"). In core, `Consolidate test results` repeats that as a warning pointing at the
  executor job. Nothing on this route exits 1 because of the debt.
- **The Actions legs carry the verdict.** Every lane that reads the executor
  (`node-repo-compile-check.yml`, `node-repo-gate.yml`, `node-repo-module-pack.yml`,
  `build-host-publication.yml`) runs its heavy jobs on `host-forced` exactly as on `actions`, and its
  context-owning job judges them (`executed-on: actions`). `build-follow.yml` is a no-op success with
  `followed=false` on `host-forced`: it does not re-stream the host stage, because that would hold a
  runner for up to 43 minutes to mirror a verdict nobody gates on, and the host's own
  `build/<stage>` check run already shows it. A caller's consumer therefore takes the verdict from
  the Actions legs, as it does for `actions`. In core, `build` (and every leg behind it) runs on
  `host-forced`, `host-follow` and `host-results` run only on plain `host`, and `Consolidate test
  results` judges the Actions suites.
- **Plain `host` is unchanged.** A repository whose stages claim `ciEquivalent` with nothing owed
  routes as `host`: its heavy legs do not run on Actions, its follow jobs follow the host, and the
  host's verdict is the required one. `build-executor.py --self-test` holds both routes, with the
  plain-`host` case as the negative control.
- **What it never touches.** Trunk runs (main's CD keeps sealing the platform sets that carry the
  host's own fixes), `MeshWeaver.Deploy` and `Memex` (they carry the host fixes and the
  infrastructure, and must stay mergeable). An unreadable routing file or an unanswering queue reads
  as NOT forced: the legs run on Actions, a real verdict.
- **Leaving it.** A repository leaves `hostForced` when its stages claim equivalence (it then routes
  as plain `host`, and the host becomes its verdict), or when the owner reverses the decision.

### Per-stage equivalence — one proven stage at a time (policy `per-stage-ci-equivalence`)

`ciEquivalent` is all-or-nothing per repository. Per-stage equivalence moves ONE stage of a host-forced
repository off Actions once that stage's own parity is proven, while the other stages stay in shadow:

- **Where it is declared.** `equivalentStages` in core main's `.github/build-routing.json`, the same
  trunk-only file as `hostForced`, so no pull request routes its own stage. One object per repository:
  `{"<stage>": {"comparable": N, "disagreements": 0, "report": "<the measurement>"}}`. The evidence comes
  from MeshWeaver.Plugins `scripts/build-parity-report.py`. It is declared, never derived: the executor
  reads the declaration and measures nothing.
- **The bar.** At least 20 comparable pull requests, where both the host and Actions reached a verdict,
  and zero disagreements. `build-executor.py --check-routing` refuses an entry below the bar in core CI,
  and the executor refuses it again at run time: the stage stays in shadow and the run says why.
- **Fail-fast order.** A declared stage is honoured only when every stage declared BEFORE it in the
  repository's `.github/build-stages.json` is honoured too. The host runs stages fail-fast, so a later
  stage behind a shadow one could read `not run` and turn its required context red for a reason it
  never checked.
- **Only on a run the host holds.** A stage moves only when this run's `build-request` was accepted and
  its signature verified. Otherwise every stage stays on Actions for that run, so a context never waits
  on a run the host never received.
- **What it answers.** The run-level `executor` stays `host-forced`, so every lane that reads it is
  unchanged. A new output, `stage-executors`, is JSON with one entry per declared stage: `host` (its
  follow job carries the verdict and its Actions legs skip) or `host-forced` (shadow). A consumer reads
  `fromJSON(stage-executors)['<stage>'] || executor`, so an empty `{}` is today's behaviour for every
  stage. The host's own `build/<stage>` check runs stay `neutral` on a shadow run. They are never
  required: the follow job is what carries the verdict.
- **Required contexts stay guarded.** Per stage, the context-owning job keeps its name and runs
  `if: !cancelled()`. With `host` it asserts a FOLLOWED success, and the stage's Actions legs must have
  skipped. With anything else it judges the Actions legs. It is never skipped: this is the shim route of
  [Renaming a Required Check](../RenamingARequiredCheck). The caller wires this, for example
  MeshWeaver.Plugins `ci.yml` and its `scripts/check-build-queue-admission.py`.
- **Rollback.** Remove the stage's entry from `equivalentStages`. The next run's admission answers
  `host-forced` for it, and its Actions legs carry the verdict again under the same context name.

`build-executor.py --self-test` holds the bar, the order, the queued-and-verified requirement, trunk
and route-only, with negative controls for each refusal. Every case runs on fakes, with no network.

### The host publication graph (module-pack on a host)

A module repository's bundles reach the registry from a host run through three calls, and
`module-publication.py check-callers` (run by `node-repo-validate.yml` on every satellite pull
request) refuses any other shape:

```yaml
modules:                 # the pack call: its `select` runs here, its legs on the host
  needs: [admission, modules-follow, …]      # every job its `executor`/`host-verdict` read
  uses: Systemorph/MeshWeaver/.github/workflows/node-repo-module-pack.yml@main
  with:
    executor: ${{ needs.admission.outputs.executor || 'actions' }}
    host-verdict: ${{ needs.modules-follow.result }}
    publish-mode: staged
    publish: <the publishing condition>
host-publication:        # the host graph's source for the publisher
  needs: [modules, admission]                # the pack call AND every job its `executor` reads
  if: <the pack call's `publish:` condition, verbatim>
  uses: Systemorph/MeshWeaver/.github/workflows/build-host-publication.yml@main
  permissions: { contents: read, id-token: write }
  with:
    executor: ${{ needs.admission.outputs.executor || 'actions' }}   # the pack call's, verbatim
    stage: modules
    lane: ${{ needs.modules.outputs.lane }}
    selected: ${{ needs.modules.outputs.selected }}
    declared: ${{ needs.modules.outputs.declared }}
    plan: ${{ needs.modules.outputs.publication-plan }}
    api-url: …
    artifact-store: …
    artifact-store-id: …
publish:                 # unchanged lane, reading THROUGH the host-publication call
  needs: [host-publication, …]
  uses: Systemorph/MeshWeaver/.github/workflows/node-repo-module-publish.yml@main
  with:
    lane: ${{ needs.host-publication.outputs.lane }}
    selected: ${{ needs.host-publication.outputs.selected }}
    modules: ${{ needs.host-publication.outputs.declared }}
    plan: ${{ needs.host-publication.outputs.plan }}
```

- **`build-host-publication.yml` emits exactly the outputs the publisher reads from a pack call**
  (`lane`, `selected`, `declared`). With `executor: host` it fetches the receipts, runs the
  publisher's OWN check over the records (`module-publication.py publish --check-only`: one record per
  selected module, stamped with the lane, naming its bytes by sha256 and the framework identity read
  back off them), stages them as `module-publication-<lane>-host`, and only then emits its outputs.
  With `executor: actions` it is a no-op success that hands the pack call's outputs through — never a
  skip, because a skipped ancestor skips the publisher. A zero selection is the one "nothing owed".
  Receipts are keyed by repository, sha and stage, not lane, so before the check it sets aside, by
  name, every record stamped with ANOTHER lane and its bundle (`module-publication.py partition`): two
  pack calls sharing a host stage each publish their own. A record with no lane stays, and the check
  refuses it.
- **The selection is the pack call's `select`, never the host's records.** A selection read off what
  the host staged would accept whatever the host staged.
- **What a run OWES is the pack call's `select` too — the publication plan** (MeshWeaver.Plugins#4478,
  slice 3). A build host packs the WHOLE catalog on a trunk run and offers every bundle (`owed: true`):
  it holds none of the inputs that decide a publication — the publication baseline and carry that
  narrow a trunk selection (a walk of GitHub run history the sandboxed host has no credential for),
  the caller's `publish-withhold`, the reuse of an earlier publication's bytes, the ledger. They exist
  only on Actions, and the host's build request is signed at admission, before any of them is
  computed. So `select` writes them down ONCE as data (`module-publication.py plan`, the workflow
  output `publication-plan`: per selected module, owed or not and why — the pack leg's own
  `need_publish` predicate), and `build-host-publication.yml` applies it to the host's records
  (`module-publication.py apply-plan`): a record the selection does not name is set aside by name
  with its bundle, and a module the plan does not owe becomes `owed: false` with the plan's reason and
  loses its bytes — exactly the records the pack legs stage on Actions. `apply-plan` can only take a
  hand-over AWAY: a plan owing a module the host did not offer, or owing a REUSED build (another
  run's recorded bytes, which no host ever had), is red. Then the publisher's `--plan` refuses any
  staged record whose `owed` disagrees with the plan — on Actions as well, so the restated predicate
  is proven against the leg on every Actions trunk run before a host relies on it. Mirroring the
  selection on the host instead would compute one decision twice, from inputs the host cannot read;
  sending them in the build request would hold every host dispatch behind the selection.
  A host fetch under the host-graph contract with no plan is RED.
- **`check-callers` refuses**: a host-capable staged pack call with no (or two) host-publication
  calls; a publisher reading such a call's lane directly; a host-publication call whose lane,
  selection or catalog does not come from one pack call, whose `executor` differs from that call's,
  whose `if:` differs from the pack call's `publish:` or carries a status function, or that does not
  need the pack call; a host-publication call for a pack call that always runs on Actions or is not
  `staged`; a call whose `executor` expression reads a job (`needs.<job>`) its own `needs:` does not
  name — that context is EMPTY there, so the expression falls back to `actions` while a sibling call
  spelling it identically resolves `host`; a literal `executor: host` that publishes in-leg; and a
  `plan` that is not the SAME call's (`needs.<pack>.outputs.publication-plan` on the host call,
  `needs.<host call>.outputs.plan` on the publisher), or a publisher that drops the plan its host call
  applies. A host call with no `plan` is named, not refused — the lane refuses its host fetch at run
  time. An `executor` expression counts as "may run on a host".
- **The host's modules stage owes the lane's format** (§2, step 7): a bundle and a
  `<module>.publication.json` per packed module (`owed: true` — the host offers, the plan decides),
  stamped with the pack call's lane key —
  `module-publication.py lane-key --lane-id <lane-id> --modules <the call's modules input, verbatim>`,
  the same key the lane's `This call's lane key` step derives (`test-module-pack-host.py` runs that
  step's bash against it).

**Adopting it, in order:** (1) core carries the lanes and the validator (H7 core half); (2) the
repository declares a `modules` stage in `.github/build-stages.json` whose command produces the
artifacts above, and adds `modules-follow` (`build-follow.yml`, stage `modules`) beside its other
follow jobs, with its `pending:` row in `.github/lane-caller-grants.yml` landed first; (3) the pack
call passes `executor` and `host-verdict`; (4) a repository that publishes from that call moves to
`publish-mode: staged` with the two calls above. Nothing changes until the admission answers `host`
for the repository (a `ciEquivalent` declaration and the instance's switch).

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
declaration claims it yet: MeshWeaver.Plugins owes the items listed in its `.github/build-stages.json`
(`Hosting/StandingBuildHosts` H7). **This repository's own CI is NOT wired** — the first item core
owes (below).

### Moving a repository's tests to the build server — `owes` and the one rule

Every repository's move follows one mechanism, the one MeshWeaver.Plugins introduced as rule 7 of its
admission check: the declaration says what its stages still check LESS than the Actions legs they
would replace, and the claim is refused until that list is empty.

```json
{ "owes": ["<one gap, naming the Actions leg it is measured against>", "…"],
  "stages": [ { "name": "…", "kind": "build|test", "command": "…", "timeoutMinutes": 40, "needs": ["nuget"] } ] }
```

- **`ciEquivalent: true` ⟺ `owes` is empty.** The claim with an open debt is RED; a debt-free
  declaration that does not claim is RED (`owes` is how "not yet" says why); a malformed `owes` or
  `ciEquivalent` is RED. `build-executor.py` applies the same test where the run is ROUTED, so a
  declaration that reached a branch without its gate still cannot send a run to a host.
- **Parity a stage must keep.** Each stage's command must keep saying the checks its Actions leg makes
  — a substring per check — so a stage cannot quietly shed one: the node-repo profile derives the
  stages a satellite owes from the lanes its `ci.yml` calls (`node-repo-compile-check.yml` ⇒
  `compile-check`, `node-repo-gate.yml` ⇒ `gate`) and checks each against the lane (core's
  `compile-check.py`, `scripts/compile-check.allow`, the host's reference set; the tester and dotnet
  through the host's own paths, never a hard-coded `/host/`, the lane's allow file, the warning
  baseline when ci.yml passes one, the gate seeded with its own bake, the Tests-area ratchet over the
  log, the log handed back as an artifact).
- **A gate stage is the WHOLE gate, and every check reaches its exit code (#7456).** Two holes a
  substring cannot see. (1) A tester narrowed with `--shard k/n` gates one slice, and the host route
  runs no `gate-shard-merge.py` of its own, so nothing would notice the missing slices: a narrowed
  gate is red unless the stage runs EVERY literal slice `1..n` and folds them with
  `gate-shard-merge.py --shards n` (a slice from a variable or a loop is red — the guard cannot read
  it). (2) The runner executes a stage with `bash -c`, which adds no failure propagation, so
  `compile; gate | tee; ratchet` exits with the ratchet's code — measured by EXECUTING it against stub
  tools: a compile that exits 1 followed by a green gate and ratchet is a stage exit of 0. A gate stage
  is therefore red unless it is `set -e` (with `-o pipefail` when a check is piped), or every check is
  handled by hand the way every satellite writes it today under `set -uo pipefail`: the compile
  `|| exit`, the gate's `${PIPESTATUS[0]}` (or `$?`) and the ratchet's `$?` captured right after them
  into variables the stage `exit`s with. The self-test executes both shapes and the unsafe one.
  The guard reads the stage in order and checks every invocation of every check: each gate slice,
  `gate-shard-merge.py`, the compile and the ratchet.
  - `set -e` counts only where it is in force. A `set -e` written after the checks, or undone by
    `set +e`, does not count.
  - `gate | tee || exit` counts only under pipefail. Without it, the `|| exit` tests `tee`.
  - A captured status must reach an `exit` before anything assigns its variable again. Two slices
    captured into one name lose the first slice's failure.
  - A `--shard` token the guard cannot read as a slice, quoted ones included, is red.
- **The rule lives once**, in `.github/scripts/check-build-stages.py` (`--profile core|node-repo`,
  `--self-test` with a negative control per breach). Core runs it through `core-build-stages.py
  --check` in `dotnet-test.yml`'s `workflow-shell` job; every satellite runs it through the shared
  `node-repo-validate.yml` lane. A satellite with no declaration is **not migrated** — said in the
  lane, not red, because `build-executor.py` answers `actions` for it and every lane still runs on
  GitHub; nothing reads green that did not run.
- **The host comes prepared.** No stage restores, stages or prepares anything per run: a stage reads
  the host's installed platform (`$MeshWeaverRefs`, the floor — the portal's `/app` as shipped, a
  gate's `--app`; and `$MW_BUILD_COMPILE_REFS`, the same app plus its shared frameworks in CI's
  `refs/` shape, prepared once per roll by the host's init container: what a compile-check's
  `--refs` must RECEIVE (directly or through a variable assigned from it — the node-repo profile
  checks the argument, not a mention), because the app alone carries no `System.Private.CoreLib` and
  `compile-check.py` refuses it). That set is the PLATFORM half only: `compile-check.py` also
  requires the module assemblies the image no longer ships (`MeshWeaver.AI`,
  `MeshWeaver.Markdown.Collaboration`, `MeshWeaver.Maps`), which the Actions lane composes from the
  sealed publication — on a host that is the separately declared "composed modules and the upstream
  seed" debt, and a satellite's compile-check stays red until it is paid. Its tester and dotnet
  (`$MW_BUILD_GATE_HOST`, `$MW_BUILD_PORTAL_DOTNET`) and, with `needs: ["nuget"]`, the host's prepared
  package cache (`NUGET_FALLBACK_PACKAGES`). Core's build restores through its own `nuget.config` with
  that cache as the FALLBACK folder: every package the cache holds is read from it, nothing is fetched.
  🚨 Never `--source "$NUGET_FALLBACK_PACKAGES"`: under the repository's `packageSourceMapping` a
  command-line source maps no package, so EVERY package is NU1100 even from a complete cache (measured:
  435 × NU1100 restoring `MeshWeaver.Hosting.Test`'s closure from a warm cache, against 0 errors and 0
  downloads through the fallback folder; `check-build-stages.py` reds a `--source`). A package the cache
  does not hold yet — a new or bumped `PackageVersion` — is restored for that run and NAMED in a
  `::warning::`; the host reports it (`MWPREP001`) and promotes it into the cache after a succeeded
  TRUNK build (MeshWeaver.Deploy#42), so the cache follows `main` as it moves. An offline-only restore
  could never warm it: the first trunk build that needs a new package would be red, and nothing would
  ever be promoted.
- **Core declares the host's warm restore.** `.github/build-stages.json` carries
  `"restore": {"command": "dotnet restore MeshWeaver.slnx -p:Configuration=Release -p:CIRun=true && dotnet restore .github/nuget-warm/ResolverFixturePackages.csproj", "timeoutMinutes": 20}`
  — the build's own closure, then the packages `NuGetAssemblyResolver` fetches at TEST time (`#r "nuget:…"` in
  executable doc cells and samples, the resolver's live-feed tests: Humanizer, Humanizer.Core, Markdig,
  MathNet.Numerics, Microsoft.Data.SqlClient at the exact versions they name, with their closures). No project in
  the solution references those, so before they were declared every run downloaded them again; the resolver reads
  the warm cache as a NuGet fallback folder ([NuGet Runtime Assets](../NuGetRuntimeAssets#offline-first-resolution)).
  The host reads it from the default branch's tip, never from a run's own tree, and runs it once per restore key to
  fill the shared NuGet cache (MeshWeaver.Deploy#68); `check-build-stages.py` validates its shape and, for core, reds
  a restore that drifts from the build's solution and properties or drops the resolver fixtures. `core-build-stages.py
  --check` holds the fixture list to the code: a `#r "nuget:…"` in a sample's source or an executable doc fence
  (`--render` / `--execute`) whose id and exact version the fixture project does not list — or that names no exact
  version — is red, because the host would download it on every run without naming it. Measured on build-latest
  (2026-10-30): host-forced shadow runs of core warmed core's restore key from the default branch's tip (`warm NuGet
  cache: filled … 3 package(s) promoted`, then `holds the default branch's restore`) and their `build` stage reported
  no `MWPREP001` — nothing restored per run — so `prepared packages` is no longer owed.
- **Remove an `owes` entry only in the change that pays it.** The claim then follows mechanically.

**Core's stages serve a DEPLOYMENT today, and are declared for its pull requests.** Core declares
`.github/build-stages.json` for both routes: `build`, then `test-shard-0` … `test-shard-<N-1>`, one
per shard of `dotnet-test.yml`'s suite (N = `CI_SHARD_TOTAL`), and `doc-gate`. A deployment (the
deploy-test-switch shape, the design page in core PR #7182, P4) submits one pinned run per shard and
doc-gate stage and reads the set's image; a pull-request or trunk run on a host would run `build`
first — the whole solution, Release, `-warnaserror`, from the prepared package cache, every
test project a solution member, then `core-tests.py pack` of THIS run's tree into
`$MW_BUILD_OUTPUTS/core-tests`. Every shard and the doc gate name both packs and
`--require-sha "$MW_BUILD_SHA"`: `core-tests.py` takes the first pack whose manifest names the run's
own commit and is RED when none does, so a run never tests the image of whatever set the host has
installed in place of its own tree. Each runs from the set's **`mw-core-tests`** image, the set's
fifth digest (`BuildHostChannel.CoreTestsDigest`): main-cd's `core-tests-image-arch` builds core's
tests with CI's flags once PER ARCHITECTURE, on a runner of that architecture — a test bin carries
the build machine's native `mw-plugin-test` apphost and its SDK's ReadyToRun Razor generator, so an
x64 tree cannot serve the arm64 image — `deploy/core-tests/core-tests.py pack` stores every shard's
bins content-addressed with a manifest, `core-tests-image` joins the two platform images into the
set's index, and `promote` tags it with the set. A stage runs
`python3 deploy/core-tests/core-tests.py run --shard <i> --total <N> --root … --require-sha …`, which copies that shard's bins out
of the read-only image tree into the run's checkout and runs them as CI's shard loop does — each
unit in its own process group, ended whole at the wall clock, and judged on a trx written by THAT
invocation; `doc-gate` runs the doc and samples gates with the image's plugin tester against the
checkout, followed by `check-tests-area-verdicts.py` on each tree exactly as CI's doc-gate job.
- **The list is GENERATED**: `.github/scripts/core-build-stages.py --write` derives it from
  `CI_SHARD_TOTAL`, and `--check` (dotnet-test.yml's lint job, and the image leg before it packs)
  reds any drift. The runner also refuses a `--total` the image was not packed for.
- **No `ciEquivalent` while core owes.** `--check` holds the declaration to the one rule above; the
  generator's `OWES` list is the debt — `Consolidate test results` (the host-results fetch is built on both
  ends but has never run, since it runs only on the plain `host` route), the prepared package closure, the browser and font set, the
  prepared-mesh rule, and a green probe that agrees with Actions. (The legs that read the build's artifacts —
  platform-compat and licence-gate — are paid: the host build hands their inputs back, below.)
- **The shards FAN OUT on one host (`"parallel": true`).** The runner (MeshWeaver.Deploy `build-run.py`)
  runs a run's independent stages concurrently when the repository declares `parallel` and the host
  declares stage slots (`MW_BUILD_STAGE_SLOTS`, chart `buildHosts.stageSlots`, default 8, refused at start
  when the container's cgroup cannot give each slot 1 CPU and 2 GiB). The rules: a stage starts once every
  stage it runs `after` finished green; a `build` stage runs ALONE (it writes the shared
  `$MW_BUILD_OUTPUTS`, and the host promotes outputs and NuGet packages after it); each concurrent stage has
  its own sandbox, temp, PostgreSQL socket, event route and `$MW_BUILD_ARTIFACTS` (`artifacts/<stage>/`, so
  a red sibling's files never reach a green one's receipts); the declared budget is the schedule's critical
  path, and a 409 on any stage kills every sibling. Core's `build` runs first, then the 8 shards and
  `doc-gate` at once — the Actions `test` matrix on one host. Measured before (#7308 on build-latest,
  serial): build 2 min, then 8 shards of 4-7 min one after another, about 47 min end to end; Actions ran
  the same commit's legs in about 14 min. The one-host design was chosen over splitting a run across
  replicas because the stages already share one checkout and one `$MW_BUILD_OUTPUTS` there: no per-run
  cache namespace, no second trust boundary, no claim protocol change. A host with one slot, or a
  repository that does not declare `parallel`, still runs serially.
- **A shard checks what the `test` job checks, and hands its evidence back.** `core-tests.py run`
  writes CI's `[CI] <name> exit=N` markers to `test/test-results.log` (TIMEOUT, SIGNAL, TESTFAIL and
  MASKED classified as the shard loop does), writes every crash INTO the trx with
  `record-host-crash.py` (#2495), collects each bin's `test-logs/` and the hosts' temp-dir traces into
  `collected-logs/`, and on a host leaves that tree — the `test` job's artifact shape — under
  `$MW_BUILD_ARTIFACTS/testResults-shard<i>/`, which the host hands over as H9 receipts. Only THEN does
  it run `check-untyped-content.sh` over `collected-logs/`, so the logs a red is decided on survive
  the red. The scripts are the checkout's own (`--repo .`, a parity marker).
- **The test hosts' temp is OUTSIDE the checkout.** Each invocation gives its hosts a fresh
  `$TMPDIR` under the temp directory the stage inherited (the sandbox's scratch), removed once its
  traces are collected — never under `--work`, which defaults INTO the checkout. Tests build
  throw-away projects under `Path.GetTempPath()`, and the csproj evaluator walks up from them to the
  nearest `Directory.Build.props` exactly as MSBuild does: with the temp inside the checkout that is
  core's own, and the tests evaluate core's build instead of their fixture (measured on build-latest:
  `MeshWeaver.PluginTester.Test` 82 of 474 red on `<checkout>/src/Directory.Build.props: <Target …> is
  not executed by this builder`, green on Actions, whose temp is outside the checkout). The runner
  refuses an inherited temp that itself lies inside the checkout.
- **No suite needs a Docker daemon, and that is checked.** A host's sandbox has none (its database is
  the prepared server). Core's Postgres suites left core, and nothing in the tree references
  `Testcontainers*` or `Docker.DotNet` or builds a container in `test/` (executable code only — a
  comment or a string fixture that names one starts nothing). `core-build-stages.py --check`
  reds the day one does: such a suite either runs against the host's prepared service or re-opens
  the `containers:` debt in the same change.
- **Core's own route** (`dotnet-test.yml`). Job `admission` calls `node-repo-build-executor.yml` (the
  base tip = precheck's main, `BUILD_WEBHOOK_SECRET` signs the request) exactly when `build` would
  run. With `host`:
  - `build` does not run here, and with it `test` and `doc-gate`;
  - `host-follow` follows EVERY declared stage (`build`, each `test-shard-<i>`, `doc-gate`) with
    `build-follow.yml`;
  - `host-results` fetches each shard's handed-back results tree over HTTPS by receipt
    (`host-shard-results.py`, below) and uploads it under the `test` matrix's own artifact name
    `testResults-shard<i>-attempt<a>`, so `collect-results` consolidates a host run unchanged (download,
    attempt resolution, the coverage count, the trx and exit-marker gates);
  - `host-build-outputs` brings back what the two DEPENDENT LEGS read from the Actions `build` job
    (`host-build-outputs.py`, below), and `platform-compat` and `licence-gate` then run their unchanged
    steps on either route — they need `build` OR, on `host`, `host-build-outputs`, and merge the head
    onto `needs.build.outputs.main-sha || needs.host-build-outputs.outputs.main-sha`;
  - `collect-results` reds a failed admission, a host stage that did not succeed, a shard whose
    results were not brought back, and a build hand-back that was not.

  **The build's hand-back (the dependent legs).** On Actions the `build` job uploads `build-output-0`
  (shard 0's tarball; `platform-compat` reads only its plugin tester, `tools/MeshWeaver.PluginTester/bin`)
  and `licence-evidence` (`check-licenses.py --stage-evidence` over the build's one restore), and both
  consumers judge the head merged onto the main the build compiled. The host `build` stage ends with the
  same staging and `host-build-outputs.py stage`, which leaves exactly three files under
  `$MW_BUILD_ARTIFACTS/build-outputs/` for the host to hand over as receipts: `build-output-0.tar` (the
  tester bin under its repository path, sorted, fixed owner and mtime), `licence-evidence.tar`, and
  `tested.json` (`sha`, the compiled `tree`, and `mergeBase` — the second parent of the host's
  `merge --no-ff`, or null when it merged nothing). The evidence is staged from EVERY folder the restore
  read — the writable `NUGET_PACKAGES`, the prepared cache bound as `NUGET_FALLBACK_PACKAGES`, and each
  `project.assets.json`'s `packageFolders` — because on a host nearly every package comes from the
  fallback folder. `host-build-outputs` (GitHub-hosted, the run's OIDC token) runs
  `host-build-outputs.py fetch`: the `build` stage's SUCCEEDED receipts under `build-outputs/` only, each
  object by content hash through `host-shard-results.py`'s no-redirect getter, verified against its
  receipt, every tar member held to its shape (no link, nothing escaping, nothing outside the tester bin
  or `restored-packages.json` + `packages/**`); then it runs the `fresh-merge` action onto the `main-sha`
  it names (the merge base, or the head itself when nothing was merged — a head merged onto itself keeps
  its own tree) and is RED unless that tree IS the host's; then it packs the tester with
  `zstd --long=27` and uploads `build-output-0` and `licence-evidence` under the Actions names.
  `core-build-stages.py --check` holds the wiring (`HANDBACK_WIRING`) and `check-build-stages.py`
  (profile core) the build stage's two parity markers.

  `core-build-stages.py --check` holds both matrices to the declaration. Any other admission answer
  runs every leg on Actions, as before — a `host-forced` answer included (see "Host-forced routing"
  above): the run is also queued on the host in shadow, and `Consolidate test results` names the
  owed parity as a warning.

  **Core runs no job on the self-hosted pool.** Core is a public repository: it never gets the
  private `MW_RUNNER` / `MW_ARTIFACT_STORE` organisation variables, and no job of its reads the
  runners' shared store. `host-results` therefore runs on `ubuntu-latest` and takes everything from the
  build API over HTTPS with the run's own GitHub OIDC token (`permissions: id-token: write`) —
  `host-shard-results.py`:
  1. `GET {api}/{repo}/{sha}/artifacts?channel=…` — the receipts, read and shape-checked by
     `build-artifacts-fetch.py`'s reader, kept for the shard's own stage `test-shard-<i>` and its unit
     directory `testResults-shard<i>/` — a RED shard's receipts (`stageState: failed`) included, because
     its results are what consolidation must read;
  2. `GET {api}/{repo}/{sha}/artifacts/{sha256}?channel=…` — each receipt's bytes by content hash,
     written under `--out` with its relative path PRESERVED (two `.trx` of one name in two projects
     do not collide), at most the receipt's size plus one byte read, redirects refused so the bearer
     never follows one, and the short-lived OIDC bearer re-minted once it is older than
     `build-follow.py`'s `TOKEN_REFRESH_SECONDS`, so a long fetch never sends an expired one.

  It is RED, naming the file, on: no receipt for the tree, a unit escaping it or FOREIGN to the `test`
  job's shape (`test/**/*.trx`, `test/test-results.log`, `collected-logs/**`), one path with two
  contents, a missing `test/test-results.log` or one with no `[CI] <project> exit=<n>` marker, a
  refused fetch, a body whose size or sha256 differs from its receipt (TRUNCATED or padded), and a
  written tree that is not exactly the receipts' set. The object route in step 2 is served by
  MeshWeaver.Plugins `BuildHostEndpoints` (since MeshWeaver.Plugins#4526) and is live on the control
  instance: measured 2026-10-30, a tokenless `GET …/artifacts/<sha256>?channel=latest` answers 401
  naming the OIDC token, while a path one segment longer answers 404. 🚨 **Still owed:** a fetch
  that has actually run. `host-results` runs only on the plain `host` route, never in host-forced
  shadow, so the `Consolidate test results` owes entry is paid only when collect-results
  consolidates results fetched this way on a host run.
- **Owed outside core:** the build host must stage the `mw-core-tests` tree read-only at
  `/core-tests` (by digest, beside `/portal/app` and `/host` — MeshWeaver.Deploy's chart and runner),
  and P4's `build-host-test` job submits one pinned run per stage. Not established: which of core's
  suites run inside a host's sandbox — the shards' browser tests need the headless browser and font
  set the build-host image carries from MeshWeaver.Deploy#50 on.
- **Two host-only reds, neither from the fan-out.** Measured on build-latest: shard 7 and shard 0 were
  red on #7308 (serial) and on #7506 and #7515 (fanned out) while the same commits were green on Actions.
  Both reproduce in a host-shaped container (the .NET 10 SDK image, the runner's exact `bwrap` flags,
  the shard's bins at `<checkout>/.core-tests/shard-<i>/`), so the parallel fan-out is not the cause.
  **Shard 7:** `PluginPublicationProvenanceTest` runs the publication workflow's own `run:` steps, which
  call `jq`; Actions' runner ships it and the runner image did not, so 11 tests failed on
  `step.sh: jq: command not found` (11 of 891 without jq, 0 with it). The image now installs jq and
  prints its version at build. **Shard 0:** `AStampThatFindsAnotherReplicasClaim_IsNotHeld` waited for a
  holder number, which also matches a claim whose STAMP has not reached that silo's mirror yet; a stamp
  that finds another claim writes nothing and reports the mirror, so a lagging mirror read as "A's stamp
  landed over B's claim". It now waits for the exact settled claim (5 of 6 runs red before, 0 of 10 after,
  in the container). A host's slower mirror propagation exposed the wait; it did not cause it.

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

- **The receiver of `build-host-update-available`** (the record write) EXISTS: MeshWeaver.Plugins
  #4166 shipped it (`Hosting/Deployment/Source/BuildHostUpdates.cs`), and it changes a record only
  when that record's `BuildHosts.Enabled` is already true. Both declared acts of §4 are done for `latest`
  (2026-10-26), so the event is sent on every CD run; enabling the build QUEUE on the operations instance
  (`Hosting__Builds__QueueEnabled`) is a separate, later act. The
  **`POST /token` route** (minting `GetScopedInstallationToken(owner, [repo], {"contents":"read"})`
  for the ONE repository of the run, only for the host that holds its claim) is the operations
  instance's, in MeshWeaver.Plugins.
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
- **The host's `modules` stage** — the command that packs a repository's modules on the host and
  leaves the bundles and lane-stamped `<module>.publication.json` records of *The host publication
  graph* (§5). The lanes and the validator are in core; no repository declares the stage yet, so no
  run's module bundles are built on a host. Its selection must be the one the pack call's `select`
  makes (`node-repo-scope.py` over the same merged tree): the publisher refuses a record the
  selection does not name. The publication-wide assembly census (`module-set-census.py`) that
  `verify` takes on an Actions run is skipped on a host run and is owed by that stage.
- **Keying compile outputs** by (source hash, reference-set hash) is the stages' work: the runner
  promotes what a trunk stage writes under `$MW_BUILD_OUTPUTS/<key>/`, and reads nothing itself.
