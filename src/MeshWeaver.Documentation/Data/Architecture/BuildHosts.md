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
   On the RATCHET side, a node repository whose `gate` stage ran on a host reads that stage's
   `gate-log/gate.log` through the composite action `.github/actions/host-gate-log` (`api-url` = the
   build-executor lane's output, `store` = `vars.MW_ARTIFACT_STORE`, the job holding
   `id-token: write`). It fetches the `gate` stage's receipts under the `gate-log/` unit with the same
   `build-artifacts-fetch.py` (`--unit-dir gate-log`: the host hands over every file under
   `$MW_BUILD_ARTIFACTS` after each successful stage, so the gate stage's receipts repeat an earlier
   modules stage's bundles), and `host-gate-log.py` then refuses anything but ONE non-empty `gate.log`
   whose LAST `=== mw-plugin-test summary ===` block ends in the tester's terminal verdict
   (`ALL GREEN.` / `GREEN — …` / `GATE FAILED …`) — nothing handed over (a red host gate hands nothing
   over), an empty, truncated or foreign log, or a second file in the unit are each RED. The repository then runs the
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
(the .NET 10 SDK, bash, python3, git, bubblewrap, Deploy's `build-run.py`, and the platform's
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
a pass. Any `executor` other than `actions` / `host` is RED. With the defaults (`actions`, no
verdict) every lane behaves exactly as before, so a caller that passes nothing is unchanged.

### Host-forced routing — red until the debt is paid (policy `build-server-routes-all-ci`)

The rule above routes a repository to a host only once its stages CLAIM equivalence. The owner
decided to route every code repository's pull requests to the build server BEFORE that, and to accept
red until each gap is closed. That is a third executor answer, `host-forced`, and it can only ever be
red:

- **Where the decision lives.** `.github/build-routing.json` on core's DEFAULT branch, `hostForced`: a
  list of repository names. `build-executor.py` reads it from `main` over the public raw URL, never
  from the pull request under test, so no pull request routes itself on or off the host, and the
  change that adds a repository is not itself routed before it merges. The operations instance must
  ALSO answer `host` for the repository (`Hosting:Builds:HostRepos`); the list alone routes nothing.
- **What it answers.** For a listed repository's pull request, when the instance says host and the
  stages do not claim equivalence, `executor=host-forced`. The executor job prints each `owes` item
  as an `::error` annotation (a repository with no declaration owes "no host stages"). When stages
  are declared, the run is still queued on the host, so the host runs what it can; those host check
  runs are not required contexts.
- **Red, never green, never skipped.** Every lane and follow job that reads the executor
  (`build-follow.yml`, `node-repo-compile-check.yml`, `node-repo-gate.yml`, `node-repo-module-pack.yml`,
  `build-host-publication.yml`) fails its FIRST step on `host-forced`, naming the policy, and the
  lanes' heavy jobs skip so the forced path puts no load on Actions. The context-owning jobs run
  `always()`/`!cancelled()`, so the context is published as a FAILURE, never skipped. In core,
  `precheck` decides with `build-executor.py --route-only` and `build` (with every leg that needs it)
  does not run on `host-forced` — precheck's answer OR the admission's: precheck's queue read can fail
  (`actions`) while the admission's succeeds moments later and queues the host run, and the legs must
  not then start on Actions as well. The required context, `Consolidate test results`, fails on either
  answer, asks again and exits 1, naming the debt.
- **What it never touches.** Trunk runs (main's CD keeps sealing the platform sets that carry the
  host's own fixes), `MeshWeaver.Deploy` and `Memex` (they carry the host fixes and the
  infrastructure, and must stay mergeable). An unreadable routing file or an unanswering queue reads
  as NOT forced: the legs run on Actions, a real verdict.
- **What still needs per-repository wiring.** A required context produced by a repository's OWN job
  (not a shared lane), and a repository that never calls `build-executor.py`, are not reached by
  this file. Each such repository adds the executor lane and a guard in that job; until then its
  context is an Actions verdict, which is real but is not routed.
- **Leaving it.** A repository leaves `hostForced` when its stages claim equivalence (it then routes
  as plain `host`), or when the owner reverses the decision.

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
- **`check-callers` refuses**: a host-capable staged pack call with no (or two) host-publication
  calls; a publisher reading such a call's lane directly; a host-publication call whose lane,
  selection or catalog does not come from one pack call, whose `executor` differs from that call's,
  whose `if:` differs from the pack call's `publish:` or carries a status function, or that does not
  need the pack call; a host-publication call for a pack call that always runs on Actions or is not
  `staged`; a call whose `executor` expression reads a job (`needs.<job>`) its own `needs:` does not
  name — that context is EMPTY there, so the expression falls back to `actions` while a sibling call
  spelling it identically resolves `host`; and a literal `executor: host` that publishes in-leg. An `executor` expression counts as
  "may run on a host".
- **The host's modules stage owes the lane's format** (§2, step 7): a bundle and a
  `<module>.publication.json` per selected module, stamped with the pack call's lane key —
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
  generator's `OWES` list is the debt — `Consolidate test results` (the build API does not yet serve a
  receipt's bytes, which the host-results job fetches), the fan-out (one run's stages run serially, past the deadline, and pinned per-stage runs
  do not share a pack), the prepared package closure, the browser and font set, the legs that read
  the build's artifacts (platform-compat,
  licence-gate), the prepared-mesh rule, and a green probe that agrees with Actions.
