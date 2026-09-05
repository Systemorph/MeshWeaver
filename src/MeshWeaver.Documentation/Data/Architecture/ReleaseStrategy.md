---
NodeType: Markdown
Name: "Release & Self-Update Strategy"
Abstract: "The end-to-end production model: the merge preconditions (build green with warnings-as-errors, tests green, reviewed, comments resolved), the version scheme (current-build vs official), CI producing ALL images to ACR tagged by version, and policy-driven SELF-UPDATE — each install (AKS, local k3s, MAUI) rolls itself to the newest image per Admin/UpdatePolicy (Stable | Continuous | None, default Continuous)."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#0e7490'/><path d='M12 5a7 7 0 1 0 6.3 4' fill='none' stroke='white' stroke-width='1.8' stroke-linecap='round'/><path d='M18.5 4.5v3.2h-3.2' fill='none' stroke='white' stroke-width='1.8' stroke-linecap='round' stroke-linejoin='round'/></svg>"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Release"
  - "Deployment"
  - "CI/CD"
  - "Self-Update"
---

# Release & Self-Update Strategy

The production model in one picture:

```
PR ──[merge preconditions]──▶ main ──[CI: build ALL images, tag by version]──▶ ACR
                                                                                 │
                                  each install polls ACR per Admin/UpdatePolicy ◀┘
                                  Continuous → newest build · Stable → newest release · None → manual
                                                 │
                  AKS / local k3s: patch own deployment   MAUI: notify + relaunch
```

One central version, two channels (continuous build vs official release), and every install keeps
**itself** up to date. The version mechanics live in
[Release Process & Versioning](/Doc/Architecture/ReleaseProcess); where the images go is
[Deployment](/Doc/Architecture/Deployment).

---

## 1. Merge preconditions

A PR may merge to `main` only when **all four** hold. CI enforces (a)+(b); branch protection +
a reviewer enforce (c)+(d). The checklist is in `.github/pull_request_template.md`.

| | Precondition | Enforced by |
|---|---|---|
| **a** | **Build is green with warnings-as-errors** | **Live.** `dotnet-test.yml`'s build step runs `dotnet build --no-restore -c Release -p:CIRun=true -warnaserror`, and `Directory.Build.props` additionally sets `TreatWarningsAsErrors=true` centrally — so a compiler warning cannot merge. Doc-*completeness* warnings (CS1591/CS1573/CS1712) and the NU1510 restore advisory are suppressed centrally (`NoWarn`); NuGet **vulnerability** advisories (NU1901–NU1904) stay visible as warnings via `WarningsNotAsErrors`, so a newly-disclosed transitive CVE doesn't fail every unrelated PR. Doc-*quality* warnings (CS1574/CS0419/CS1570) are NOT suppressed — they fail the gate. |
| **b** | **Tests are green** | The sharded suite + the consolidated *Test Results* check (`dotnet-test.yml`). |
| **c** | **Reviewed (AI _or_ human)** | A required approving review (branch protection). We don't care whether the reviewer is a person or an AI. `.github/CODEOWNERS` requests an owner. |
| **d** | **Review comments dealt with** | "Require conversation resolution before merging" (branch protection). We expect at least one comment or code change tied to the review. |

> **Branch-protection settings to configure once** (GitHub → Settings → Branches → `main`; `gh` can't
> set these): require the *Build* and *Test Results* status checks; require ≥1 approving review;
> require review from Code Owners; require conversation resolution before merging.

---

## 2. Versions: current-build vs official

The one number is `PlatformVersion` in `Directory.Build.props` — **today `3.1.0`**, the *next*
release. Every build derives its version from it ([details](/Doc/Architecture/ReleaseProcess)):

