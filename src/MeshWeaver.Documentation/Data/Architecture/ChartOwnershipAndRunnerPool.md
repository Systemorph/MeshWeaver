---
Name: Chart Ownership and the Runner Pool
Category: Architecture
Description: Why the helm chart still lives in the platform repo although the application it deploys does not, everything a relocation must carry in one change set, why the cheap path-filter interim is unsafe here, and the self-hosted runner shape with an honest account of what it buys.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7l9-4 9 4v10l-9 4-9-4z"/><path d="M12 11v10"/><path d="M3 7l9 4 9-4"/><circle cx="12" cy="7" r="1.6"/></svg>
---

Two questions were raised during the 2026-08-31 CI overhaul and tracked as issues: *"why is the
chart gate on core?"*, and *"can we mount a drive where git is checked out"*. Both are real, both
are larger than they look, and neither should have stayed in an issue thread. This page is the
state of record for both, measured against the code on 2026-09-01.

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
| `deploy/aks/envs/example/deploy.sh`, `secretproviderclass.yaml` | first-time environment setup |
| `deploy/aks/infra/modules/portal-identity.bicep` | infrastructure that must agree with the chart's service account |
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

## The self-hosted runner pool

GitHub-hosted runners cannot mount persistent drives, which is what the original request asked for.
The approximation already in place is per-digest blob caching; what remains per job is a
once-per-digest fill and a checkout of a few seconds.

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

## See also

- [The Continuous Delivery Contract](../ContinuousDeliveryContract) — what a published image set guarantees
- [Deployment — AKS](../DeploymentAKS) — read end-to-end before any AKS deploy
- [Module Build Architecture](../ModuleBuildArchitecture) — the unified build shape every repo runs
- [Reading CI Signals](../ReadingCiSignals) — why a skipped required context reads as satisfied