- **A shard checks what the `test` job checks, and hands its evidence back.** `core-tests.py run`
  writes CI's `[CI] <name> exit=N` markers to `test/test-results.log` (TIMEOUT, SIGNAL, TESTFAIL and
  MASKED classified as the shard loop does), writes every crash INTO the trx with
  `record-host-crash.py` (#2495), collects each bin's `test-logs/` and the hosts' temp-dir traces into
  `collected-logs/`, and on a host leaves that tree — the `test` job's artifact shape — under
  `$MW_BUILD_ARTIFACTS/testResults-shard<i>/`, which the host hands over as H9 receipts. Only THEN does
  it run `check-untyped-content.sh` over `collected-logs/`, so the logs a red is decided on survive
  the red. The scripts are the checkout's own (`--repo .`, a parity marker).
- **No suite needs a Docker daemon, and that is checked.** A host's sandbox has none (its database is
  the prepared server). Core's Postgres suites left core, and nothing in the tree references
  `Testcontainers*` or `Docker.DotNet` or builds a container in `test/` (executable code only — a
  comment or a string fixture that names one starts nothing). `core-build-stages.py --check`
  reds the day one does: such a suite either runs against the host's prepared service or re-opens
  the `containers:` debt in the same change.
- **Core's own route** (`dotnet-test.yml`). Job `admission` calls `node-repo-build-executor.yml` (the
  base tip = precheck's main, `BUILD_WEBHOOK_SECRET` signs the request) exactly when `build` would
  run. With `host`:
  - `build` does not run here, and with it `test`, `doc-gate`, `platform-compat` and `licence-gate`;
  - `host-follow` follows EVERY declared stage (`build`, each `test-shard-<i>`, `doc-gate`) with
    `build-follow.yml`;
  - `host-results` fetches each shard's handed-back results tree over HTTPS by receipt
    (`host-shard-results.py`, below) and uploads it under the `test` matrix's own artifact name
    `testResults-shard<i>-attempt<a>`, so `collect-results` consolidates a host run unchanged (download,
    attempt resolution, the coverage count, the trx and exit-marker gates);
  - `collect-results` reds a failed admission, a host stage that did not succeed and a shard whose
    results were not brought back; on the host route `platform-compat` and `licence-gate` read as not
    run, which is RED — they are owed (`dependent legs`).

  `core-build-stages.py --check` holds both matrices to the declaration. Any other admission answer
  runs every leg on Actions, as before — except a `host-forced` answer from precheck or the admission (see "Host-forced routing"
  above): `build` skips on either route, and on `host-forced` `Consolidate test results` is red
  naming the debt.

  **Core runs no job on the self-hosted pool.** Core is a public repository: it never gets the
  private `MW_RUNNER` / `MW_ARTIFACT_STORE` organisation variables, and no job of its reads the
  runners' shared store. `host-results` therefore runs on `ubuntu-latest` and takes everything from the
  build API over HTTPS with the run's own GitHub OIDC token (`permissions: id-token: write`) —
  `host-shard-results.py`:
  1. `GET {api}/{repo}/{sha}/artifacts?channel=…` — the receipts, read and shape-checked by
     `build-artifacts-fetch.py`'s reader, kept for the shard's own stage `test-shard-<i>` and its unit
     directory `testResults-shard<i>/`;
  2. `GET {api}/{repo}/{sha}/artifacts/{sha256}?channel=…` — each receipt's bytes by content hash,
     written under `--out` with its relative path PRESERVED (two `.trx` of one name in two projects
     do not collide), at most the receipt's size plus one byte read, redirects refused so the bearer
     never follows one, and the short-lived OIDC bearer re-minted once it is older than
     `build-follow.py`'s `TOKEN_REFRESH_SECONDS`, so a long fetch never sends an expired one.

  It is RED, naming the file, on: no receipt for the tree, a unit escaping it or FOREIGN to the `test`
  job's shape (`test/**/*.trx`, `test/test-results.log`, `collected-logs/**`), one path with two
  contents, a missing `test/test-results.log` or one with no `[CI] <project> exit=<n>` marker, a
  refused fetch, a body whose size or sha256 differs from its receipt (TRUNCATED or padded), and a
  written tree that is not exactly the receipts' set. 🚨 **Owed outside core:** the build API
  (MeshWeaver.Plugins `BuildHostEndpoints`) serves the receipts but not yet the object route in step 2,
  so on the host route `host-results` is RED with a 404 naming it until that lands — and the
  `Consolidate test results` owes entry is paid only when collect-results consolidates results
  fetched this way.
- **Owed outside core:** the build host must stage the `mw-core-tests` tree read-only at
  `/core-tests` (by digest, beside `/portal/app` and `/host` — MeshWeaver.Deploy's chart and runner),
  and P4's `build-host-test` job submits one pinned run per stage. Not established: which of core's
  suites run inside a host's sandbox — the shards' browser tests need the headless browser and font
  set the build-host image carries from MeshWeaver.Deploy#50 on.

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
