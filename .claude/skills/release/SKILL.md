---
name: release
description: Cut a MeshWeaver release. Two channels, both already wired in .github/workflows. CONTINUOUS = merge to main → multi-arch Docker to ACR → CD rolls memex/atioz/memex-cloud AND every install self-updates. OFFICIAL = push a v*.*.* tag → clean multi-arch images (ACR + GHCR) + NuGet packages to nuget.org. Use when shipping a release, tagging a version, publishing packages, or wiring/altering the release pipeline. Read BEFORE tagging — a tag is a public, hard-to-reverse publish.
user-invocable: true
allowed-tools:
  - Read
  - Bash
  - Grep
  - Edit
---

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
| **Rollout** | CD rolls memex/atioz/memex-cloud + all installs self-update | self-update (Continuous installs already track ACR) |

So: **merge to main = multi-arch Docker + deploy; NuGet only on a major (clean) release tag.**

## Preconditions for a release (gates)

1. **CI green** on the exact commit: the `MeshWeaver Build and Test` workflow succeeded.
   `gh run list --branch main --limit 3 --json headSha,conclusion`.
2. **PR merged** with review + conversations resolved (PR #95-style). `gh` has read+push only — it
   usually **cannot merge**; merge in the GitHub UI if `gh pr merge` is FORBIDDEN.
3. **Secrets present** (can't be read; confirm with the operator): `NUGET_PAT` (nuget.org push),
   `AZURE_CLIENT_ID`/`AZURE_TENANT_ID`/`AZURE_SUBSCRIPTION_ID` (OIDC for ACR + AKS).

## Continuous release (the common case — just merge)

```bash
# 1. Confirm CI green on the merge commit (NEVER ship a red commit — main-cd gates on this).
gh run list --branch main --limit 3 --json headSha,status,conclusion \
  --jq '.[] | "\(.headSha[0:9]) \(.status) \(.conclusion)"'
# 2. Merge the PR (UI if gh can't). main-cd.yml then fires automatically on the green test run:
#    builds multi-arch portal-ai + migration, tags <version>;<sha>;main, pushes to ACR, and
#    rolls memex/atioz/memex-cloud (kubectl set image + rollout restart + rollout status).
# 3. Every OTHER Continuous install self-updates from ACR within its poll window (no action).
```

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
- **Push (CD):** `main-cd.yml`'s `deploy` matrix rolls `memex`, `atioz`, `memex-cloud` directly.
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
az aks command invoke -g memex-aks-rg -n memexaks-cluster --command \
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