- **Current build (continuous):** `3.1.0-ci.<n>` — the default. `<n>` is the **GitHub Actions run
  number** (monotonic), so newer builds always sort higher. 🔴 This monotonicity is load-bearing: the
  self-updater picks the *newest* version, and the old seconds-since-midnight build number reset at
  midnight (a morning build would sort below the prior evening's). Do not revert it.
- **Official release:** clean `3.1.0` — **the same bytes as one continuous build**, promoted by
  `release.yml` when the annotated tag `v3.1.0` is pushed on a commit `main-cd` has promoted and
  sealed. Nothing is rebuilt.

> 🚨 **No pre-release label on the core, and no rc line.** `3.1.0-ci.<n>` sorts above every
> `3.0.0-*` by its minor number, below the clean `3.1.0`, and below the next line's first
> `3.2.0-ci.1` — which is why the bump to `3.2.0` happens the day `3.1.0` is tagged (the lane opens
> that pull request). The rc labels were retired on 2026-09-05: SemVer compares them as text, so
> `rc13` sorted below `rc2`. See [Release Process & Versioning](/Doc/Architecture/ReleaseProcess) §1.

### Cutting an official release and starting the next line

1. **Cut the official release:** commit the notes page `Doc/ReleaseNotes/3_1_0`, then push an
   annotated `v3.1.0` tag on a promoted, sealed commit. `release.yml` retags that commit's set
   with the clean version in ACR, mirrors it to GHCR, records the release marker and publishes the
   GitHub Release. Stable installs pick it up.
2. **Start the next line:** merge the pull request the lane opened —
   `PlatformVersion` `3.1.0` → `3.2.0`. Continuous builds are now `3.2.0-ci.<n>`, above the release.

---

## 3. CI produces ALL images, tagged by version, on ACR

Both channels publish the full image set to **`meshweaver.azurecr.io`**, tagged by the **version
string** — that tag is what each install compares.

| Channel | Trigger | Version baked + image tag | Workflow |
|---|---|---|---|
| **Continuous** | green merge to `main` | `3.1.0-ci.<run#>` (+ short SHA + moving `main`) | `main-cd.yml` |
| **Official** | push an annotated `v*.*.*` tag on a promoted, sealed commit | clean `3.1.0` — the continuous set **retagged** in ACR and mirrored to GHCR; nothing rebuilt | `release.yml` |

So the continuous build produces all images, the release names one of them, and a running install
only has to list ACR tags and pick the best per its policy. (`main-cd.yml` still rolls the environments once as
the bootstrap; steady-state updates are the self-updater below.)

Two properties of the continuous leg matter to a reader of tags:

- **Only the version tag is selectable.** `VersionSelect.PlatformVersionTag` requires
  `^\d+\.\d+\.\d+`, so the moving `main` pointer and the per-run `staging-<sha>-<run_id>` tag are
  invisible to every self-updater by construction.
- **Publication is all-or-nothing.** Each leg pushes only its staging tag; the `promote` job applies
  the real tags after all five legs succeed, ending with `memex-portal-ai:<version>` — the single
  write the self-updater acts on.

---

## 4. The update policy — `Admin/UpdatePolicy`

A single mesh node, edited by platform admins under **Settings → Updates** (a dropdown bound straight
to the node). Default **Continuous**.

| Policy | Behaviour |
|---|---|
| **Continuous** (default) | Roll to the newest tag on ACR, **including** build-numbered continuous builds. As soon as a new build number lands, the install picks it up. |
| **Stable** | Roll only to the newest **clean release** (no build number). |
| **None** | Never auto-update. Apply updates manually (operator, or the admin tab's *Apply available update now*). |

The poller (`SelfUpdateHostedService`) reads this node live: changing the policy re-drives it
immediately. It checks ACR a few times a day, records the latest tag it sees on the node
(`LatestAvailableTag`, surfaced in the admin tab and to the MAUI notifier), and — where it can —
applies the update.

---

## 5. How each install updates

| Target | What "update" does |
|---|---|
| **AKS** (`memex` portal) | The portal **patches its own Deployment image from inside the pod** (Kubernetes API, projected service-account token). It rolls the **portal AND migration** deployments to the new tag together; k8s does the rolling update. |
| **Local k3s on Mac** | Same Helm chart as AKS → same in-pod patch. (A version-specific tag pulls even under `imagePullPolicy: IfNotPresent` because the tag isn't cached. A pure local-build dev loop without ACR is effectively `None`.) See [LocalColimaMac](/Doc/Architecture/LocalColimaMac). |
| **Monolith** (non-k8s) | No self-patch (no service-account token) → detect-only: records `LatestAvailableTag` for visibility; the operator updates the binary. |
| **MAUI app** | **Detect + notify.** A sandboxed app can't replace its own binary, so on connecting to a remote mesh that runs a newer platform version it shows an in-app alert: update from the store and relaunch. |

### The availability gate — "newer" is not sufficient

Whichever target it is, a newer tag is **not** on its own a reason to roll. Before patching
anything, the poller asks the [release availability gate](../ReleaseGates) whether every package
this deployment runs actually has a usable artifact for that release. If one does not, the update is
**held**: the poller stays on the current image, writes the refusal to `Admin/UpdatePolicy`
(`HeldTag`, `HeldReason`, `HeldAt`), and the Updates tab reports it — the About tab shows
`⏸️ Update held` rather than `⬆️ Update available`, because an install that has refused a build must
not look like one that is about to take it.

A hold is not sticky. It is re-evaluated on every poll and every green-build event and clears itself
the moment the missing artifact is published, so nothing has to be un-stuck by hand. The manual
**Apply update now** button consults the same verdict — a gate the unattended path respects and the
manual one does not is no gate at all.

🚨 "Cannot determine" is a hold too, and it says so distinctly: an unreadable artifact catalogue is
an availability incident to fix, not an incompatible release to re-bake.

### Postgres ("auto-update pg")

- **Schema / `db_version` is kept in step automatically:** the **migration** container is rolled to
  the same version as the portal on every update — the migration is what applies schema changes, so
  the database is always current for the running code. This is the meaningful, safe "auto-update pg."
- **The Postgres SERVER image stays at its pinned major** (e.g. `pgvector:pg17`). A **major** upgrade
  is **never** automated — it needs `pg_upgrade` against the data volume (data-loss risk) and is a
  deliberate, manual runbook. On AKS/Container Apps Postgres is a managed Flexible Server (Azure
  handles minor upgrades; the in-pod updater never touches it).

---

## 6. AKS prerequisites (for the in-pod patch + ACR polling)

The Helm chart (`deploy/helm/templates/memex-portal/`) ships these so the portal **can** update itself:

- **`serviceaccount.yaml`** — `memex-portal-sa` (the pod runs as it).
- **`rbac.yaml`** — a `Role` granting `get,patch` on the portal + migration Deployments **only**
  (scoped by `resourceNames`), bound to the SA. Without it the PATCH is `403`; the poller logs and
  keeps ticking (no crash).
- **`deployment.yaml`** — sets `serviceAccountName`, and (when `selfUpdate.azureClientId` is set) the
  `azure.workload.identity/use` label + `AZURE_CLIENT_ID`.

For **ACR polling** on AKS you must, once per environment, create a user-assigned managed identity,
**federate** it to `system:serviceaccount:<ns>:memex-portal-sa`, grant it **AcrPull** on
`meshweaver.azurecr.io`, and set `selfUpdate.azureClientId` (e.g. in `values.aks.yaml` / via Key
Vault) to its client id. (Mirrors the existing `pgbackrest-sa` workload-identity wiring.) The
in-cluster Deployment PATCH works without this; it only authenticates the tag-list call.

---

## 7. Operate & verify

- **Set the policy:** Settings → Updates (platform admin). The dropdown writes `Admin/UpdatePolicy`.
- **Watch a continuous roll (AKS):** merge to `main` → a new `…-ci.<n>` tag lands on ACR → within the
  poll window a `Continuous` install patches `memex-portal-deployment` + `memex-migration-deployment`
  (`kubectl rollout status`).
- **Pin an environment:** set the policy to `None` (or `Stable` for releases-only).
- **Manual apply:** Settings → Updates → *Apply available update now* (installs that can self-patch).

The decision logic (which tag each policy picks; "is newer") is unit-pinned in
`VersionSelectTest`; the enum dropdown in `MeshNodeEditorFieldTest`.

---

## 8. Cut a release — operator runbook

Three operator actions, two independent channels, and **steady state is self-update**: you *push
images* (by merging or tagging) and installs roll **themselves** per `Admin/UpdatePolicy` — you do
not `kubectl set image` by hand. The manual [AKS runbook](/Doc/Architecture/DeploymentAKS) is the
**bootstrap / break-glass** path (first install, or forcing a specific tag).

| Step | Action | What ships | Who rolls to it |
|---|---|---|---|
| **a** | **Merge to `main`** (preconditions §1 green) | `main-cd.yml` builds the **multi-arch** image set (amd64 + arm64), tags it `3.1.0-ci.<run#>` (+ short SHA + moving `main`), pushes to **ACR**, bakes and seals | **Continuous** installs (dev/test) |
| **b** | **Push the annotated tag `v3.1.0`** on a promoted, sealed commit | `release.yml` retags that set `3.1.0` in ACR, mirrors it to GHCR, records `_releases/3.1.0`, publishes the GitHub Release | **Stable** installs (prod) |
| **c** | **Merge the bump** the lane opened (`PlatformVersion` → `3.2.0`) | continuous builds become `3.2.0-ci.<n>` | opens the next development line |

```bash
# (a) ship a continuous build — just merge; CI builds + pushes + seals the image set
git switch main && git pull

# (b) cut the official release — an immutable, ANNOTATED tag on a commit whose CD run sealed
#     (Doc/ReleaseNotes/3_1_0 must already be committed; the lane refuses a release without notes)
git tag -a v3.1.0 -m "MeshWeaver 3.1.0" <sha> && git push origin v3.1.0

# (c) open the next line — merge the pull request release.yml opened:
#     release: PlatformVersion 3.1.0 → 3.2.0
```

> **(a) can look shipped and produce no image.** CD reacts to *MeshWeaver Build and Test* completing
> on a `push` to main — a hand-kicked `workflow_dispatch` of that workflow turns main green and CD
> still skips. Verify the IMAGE (`.github/scripts/check-image-set.sh <short-sha>`), and re-drive CD
> with `gh workflow run main-cd.yml --ref main` if it is missing (it also self-heals HEAD on a
> hourly schedule).

(b) depends on (a): a tag can only name a set that a merge already shipped and sealed — that is the
whole point of a promotion. (c) follows (b) once per release line, opened by the lane and merged
by you. The version mechanics behind each step are in
[Release Process & Versioning](/Doc/Architecture/ReleaseProcess); §2 above covers the same cut from
the version-scheme angle.

---

## 9. See also

- [The Continuous Delivery Contract](/Doc/Architecture/ContinuousDeliveryContract) — what `main-cd.yml` guarantees about the set: all-or-nothing publication, the promote ordering, the reconciler, and verifying the image rather than the tick.
- [Release Process & Versioning](/Doc/Architecture/ReleaseProcess) — the version-number mechanics.
- [Deployment](/Doc/Architecture/Deployment) — the two deploy routes (AKS vs Container Apps).
- [DeploymentAKS](/Doc/Architecture/DeploymentAKS) · [LocalColimaMac](/Doc/Architecture/LocalColimaMac).
- [Request via stream.Update](/Doc/Architecture/RequestViaStreamUpdate) · [Controlled I/O pooling](/Doc/Architecture/ControlledIoPooling) — the patterns the poller is built on.
