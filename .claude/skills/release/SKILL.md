---
name: release
description: Cut a MeshWeaver release. Two channels, both already wired in .github/workflows. CONTINUOUS = merge to main → multi-arch Docker to ACR, baked and sealed → CD rolls memex/memex-cloud AND every install self-updates. OFFICIAL = push an annotated v*.*.* tag on a promoted, sealed commit → release.yml PROMOTES that set (retags in ACR + GHCR, release marker, GitHub Release, next-line bump PR). Nothing is rebuilt and nothing ships to NuGet. Use when shipping a release, tagging a version, or wiring/altering the release pipeline. Read BEFORE tagging — a tag is a public, hard-to-reverse publish.
user-invocable: true
allowed-tools:
  - Read
  - Bash
  - Grep
  - Edit
---

> The cluster name, resource group and namespace list are deployment identities and live in
> the PRIVATE `Systemorph/Memex` repo — `deployments/aks/envs.json` (cluster + environments)
> and `docs/deployments.md`. Export them once per session rather than hard-coding them here:
>
> ```bash
> eval "$(gh api repos/Systemorph/Memex/contents/deployments/aks/envs.json --jq '.content' | base64 -d \
>   | jq -r '"AKS_RG=\(.cluster.resourceGroup) AKS_CLUSTER=\(.cluster.name) NAMESPACES=\"\([.environments[].ns]|join(" "))\""')"
> ```
>
> Verified to set all three (`memex-aks-rg` / `memexaks-cluster` / the namespaces) on
> 2026-08-19. Reading it from the source of truth also means a new environment shows up here
> automatically instead of this file going quietly stale.

# /release — ship MeshWeaver (continuous + official channels)

The release pipeline is **tag-driven and already built**. This skill is the runbook for using it
safely; the design rationale lives in
[ReleaseStrategy.md](../../../src/MeshWeaver.Documentation/Data/Architecture/ReleaseStrategy.md)
and the version mechanics in
[ReleaseProcess.md](../../../src/MeshWeaver.Documentation/Data/Architecture/ReleaseProcess.md).

> 🚨 A `v*.*.*` tag fires a **public, hard-to-reverse** publish (ACR + GHCR tags, a GitHub Release).
> Never tag until the commit's CD run has SEALED and the notes page is committed.

## The two channels (don't mix them)

| | CONTINUOUS (steady-state) | OFFICIAL (a release) |
|---|---|---|
| **Trigger** | merge to `main` | push an **annotated** tag `v<major>.<minor>.<patch>` on a promoted, sealed commit |
| **Version** | `3.0.0-ci.<run#>` (build-numbered, monotonic from `$GITHUB_RUN_NUMBER`) | clean `3.0.0` — **the same bytes**, retagged |
| **Workflow** | `main-cd.yml` (after `MeshWeaver Build and Test` passes) | `release.yml` |
| **Docker** | **multi-arch** (`linux-x64;linux-arm64` → OCI image-index) → ACR | ACR retag + GHCR mirror, by digest |
| **Bake / seal** | ✅ platform content + Plugins modules, sealed per framework identity | ✅ inherited — `_releases/<clean>` copies the identity marker |
| **NuGet** | ❌ never | ❌ **retired** (last publish `3.0.0-rc13`) |
| **Rollout** | CD rolls memex/memex-cloud; an install self-updates onto it ONLY when its `Admin/UpdatePolicy` is `Continuous` **with a pattern** that admits the tag (`3.0.0-ci*`) | every install on the default (`Stable`, clean releases only) self-updates on its next check |

So: **merge to main = build + bake + seal + deploy; tag = promote.** There is no rc line: the
continuous builds ARE the pre-releases, and `PlatformVersion` always names the next clean release.

