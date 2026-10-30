---
Name: Chart Ownership and the Runner Pool
Category: Architecture
Description: The chart lives in Systemorph/MeshWeaver.Deploy and the platform copy is frozen (deletions only); why it lived here, everything the relocation had to carry in one change set, why the cheap path-filter interim is unsafe here, and the self-hosted runner shape with an honest account of what it buys.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7l9-4 9 4v10l-9 4-9-4z"/><path d="M12 11v10"/><path d="M3 7l9 4 9-4"/><circle cx="12" cy="7" r="1.6"/></svg>
---

Two questions were raised during the 2026-08-31 CI overhaul and tracked as issues: *"why is the
chart gate on core?"*, and *"can we mount a drive where git is checked out"*. Both are real, both
are larger than they look, and neither should have stayed in an issue thread. This page is the
state of record for both, measured against the code on 2026-09-01.

## The chart lives in MeshWeaver.Deploy

The relocation this page planned has happened (policy `chart-lives-in-deploy`). Systemorph/MeshWeaver.Deploy,
a public repository, was seeded with the chart material (Deploy#1) and is now its only home. The
sections below record why the chart was here and what a relocation had to carry; read them as
history.

Both copies stayed editable after the seed, and both moved. Two pipelines then baked
`hosting-operator:main`, one from each copy, so the moving tag flapped between two charts. The
platform's copy is therefore FROZEN: `scripts/check-chart-freeze.py`, run by the
`Chart frozen (lives in MeshWeaver.Deploy)` job and hung off the required check, refuses any pull
request that adds, modifies, retypes or renames into these paths:

- `deploy/helm/`, `deploy/helm-db/`, `deploy/aks/values.aks.yaml`, `deploy/aks/manifests/`,
  `deploy/aks/scripts/`, `deploy/homebrew/`;
- `.github/workflows/chart-gate.yml`, `.github/workflows/homebrew.yml`,
  `scripts/check-config-key-coverage.py`, `.github/scripts/test-bundle-registry.py`.

A **deletion** is allowed, because that is how the copy leaves (#7127). The build-host deployment
followed it (MeshWeaver.Deploy#5) and is frozen the same way: `deploy/build-host/`,
`.github/scripts/build-run.py`, `.github/scripts/build-host-announcement.py`,
`.github/build-hosts.json` and `.github/workflows/build-host-stable.yml`. Deploy builds the
`mw-build-host` runner image from its own tree, fetching the platform's helpers by the set's commit.
A chart change belongs in MeshWeaver.Deploy. The gate's message says so, and its
self-test proves it refuses a modification and an addition and lets a pure deletion through.

Two shapes no well-meant edit reaches are refused as well (#7391). A non-deletion **at a directory
above a frozen path** — move `deploy/aks` aside and add the symlink `deploy/aks -> ../editable-aks`
— lists every frozen child as a deletion and one addition that no frozen prefix matches, yet
re-opens the whole subtree for editing through the link; so an addition, modification or retype at
any ancestor of a frozen path is a violation, while deleting there stays allowed. And a saved
`--diff-file` listing is read **strictly**: the whole status token must be one git prints (`Dgarbage`
used to read as a deletion). Only `C`, `R` and `M` carry a score, and a score never exceeds 100, so a
scored deletion such as `D100` is refused rather than exempted. And a record must carry the number of path fields its status needs, none
empty (`R100` with no destination used to count as nothing). An unreadable record stops the read and
is itself a violation.

## The chart lives here; the application it deploys does not

`deploy/helm/` is in the platform repo, so `Chart Gate` and `Chart Drift` correctly run here — **a
gate lives beside its subject**, and this one exists because of a measured shape-impossibility
incident: the chart described KEDA-min-2, PDB-2 and `replicas: 1` simultaneously for a month, and
one pod meant multi-minute 503s.

The architectural drift is equally real. The portal hosts the chart deploys have moved to the
plugins repository; deployments are node-type-driven through the hosting operator; environment
folders live in the private deployments repository. So the platform ships and gates a chart for an
application it no longer contains.

**What that costs today, measured:** `Chart Gate` triggers on `pull_request` with **no `paths:`
filter**, so *every* core PR renders every values combination and asserts every config key is read
by a template — including PRs that cannot touch the chart. `Chart Drift` is `schedule`-only and
costs nothing per PR.

## 🚨 The relocation checklist — everything that must move in ONE change set

A chart is not a directory; it is a directory plus everyone who resolves a path into it. Measured
references, each of which breaks silently if it is left behind:

| Consumer | What it holds |
|---|---|
| `main-cd.yml` | renders `WebhookInbox__Targets__N` from `deploy/helm/templates/memex-portal/config.yaml` (the `FrameworkBroadcast__Subscribers__N` slots were retired 2026-09-03 — the subscriber set is the `Hosting/Deployment` records') |
| `homebrew.yml` | **path filters on `deploy/helm/**`** — a chart change is what triggers a tap rebuild |
| `deploy/aks/scripts/check-chart-invariants.sh`, `check-values-are-read.py` | the gate's own implementation |
| Systemorph/Memex `deployments/aks/example/deploy.sh`, `secretproviderclass.yaml` (moved there from `deploy/aks/envs/example/`) | first-time environment setup |
| Systemorph/Memex `infra/platform/modules/portal-identity.bicep` (moved there from `deploy/aks/infra/`) | infrastructure that must agree with the chart's service account |
| the hosting operator | `CHART="${HOSTING_CHART:-/opt/hosting/chart}"` — the operator carries its **own baked copy** at a pinned path inside its image |
| the self-updater | documented against `deploy/helm/templates/memex-portal/` for the SA, Role and RoleBinding |

🚨 **The bake gate is live and `helm upgrade` REVERTS.** A relocation that moves the chart without
moving the gate's arming leaves a window in which an upgrade silently undoes it.

⚠️ **A drift already exists in this set, independent of any move**, and is worth fixing whether or
not the chart relocates: the self-updater and its RBAC target `memex-migration-deployment`, **a
workload the chart does not render** — `helm template deploy/helm` emits a `Job`, not a Deployment.
That is the #1788 shape (a command aimed at a Deployment that does not exist either errors or keeps
a cluster-only orphan alive that re-runs the migration forever).

## 🚨 The cheap interim is NOT safe here — do not path-filter the chart jobs

The obvious economy is to filter the PR-triggered chart jobs to `deploy/helm/**`, on the reasoning
that a chart which did not change cannot newly break.

**Do not do this while `Chart Gate` is a required context.** GitHub paints a skipped job the same
colour as a passed one, and this repository has already established that a `SKIPPED` or *absent*
required context **counts as SATISFIED**. So a path filter converts "the chart gate did not need to
run" and "the chart gate did not run" into the same green tick — and the day someone changes a
values file through a path the filter does not match, the gate is silently absent rather than red.

The reasoning that makes a filter *seem* safe — "it cannot newly break" — is also not quite true:
the gate asserts a **cross-file** property (every config key a values file sets is read by a
template). A change to a *template* under a different path can therefore break an *unchanged*
values file, which is precisely the class the filter would stop watching.

If the per-PR cost must come down, the sound options are to make the gate cheaper, or to move it
with its subject — not to make its absence indistinguishable from its success.

## 🚨 Moving the chart into a PRIVATE repository: what it converts, measured

The core-slimming carve moved the Aspire integration, the example environment and the AKS platform
Bicep out of this repository ([Carving Projects Out Of Core](../CarvingProjectsOutOfCore)). The chart,
`deploy/aks/manifests`, `deploy/aks/scripts` and `values.aks.yaml` were deliberately **not** moved with
them. Measured against `Systemorph/MeshWeaver@b85e51b4c4`, `Systemorph/MeshWeaver.Plugins@cecd1fc25` and
`Systemorph/Memex@e5fdf2f`, these consumers read them **without a credential**, because this
repository is public and both candidate homes are private:

| Consumer | What it reads, and how |
|---|---|
| the operator image (`deploy/aks/operator/Dockerfile`, build context `deploy/`) | `COPY helm/`, `COPY helm-db/` and the four drift scripts. *The operator image version IS the chart version it deploys*, so the chart has to sit in the same tree as the operator at the same commit. |
| Memex `node-repo-aks-ops.yml` | sparse-checks out `deploy/aks/operator/bin`, `deploy/aks/manifests` and `deploy/helm` from this repository at the record's operator pin, with no token. `hosting-manifest-apply` refuses a manifest that is not beside `bin/`, and `hosting-chart-drift` finds its scripts at `bin/../../scripts`. |
| Memex `config-key-coverage.yml` | a **required** context of Memex's ruleset. It checks this repository out for `deploy/helm` and `scripts/check-config-key-coverage.py` (its comment reads "Public repo — no token"). The `vendored-charts` job does the same. |
| `deploy/homebrew` (memex-local) | vendors `deploy/helm/*` into the PUBLIC tap tarball (Systemorph/homebrew-memex). `homebrew.yml` triggers on `deploy/helm/**`. |
| the guard tests here that read the chart (`MeshWeaver.Documentation.Test`, `Memex.Portal.Shared.Test`, `MeshWeaver.Hosting.Test`, and in MeshWeaver.Plugins the Deployment record's suites in `MeshWeaver.SelfUpdate.Aks.Test`) | read the chart to keep **core code** consistent with it: probe paths against `ProbeEndpoints`, config keys against their readers, the drain deadline against `ServiceDefaults`. |
| `chart-gate.yml`, `.github/scripts/test-bundle-registry.py` | render and execute the chart (the second one together with this repository's `push-bundle-publication.sh`). |

Moving the chart into MeshWeaver.Plugins (beside the operator, which is moving there) or into Memex
turns every row into a credentialed cross-repository read. That means a read token provisioned in
**both** secret stores for each private lane that reads it, Memex's required one included. It also
means re-homing the public homebrew tarball and porting every chart-reading guard into a test project that can see
both trees. Choosing among the shapes below is an owner decision, not an implementation detail:

1. **Keep it here** as the public deployment contract. Nothing changes, but every chart edit is a
   core change.
2. **A separate PUBLIC repository** for the deployment mechanism (chart, manifests, AKS scripts).
   Every reader stays tokenless and core stops changing for chart edits. The guards that compare
   the chart with core code have to move to a repository that can see both, or run against a
   checkout of it.
3. **A private repository**, with the provisioning listed above.

## The self-hosted runner pool

GitHub-hosted runners cannot mount persistent drives, which is what the original request asked for.
The approximation already in place is per-digest blob caching; what remains per job is a
once-per-digest fill and a checkout of a few seconds.

> 🚨 **The first half of this shipped on 2026-09-07, and it did NOT use a dedicated node pool.**
> The measured headroom on the existing pool made the standing cost avoidable; isolation comes from
> a negative priority class, a `ResourceQuota` and an explicit reserve instead. What was deployed,
> the caps and the arithmetic behind them are the state of record in
> [Self-hosted CI runners on AKS](../SelfHostedRunners). The rest of the shape below — the git
> mirror, the warm layer store, the opt-in labels — is still outstanding.

The full shape, recorded so it can be picked up rather than re-derived:

- **ARC on the existing cluster**, on a dedicated node pool so CI cannot starve the portal.
- **A persistent volume carrying a bare git mirror per repo.** Each job runs a delta `fetch`, then
  `git worktree add` for the job and **deletes the worktree at job end** — the mirror stays.
- **A warm layer store on the node** (host containerd plus a pre-pull DaemonSet for the current
  portal and tester digests), so even the once-per-digest pull disappears.
- **Runner labels** so lanes opt in per repo, with hosted runners as the fallback.

Also agreed for the lane, and independent of the pool: **after the global build, run tests** — one
job builds the whole graph in one workspace, and test jobs consume its artifacts.

**Constraints that shape it:** the cluster is private, so `kubectl` reaches it only through
`az aks command invoke`; environment configuration lives in the private deployments repository; and
cluster changes route through the hosting operator rather than direct access.

**The honest trade.** This converts a variable per-job cost into a fixed standing one: a node pool
that exists whether or not CI is running, plus a controller to keep alive, plus the failure modes of
a stateful mirror (a corrupted or diverged mirror fails every job at once, where a fresh checkout
fails none). It is worth doing when CI volume makes the per-job seconds dominate — and it is worth
saying out loud that until then, the caching already in place is most of the benefit for none of the
standing cost.

## Outcome: the chart moved to a public repository of its own

The chart, the database chart, `deploy/aks/manifests`, `deploy/aks/scripts`, `values.aks.yaml`,
`scripts/check-config-key-coverage.py`, the bundle-registry harness, `chart-gate.yml` and memex-local
(`deploy/homebrew`, `homebrew.yml`) moved to the PUBLIC
[`Systemorph/MeshWeaver.Deploy`](https://github.com/Systemorph/MeshWeaver.Deploy) at the same paths —
shape 2 above, a separate public repository — so every reader stayed
credential-free:

- the hosting operator's image (MeshWeaver.Plugins) checks it out at a declared ref and tags the image
  `<plugins sha7>-<core sha7>-<deploy sha7>`, so the estate's aks-ops lane reads the chart, the
  manifests and the drift scripts at the commit the image was built from;
- the estate's `config-key-coverage` and `helm-release` read it as they read this repository before;
- the chart-reading guard tests (and memex-local's own) moved with it into
  `test/MeshWeaver.Deploy.ChartGuards`, built against a checkout of this repository;
- this repository's registry retention gate names the chart's new home (`chartRepository`) and leaves
  its re-derivation to MeshWeaver.Deploy's `Registry retention` job, which reads this repository's
  record with no credential.

Nothing estate-specific was in the moved material — this repository is public — so the estate's
private repository received nothing: its real values were already there.

## See also

- [The Continuous Delivery Contract](../ContinuousDeliveryContract) — what a published image set guarantees
- [Deployment — AKS](../DeploymentAKS) — read end-to-end before any AKS deploy
- [Module Build Architecture](../ModuleBuildArchitecture) — the unified build shape every repo runs
- [Self-hosted CI runners on AKS](../SelfHostedRunners) — what actually runs, its caps, and how to scale or remove it
- [Reading CI Signals](../ReadingCiSignals) — why a skipped required context reads as satisfied
