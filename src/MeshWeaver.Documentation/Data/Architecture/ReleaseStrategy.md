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
| **a** | **Build is green with warnings-as-errors** | The target: the `Build` job builds the restored tree with `-warnaserror`, so a compiler warning can't merge. **Staged** — the flag is currently commented in `dotnet-test.yml` because the codebase still emits ~130 pre-existing compiler warnings (broken XML-doc crefs CS1574/CS0419, platform-compat CA1416, a few CS4014/nullable). Flip it on (append `-warnaserror`) after that burn-down. Doc-completeness warnings (CS1591/CS1573/CS1712) are suppressed centrally (`Directory.Build.props` `NoWarn`); NuGet restore advisories (NU19xx) surface in the separate restore step, not this gate. |
| **b** | **Tests are green** | The sharded suite + the consolidated *Test Results* check (`dotnet-test.yml`). |
| **c** | **Reviewed (AI _or_ human)** | A required approving review (branch protection). We don't care whether the reviewer is a person or an AI. `.github/CODEOWNERS` requests an owner. |
| **d** | **Review comments dealt with** | "Require conversation resolution before merging" (branch protection). We expect at least one comment or code change tied to the review. |

> **Branch-protection settings to configure once** (GitHub → Settings → Branches → `main`; `gh` can't
> set these): require the *Build* and *Test Results* status checks; require ≥1 approving review;
> require review from Code Owners; require conversation resolution before merging.

---

## 2. Versions: current-build vs official

The one number is `PlatformVersion` in `Directory.Build.props` (today `3.0.0`). Every build derives
its version from it ([details](/Doc/Architecture/ReleaseProcess)):

- **Current build (continuous):** `3.0.0-ci.<n>` — the default. `<n>` is the **GitHub Actions run
  number** (monotonic), so newer builds always sort higher. 🔴 This monotonicity is load-bearing: the
  self-updater picks the *newest* version, and the old seconds-since-midnight build number reset at
  midnight (a morning build would sort below the prior evening's). Do not revert it.
- **Official release:** clean `3.0.0` — built with `-p:PublicRelease=true`, fired by pushing a
  `v3.0.0` tag.

### Cutting an official release and starting the next line

This is the only time you edit `Directory.Build.props`:

1. **Cut the official `3.0.0`:** push tag `v3.0.0`. `release-images.yml` + `release-packages.yml`
   build the clean `3.0.0` artifacts and **push them to ACR** (and GHCR/NuGet). Stable installs pick
   it up.
2. **Start `3.1`:** bump `PlatformVersion` to `3.1.0` in `Directory.Build.props`. Continuous builds
   are now `3.1.0-ci.<n>`. (Because `3.1.0 > 3.0.0`, the new line dominates the comparison — a
   `3.1.0-ci.1` is newer than any `3.0.0-ci.<n>`.)

---

## 3. CI produces ALL images, tagged by version, on ACR

Both channels publish the full image set to **`meshweaver.azurecr.io`**, tagged by the **version
string** — that tag is what each install compares.

| Channel | Trigger | Version baked + image tag | Workflow |
|---|---|---|---|
| **Continuous** | green merge to `main` | `3.0.0-ci.<run#>` (+ short SHA + moving `main`) | `main-cd.yml` |
| **Official** | push `v*.*.*` tag | clean `3.0.0` (+ `latest`) — GHCR **and** mirrored into ACR via `az acr import` | `release-images.yml` |

So "build produces all images, with or without a build number," and a running install only has to
list ACR tags and pick the best per its policy. (`main-cd.yml` still rolls the environments once as
the bootstrap; steady-state updates are the self-updater below.)

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

## 8. See also

- [Release Process & Versioning](/Doc/Architecture/ReleaseProcess) — the version-number mechanics.
- [Deployment](/Doc/Architecture/Deployment) — the two deploy routes (AKS vs Container Apps).
- [DeploymentAKS](/Doc/Architecture/DeploymentAKS) · [LocalColimaMac](/Doc/Architecture/LocalColimaMac).
- [Request via stream.Update](/Doc/Architecture/RequestViaStreamUpdate) · [Controlled I/O pooling](/Doc/Architecture/ControlledIoPooling) — the patterns the poller is built on.
