---
name: release
description: Cut a MeshWeaver release. Two channels, both already wired in .github/workflows. CONTINUOUS = merge to main → multi-arch Docker to ACR → CD rolls memex/memex-cloud AND every install self-updates. OFFICIAL = push a v*.*.* tag → clean multi-arch images (ACR + GHCR) + NuGet packages to nuget.org. Use when shipping a release, tagging a version, publishing packages, or wiring/altering the release pipeline. Read BEFORE tagging — a tag is a public, hard-to-reverse publish.
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
> Verified to set all three (`memex-aks-rg` / `memexaks-cluster` / the three namespaces) on
> 2026-08-19. Reading it from the source of truth also means a new environment shows up here
> automatically instead of this file going quietly stale.

# /release — ship MeshWeaver (continuous + official channels)

The release pipeline is **tag-driven and already built**. This skill is the runbook for using it
safely; the design rationale lives in
[ReleaseStrategy.md](../../../src/MeshWeaver.Documentation/Data/Architecture/ReleaseStrategy.md).

> 🚨 A `v*.*.*` tag fires a **public, hard-to-reverse** publish (NuGet 3.0.0 can't be unlisted-then-
> reused). Never tag until CI is green on the merge commit and the version is confirmed.

## The two channels (don't mix them)

| | CONTINUOUS (steady-state) | OFFICIAL (a release) |
|---|---|---|
| **Trigger** | merge to `main` | push tag `v*.*.*` |
| **Version** | `3.0.0-ci.<run#>` (build-numbered, monotonic from `$GITHUB_RUN_NUMBER`) | clean `3.0.0` (`PublicRelease=true`) |
| **Workflow** | `main-cd.yml` (after `MeshWeaver Build and Test` passes) | `release-images.yml` + `release-packages.yml` |
| **Docker** | **multi-arch** (`linux-x64;linux-arm64` → OCI image-index) → ACR | multi-arch → ACR **+** GHCR |
| **NuGet** | ❌ never | ✅ **`dotnet pack` → nuget.org** (clean version, no build number) |
| **Rollout** | CD rolls memex/memex-cloud + all installs self-update | self-update (Continuous installs already track ACR) |

So: **merge to main = multi-arch Docker + deploy; NuGet only on a major (clean) release tag.**

## Preconditions for a release (gates)

1. **CI green** on the exact commit: the `MeshWeaver Build and Test` workflow succeeded.
   `gh run list --branch main --limit 3 --json headSha,conclusion`.
2. **PR merged** with review + conversations resolved. Merge capability is CREDENTIAL × REPO —
   measure it (`gh api repos/Systemorph/<repo> --jq '.permissions'`), never assume; see
   [/pullrequest](../pullrequest/SKILL.md).