🚨 **Self-update takes clean releases by default** (maintainer, 2026-09-08: *"by default we will not
upgrade as long as no version without `-ci…` is labelled"*). A `-ci.<n>` build is rolled onto only
under `Continuous` + a `pattern` on `Admin/UpdatePolicy` — a glob over the tag, `3.0.1-ci*` — and
`Continuous` with no pattern IS `Stable` (the poller warns once, naming the pattern to set). A
pattern admits exactly the line it names: `3.0.0-ci*` never selects `3.0.1`, so following a line
ends by itself when the next release is tagged. **Fleet today: `memex` and `memex-cloud` carry
`Continuous` + `3.0.0-ci*`; change it the day `3.0.1` is tagged.** The `-latest` pointers (`3-latest`,
`3.0-latest`, `3.0.1-latest`) are a fresh install's STARTING image and never a self-update
candidate. Full rule: [ReleaseProcess.md](../../../src/MeshWeaver.Documentation/Data/Architecture/ReleaseProcess.md)
→ "Which build an install takes".

🚨 **A platform roll no longer waits for every satellite to re-seal** (2026-09-08,
`Modules:VersionStrictness`, default `Family`). A portal on 3.1.0 adopts a bundle sealed for any
3.x identity whose declared floor is satisfied AND whose platform type references measurably resolve
(`ModulePlatformLink`); one that cannot load compiles from source (or is refused, named, on a
require-prebuilt mesh). `Exact` restores the old identity-only rule; `Minimum` (the Development
default) drops the line check. So "sealed for THIS identity" is no longer the precondition for a
roll — a sealed set of the SAME LINE is. The seal still matters for the sources: a publication sealed
for the portal's own identity brings the repository's sync sources onto its commit
(`IPublicationSyncReconciler`), which is what keeps bundle and sources one unit. Full reference:
[ModuleVersioning.md](../../../src/MeshWeaver.Documentation/Data/Architecture/ModuleVersioning.md)
→ "Version strictness".

## Preconditions for a release (gates the lane enforces — check them before tagging)

1. **The commit is on `main` and its CD run SEALED**: `Promote`, `Verify every image shipped` and
   `Plugins: bake + seal` all `success` — read the seal JOB, never the run's conclusion:
   ```bash
   gh api "repos/Systemorph/MeshWeaver/actions/runs?head_sha=<sha>" \
     --jq '.workflow_runs[] | select(.name=="Continuous Delivery (main)") | .id' | head -1 \
     | xargs -I{} gh api "repos/Systemorph/MeshWeaver/actions/runs/{}/jobs?per_page=100" \
       --jq '.jobs[] | select(.name | test("Promote|Verify every|bake \\+ seal")) | "\(.name): \(.conclusion)"'
   ```
   🚨 **Name the target set only AFTER this reads success, and state the requirement as a COMMIT until
   then** ("the first sealed set cut from `<sha>` or later"). A set's name carries its RUN number, and
   the one-pending-slot rule cancels the pending run on every merge to `main` while the commit stays
   valid — 7910 → 7911 → 7912 inside 20 minutes on 2026-09-06. Send a set's name and its digests in
   ONE message. Full reference: [ContinuousDeliveryContract](../../../src/MeshWeaver.Documentation/Data/Architecture/ContinuousDeliveryContract.md) → "A set is named by its RUN".
2. **`PlatformVersion` at that commit equals the tag** (`3.0.0` ↔ `v3.0.0`). The lane refuses a
   mismatch; so does it refuse a `-rc`/`-beta` suffix and a lightweight tag.
3. **The notes page exists at that commit**: `src/MeshWeaver.Documentation/Data/ReleaseNotes/3_0_0.md` (a
   FLAT file — a `3_0_0/index.md` is embedded as `_3_0_0` and served at the wrong path)
   (`Category: Release Notes`). The GitHub Release is published FROM it.
4. **Secrets present** (can't be read; confirm with the operator): `AZURE_CLIENT_ID`/`AZURE_TENANT_ID`/
   `AZURE_SUBSCRIPTION_ID` (OIDC → ACR + the artifact stores, via the `release` environment),
   `MESHWEAVER_APP_ID`/`MESHWEAVER_APP_PRIVATE_KEY` (the next-line PR), repo var
   `BAKE_PUBLISH_TARGETS`. The lane's `preflight` job fails RED naming any that is missing.
5. **The OWASP ZAP scans ran against the candidate and every finding has a disposition on the
   notes page** — the lane cannot check this one; it is the operator's gate (next section).

## Security scan — every release, before the tag

Both runs, against the deployment that serves the candidate build. Full reference, verdict rules
and the findings each release carried:
[SecurityScanning.md](../../../src/MeshWeaver.Documentation/Data/Architecture/SecurityScanning.md).

```bash
ZAP=ghcr.io/zaproxy/zaproxy:2.17.0          # pinned by VERSION; the version goes on the notes page; bump deliberately
OUT=~/.cache/zap-scan-$(date -u +%F); mkdir -p "$OUT/public" "$OUT/auth"   # a PLAIN dir: Docker mounts it
# Each run's console output IS its log (the verdict is its last line), so it is captured; the
# scripts exit 0 PASS / 2 WARN / 1 FAIL / 3 scanner error — echo it, never let it end the shell.
# 1. PUBLIC, ACTIVE — anonymous, safe against production
docker run --rm -v "$OUT/public":/zap/wrk/:rw -t "$ZAP" \
  zap-full-scan.py -t https://memex.meshweaver.cloud -r public-full.html -J public-full.json -w public-full.md \
  > "$OUT/public/public-full.log" 2>&1; echo "public exit=$?"
# 2. AUTHENTICATED, PASSIVE — never zap-full-scan with a session on production (it fires payloads
#    AS THE USER at every write endpoint). $COOKIE = the full Cookie header of a real browser
#    session (.AspNetCore.Cookies; an API token does not authenticate the SPA). -j = AJAX spider,
#    the only thing that reaches the bundles a signed-in page loads.
docker run --rm -v "$OUT/auth":/zap/wrk/:rw -t "$ZAP" \
  zap-baseline.py -t https://memex.meshweaver.cloud -j -r auth-report.html -J auth-report.json -w auth-report.md \
  -z "-config replacer.full_list(0).description=sess -config replacer.full_list(0).enabled=true -config replacer.full_list(0).matchtype=REQ_HEADER -config replacer.full_list(0).matchstr=Cookie -config replacer.full_list(0).regex=false -config replacer.full_list(0).replacement=$COOKIE" \
  > "$OUT/auth/auth-baseline.log" 2>&1; echo "authenticated exit=$?"
# 3. VERDICT — the last line of each captured log; FAIL-NEW must be 0 on both, and rule 10003 blocks:
tail -1 "$OUT/public/public-full.log" "$OUT/auth/auth-baseline.log"
grep -h "Vulnerable JS Library" "$OUT"/*/*.log        # PASS = clean; WARN-NEW = a shipped library with advisories → blocker
```

Then write the two verdict lines and a disposition for EVERY `WARN-NEW` — fixed, or carried with
the rule id, the reason and the tracking issue — on the notes page (the 3.0.0 page's *Security*
section is the shape). A WARN without a disposition is an untriaged finding, not a pass. The raw
reports stay under `$OUT` (they are a site map with bodies); the cookie is a live session — keep it
out of every repo and sign out when done. 🚨 The version string on a package manifest is not the
verdict: the DOMPurify finding on 3.0.0 (MeshWeaver#3378) lived inside a NuGet package's static
bundle, invisible to Dependabot and to any source build — only the served bytes tell.

## Continuous release (the common case — just merge)

```bash
# 1. Confirm CI green on the merge commit (NEVER ship a red commit — main-cd gates on this).
gh api "repos/Systemorph/MeshWeaver/actions/runs?branch=main&per_page=3" \
  --jq '.workflow_runs[] | "\(.name) \(.head_sha[0:9]) \(.status) \(.conclusion)"'
# 2. Merge the PR. main-cd.yml then fires automatically on the green test run:
#    builds multi-arch portal-ai + migration + mw-plugin-test, promotes <version>;<sha>;main to ACR,
#    bakes + seals the platform content and the Plugins modules, and rolls memex/memex-cloud.
# 3. Every OTHER install whose Admin/UpdatePolicy is Continuous + a pattern admitting the tag
#    (3.0.0-ci*) self-updates from ACR on the next publication EVENT (no action). Stable installs
#    (the default) wait for the clean release.
```

## 🚨 A merged fix can look SHIPPED while producing no image — verify the IMAGE, never the tick

**Publishes are BATCHED (maintainer, 2026-08-16).** When repo var `CD_BATCH_WINDOW_MINUTES` is set,
a green merge whose newest published set is younger than the window does NOT publish — the hourly
reconciler publishes main's tip on its next tick instead. The decision step's summary says
`🕐 Batched` when this happened, so "merged + green + no new tag" inside the window is INTENTIONAL,
not a failure. To ship a specific fix immediately: `gh workflow run main-cd.yml --ref main` (the
manual path bypasses the window by construction). The probe fails OPEN — an unreadable registry
publishes rather than blocks.

🚨 **The window is NOT the wait.** A batched merge leaves main's HEAD without an image set, which is
exactly what the reconciler heals — so it ships on the next TICK, whatever the window says. The
window controls how many merges ride one image; the **reconcile cadence controls the tail**. Now:
window 60 min, one tick at `:23`, so the worst case is ~1 h plus the ~20 min build.

🚨 **Publication frequency is NOT portal-restart frequency — but only because a floor now exists.**
Since #1773 an install checks per publication *event*, and `UpdatePolicyKind` picks a channel, not a
cadence. #1780 added **`SelfUpdate__MinRollInterval`**, a restart budget set to `1h` in
`values.aks.yaml`, deliberately matched to the tick. **If you change one, change the other.**

🚨 **Promoted is not SEALED.** `Promote` and `Verify every image shipped` can be green while
`Plugins: bake + seal` failed — the images exist, no install can adopt them, and a tag on that
commit is refused by `release.yml`. The self-updater holds such a build with
`heldReason: no sealed content bake …` on `Admin/UpdatePolicy`.

### The `main-cd.yml` trigger gate and its three SILENT consequences

`main-cd.yml`'s `workflow_run` path is gated on `event == 'push' && head_branch == 'main'` — that
gate is what stops a **fork's** pull_request run (whose `head_branch` can also be "main") from
publishing untrusted code with this repo's secrets, so never relax it. Three consequences trip
people up, all silent:

1. **No Build-and-Test run on main at all.** CD reacts to that workflow completing; if it never ran
   on the merge commit (a CI incident, a stalled queue), CD sits `SKIPPED` with nothing to react to.
2. **`workflow_dispatch` of Build-and-Test can never ship.** It RUNS and genuinely tests the merge
   commit, so main shows a **green Build-and-Test** — but its `event` is `workflow_dispatch`, not
   `push`, so CD still skips.
3. **A cancelled main run publishes nothing.** CD's delivery gate keys on `Consolidate test results`
   reaching `success` **for that SHA**. This is why runs on `main` are never cancelled.

**None of these is terminal.** CD carries a **reconciler**: an hourly `schedule` (plus its own
`workflow_dispatch`) resolves main's tip through the API, asks ACR whether that commit has the
complete image set, and publishes only when it does not — bounded at 3 attempts per commit.

- **To kick CD by hand: `gh workflow run main-cd.yml --ref main`.**
- It heals **HEAD, not the commit that failed** — deliberately: re-publishing older code would mint
  a higher `-ci.<n>` for it and roll every install *backwards*.
- **Publication is all-or-nothing**: each leg pushes only a non-selectable `staging-<sha>-<run_id>`
  tag, and the `promote` job applies the real tags only after every leg succeeds, ending with
  `memex-portal-ai:<version>` — the single write the self-updater acts on.

**Before believing something is deployed, check the IMAGE, never the green tick** — the Fleet
Console (`/Hosting/Console`) shows the RUNNING version per instance; the cluster read is the
break-glass form:

```bash
az acr repository show-tags -n meshweaver --repository memex-portal-ai --orderby time_desc --top 5 -o tsv
az aks command invoke -g "$AKS_RG" -n "$AKS_CLUSTER" --command \
  "kubectl get deploy -A -o custom-columns=NS:.metadata.namespace,IMAGE:.spec.template.spec.containers[0].image --no-headers | grep memex-portal-ai"
.github/scripts/check-image-set.sh <short-sha> [<plugins-short-sha>]   # the exact assertion CD itself makes
```

## Official release (promote a sealed continuous build)

```bash
# 0. Pick the commit: on main, its CD run SEALED (preconditions above), PlatformVersion == the tag.
grep -m1 '<PlatformVersion Condition' Directory.Build.props          # e.g. 3.0.0
# 1. Make sure the notes page is committed at that commit — WITH the Security section: both OWASP
#    ZAP verdict lines and a disposition per WARN (section above):
#    src/MeshWeaver.Documentation/Data/ReleaseNotes/3_0_0.md   (flat — never <version>/index.md)
# 2. Tag it ANNOTATED and push — this is the whole release (maintainer: only once every open
#    issue is closed):
git tag -a v3.0.0 -m "MeshWeaver 3.0.0" <sha> && git push origin v3.0.0
#    → release.yml: resolves the sealed set for <sha> → asserts complete + sealed → writes
#      _releases/3.0.0 → retags memex-migration, mw-plugin-test, memex-portal-ai (last) → mirrors
#      to GHCR → publishes the GitHub Release → opens "release: PlatformVersion 3.0.0 → 3.1.0".
# 3. Merge that bump PR the same day. Until it merges, continuous builds sort BELOW the release.
gh api "repos/Systemorph/MeshWeaver/pulls?state=open&head=Systemorph:release/open-3.1.0-line" --jq '.[].html_url'
```

What the lane REFUSES, each with a red step naming the fix: a non-clean version (`v3.0.0-rc14`),
a lightweight tag, a commit not on `main`, a `PlatformVersion` mismatch, a commit `main-cd` never
promoted, a set whose bake is not sealed, and a release with no notes page.

## "All portals update" — how (and how to confirm)

Two mechanisms, both live:
- **Push (CD):** `main-cd.yml`'s `deploy` matrix rolls `memex` and `memex-cloud` directly.
- **Pull (self-update):** `SelfUpdateHostedService` runs on EVERY install. It reads
  `Admin/UpdatePolicy` (default **Stable** — clean tags only, i.e. what `release.yml` promoted),
  lists ACR tags, walks the eligible ones newest-first and takes the first one whose set is SEALED
  for its identity (`VersionSelect.SelectCandidates` + `ReleaseAvailability`), then patches its own
  Deployment in-pod. `Continuous` + `pattern` (e.g. `3.0.0-ci*`) makes that line's `-ci.<n>` builds
  eligible; `Continuous` without a pattern is `Stable`.

Confirm a roll-out — the RUNNING version per instance is on the Fleet Console (`/Hosting/Console`
on memex.meshweaver.cloud), and a roll you order by hand is a `Roll` `Hosting/InstanceAction` with
the tag, never `kubectl set image` (policy:
[OperatingFromThePortal.md](../../../src/MeshWeaver.Documentation/Data/Architecture/OperatingFromThePortal.md)).
The cluster reads below are the break-glass form of the same three questions:
```bash
# ACR has the new tag:
az acr repository show-tags -n meshweaver --repository memex-portal-ai -o tsv | tail
# Each portal serves + runs the new image (break-glass: private cluster → az aks command invoke):
az aks command invoke -g "$AKS_RG" -n "$AKS_CLUSTER" --command \
  "kubectl -n <ns> get deploy memex-portal-deployment -o jsonpath='{.spec.template.spec.containers[0].image}'"
# The release's identity marker (what a Stable install's gate reads):
.github/scripts/check-release-availability.sh 3.0.0 meshweaver-content plugins   # needs BAKE_PUBLISH_TARGETS + az login
```

## Verify a release is healthy (before declaring done)

- Migration log shows `Database migration completed. Version: N` AND the portal serves HTTP 200
  (see [DeploymentAKS.md](../../../src/MeshWeaver.Documentation/Data/Architecture/DeploymentAKS.md)).
- The self-updater logged its decision (picked newer / already current / held with a reason), not
  a 403 (missing AcrPull workload-identity grant — armed path no-ops with a logged 403, never crashes).
- `Admin/UpdatePolicy.heldReason` is empty: a hold names an unsealed bundle, and that is a delivery
  incident, not a release.

## Pipeline files (edit here to change the pipeline)

- `.github/workflows/main-cd.yml` — continuous: multi-arch build + ACR promote + bake/seal + CD deploy.
- `.github/workflows/release.yml` — official: promote a sealed set on a `v*.*.*` tag; GitHub Release;
  next-line bump PR.
- `.github/workflows/base-image-acr.yml` — the hand-authored multi-arch base, on demand.
- `.github/scripts/check-image-set.sh` / `check-release-availability.sh` / `publish-bake-bundles.sh`
  — the set, the seal, the marker; `release.yml` reuses the first two verbatim.
- `Directory.Build.props` — `PlatformVersion` + the `-ci.<n>` monotonic build-number logic.