3. **Secrets present** (can't be read; confirm with the operator): `NUGET_PAT` (nuget.org push),
   `AZURE_CLIENT_ID`/`AZURE_TENANT_ID`/`AZURE_SUBSCRIPTION_ID` (OIDC for ACR + AKS).

## Continuous release (the common case — just merge)

```bash
# 1. Confirm CI green on the merge commit (NEVER ship a red commit — main-cd gates on this).
gh run list --branch main --limit 3 --json headSha,status,conclusion \
  --jq '.[] | "\(.headSha[0:9]) \(.status) \(.conclusion)"'
# 2. Merge the PR (UI if gh can't). main-cd.yml then fires automatically on the green test run:
#    builds multi-arch portal-ai + migration, tags <version>;<sha>;main, pushes to ACR, and
#    rolls memex/memex-cloud (kubectl set image + rollout restart + rollout status).
# 3. Every OTHER Continuous install self-updates from ACR on the next publication EVENT (no action).
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
window controls how many merges ride one image; the **reconcile cadence controls the tail**. Both
were 3 h until 2026-08-17, which is the only reason a merge could sit unpublished that long
(maintainer: *"3 hour window is not acceptable"*). Now: window 60 min, one tick at `:23`, so the
worst case is ~1 h plus the ~20 min build. Raising the cadence raises publish latency — it does not
save runner time, which the 3-attempts-per-commit budget already bounds.

🚨 **Publication frequency is NOT portal-restart frequency — but only because a floor now exists.**
Since #1773 an install checks per publication *event*, and `UpdatePolicyKind` picks a channel, not a
cadence — so for a few hours there was nothing at all between "an image published" and "every portal
restarted". #1780 closed that with **`SelfUpdate__MinRollInterval`**, a restart budget set to `1h` in
`values.aks.yaml` beside the 24h/6h/1h trade. It is deliberately matched to the tick, so hourly
publication means hourly delivery and no more. **If you change one, change the other, and in this
order:** a faster tick with the floor left behind buys nothing (the floor gates the roll), while a
shorter floor with the tick left behind just restarts pods onto the same image. Note the key that
used to do this job, `SelfUpdate__PollInterval`, was RENAMED rather than deleted — it sat in the
chart inert, reading as a live daily throttle, until #1778.

### The `main-cd.yml` trigger gate and its three SILENT consequences

`main-cd.yml`'s `workflow_run` path is gated on `event == 'push' && head_branch == 'main'` — that
gate is what stops a **fork's** pull_request run (whose `head_branch` can also be "main") from
publishing untrusted code with this repo's secrets, so never relax it. Three consequences trip
people up, all silent:

1. **No Build-and-Test run on main at all.** CD reacts to that workflow completing; if it never ran
   on the merge commit (a CI incident, a stalled queue), CD sits `SKIPPED` with nothing to react to.
2. **`workflow_dispatch` of Build-and-Test can never ship.**
   `gh workflow run "MeshWeaver Build and Test" --ref main` RUNS and genuinely tests the merge
   commit, so main shows a **green Build-and-Test** — but its `event` is `workflow_dispatch`, not
   `push`, so CD still skips. The most convincing possible "it shipped" signal, and no image.
3. **A cancelled main run publishes nothing.** CD's delivery gate keys on `Consolidate test results`
   reaching `success` **for that SHA**; CD does fire on a cancelled run (it subscribes with
   `types: [completed]`) and simply finds no success to act on. This is why runs on `main` are never
   cancelled — see [/ci](../ci/SKILL.md).

**None of these is terminal any more.** CD carries a **reconciler**: an hourly `schedule` (plus its
own `workflow_dispatch`) resolves main's tip through the API, asks ACR whether that commit has the
complete four-image set, and publishes only when it does not — bounded at 3 attempts per commit, with
every attempt and the final give-up written to the `ci-failure` issue. So:

- **To kick CD by hand: `gh workflow run main-cd.yml --ref main`** (it heals HEAD; it can never
  publish an untested tree — it re-checks `Consolidate test results` on the commit it resolved).
- It heals **HEAD, not the commit that failed** — deliberately. The version tag comes from the
  BUILDING run's number, so re-publishing older code would mint a higher `-ci.<n>` for it and roll
  every install *backwards* past `VersionSelect`'s monotonic-build-number assumption.
- **Publication is all-or-nothing**: each leg pushes only a non-selectable
  `staging-<sha>-<run_id>` tag, and the `promote` job applies the real tags only after all five legs
  succeed, ending with `memex-portal-ai:<version>` — the single write the self-updater acts on.

**Before believing something is deployed, check the IMAGE, never the green tick:**

```bash
az acr repository show-tags -n meshweaver --repository memex-portal-ai --orderby time_desc --top 5 -o tsv
az aks command invoke -g "$AKS_RG" -n "$AKS_CLUSTER" --command \
  "kubectl get deploy -A -o custom-columns=NS:.metadata.namespace,IMAGE:.spec.template.spec.containers[0].image --no-headers | grep memex-portal-ai"
.github/scripts/check-image-set.sh <short-sha>   # the exact assertion CD itself makes
```

Then the portals self-update **on the publication event, not on a poll** (#1773): one pass at
startup, then a check per build-completion event. `SelfUpdateOptions.PollInterval` no longer exists
— `RetryInterval` (6 h) is only the backstop for a faulted watch — so an install that is behind is
behind for a reason worth reading, not because a timer has not fired yet. `kubectl rollout restart`
still forces an immediate check via the startup pass.

## Official release (cut a versioned release + publish NuGet)

```bash
# 0. Be on a green main. Confirm the version (Directory.Build.props PlatformVersion).
grep -m1 PlatformVersion Directory.Build.props          # e.g. 3.0.0
# 1. Tag the merge commit and push — this is the whole release:
git tag v3.0.0 && git push origin v3.0.0
#    → release-images.yml : multi-arch clean images → ACR + GHCR
#    → release-packages.yml: dotnet pack → nuget.org (VERSION = tag without 'v', clean)
# 2. After the release lands, bump to the NEXT line so continuous builds move on:
#    edit Directory.Build.props PlatformVersion → 3.1.0 (or 3.0.1 for a patch line), commit.
```

## "All portals update" — how (and how to confirm)

Two mechanisms, both live:
- **Push (CD):** `main-cd.yml`'s `deploy` matrix rolls `memex` and `memex-cloud` directly.
- **Pull (self-update):** `AddSelfUpdate()` (MemexConfiguration.cs) runs `SelfUpdateHostedService`
  on EVERY install. It reads `Admin/UpdatePolicy` (default **Continuous**), lists ACR tags
  (`AcrTagLister`), and when a newer tag exists patches its own Deployment in-pod
  (`KubernetesDeploymentUpdater`) — so installs NOT in the CD matrix, and arm64 local k3s, update
  too. **Multi-arch images are the prerequisite** for arm64 self-update.

Confirm a roll-out:
```bash
# ACR has the new tag:
az acr repository show-tags -n meshweaver --repository memex-portal-ai -o tsv | tail
# Each portal serves + runs the new image (private cluster → az aks command invoke):
az aks command invoke -g "$AKS_RG" -n "$AKS_CLUSTER" --command \
  "kubectl -n <ns> get deploy memex-portal-deployment -o jsonpath='{.spec.template.spec.containers[0].image}'"
# NuGet (official only):
#   https://www.nuget.org/profiles/<owner> — the new clean version is listed.
```

## Verify a release is healthy (before declaring done)

- Migration log shows `Database migration completed. Version: N` AND the portal serves HTTP 200
  (see [DeploymentAKS.md](../../../src/MeshWeaver.Documentation/Data/Architecture/DeploymentAKS.md)).
- The self-updater logged its decision (picked newer / already current), not a 403 (missing
  AcrPull workload-identity grant — armed path no-ops with a logged 403, never crashes).

## Pipeline files (edit here to change the pipeline)

- `.github/workflows/main-cd.yml` — continuous: multi-arch build + ACR push + CD deploy.
- `.github/workflows/release-images.yml` — official: multi-arch images → ACR + GHCR.
- `.github/workflows/release-packages.yml` — official: NuGet publish (tag-only, clean version).
- `Directory.Build.props` — `PlatformVersion` + the `-ci.<n>` monotonic build-number logic.
