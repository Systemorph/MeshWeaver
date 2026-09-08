---
Name: Self-hosted CI runners on AKS
Category: Architecture
Description: Actions Runner Controller on the shared AKS cluster — the guardrails that keep CI from ever starving the portals, the headroom arithmetic behind the caps, and how to scale, verify and tear it down.
Icon: Server
---

# Self-hosted CI runners on AKS

The fleet's CI queues behind an org-wide ceiling of 60 concurrent GitHub-hosted jobs. One
MeshWeaver.Plugins pull request is **105 jobs / ~350 job-minutes**, so a handful of open PRs
saturates the org and everything else waits — measured 2026-09-07: 61 hosted jobs running, 18
Plugins runs queued behind them, and $1,987 of a $3,000 monthly Actions budget consumed on day 7
with `prevent_further_usage: true` armed.

Self-hosted minutes do not count against that budget, and the AKS cluster that hosts the portals has
real spare capacity. This page is how runners live there **without ever becoming a risk to the
portals**, which is the only hard constraint on the whole design.

> **The portals and the runners share nodes.** Every decision below exists to make one sentence
> true: *under pressure the scheduler removes a runner, never a portal.* If you change a cap, a
> request, or the priority class, re-derive the arithmetic in
> ["The arithmetic"](#the-arithmetic) first — the numbers are measurements, not preferences.

## What is deployed

| | |
|---|---|
| Product | `gha-runner-scale-set-controller` — the **current** ARC, not the legacy `summerwind` operator |
| Version | chart **0.14.2** (`ghcr.io/actions/actions-runner-controller-charts/gha-runner-scale-set-controller`) |
| Controller namespace | `arc-systems` |
| Runner namespace | `arc-runners` |
| Node placement | `nodeSelector: workload=silos` — the user pool, never the 4-vCPU system pool |
| Priority | `arc-runner-low` (value **-10**, `preemptionPolicy: Never`) |
| RBAC reach | `flags.watchSingleNamespace: arc-runners` — the controller takes **namespaced Roles only**, no ClusterRole, and cannot see the portal namespaces |

Install (private cluster, so through the API-server-side runner):

```bash
az aks command invoke -g <aks-resource-group> -n <aks-cluster> \
  --file controller-values.yaml \
  --command "helm upgrade --install arc \
    oci://ghcr.io/actions/actions-runner-controller-charts/gha-runner-scale-set-controller \
    --version 0.14.2 --namespace arc-systems -f controller-values.yaml --wait --timeout 5m"
```

The guardrails (`PriorityClass`, both namespaces, the `ResourceQuota` and the `LimitRange`) are a
plain manifest applied **before** the controller — they are the safety mechanism, so they must exist
before anything that could consume capacity does. Both files live in the private deploy repo beside
the other environment material; nothing here is environment-specific.

## Why the shared pool and not a dedicated one

[Chart Ownership and the Runner Pool](/Doc/Architecture/ChartOwnershipAndRunnerPool) recorded the
intended shape in 2026-09 as *"ARC on the existing cluster, on a dedicated node pool so CI cannot
starve the portal"*, and named the honest trade: a dedicated pool converts a variable per-job cost
into a **standing** one.

What actually shipped uses the **existing silos pool**, for one reason that page could not yet
weigh: the headroom was measured. The pool carries 20,703m of unrequested CPU and 120 GiB of
unrequested memory, and its real utilisation is 2–14%. A dedicated pool would spend money to buy
isolation that the priority class, the quota and the reserve already provide arithmetically — and
it would spend it whether or not CI is running.

That page's reasoning is not wrong, it is the **next** step, and it is written up as such under
["How to scale it"](#how-to-scale-it). The trigger to take it is steady rather than bursty CI load,
or a reserve calculation that stops closing. Everything else that page records for the lane — a
persistent git mirror, a warm layer store, opt-in runner labels with hosted runners as the fallback
— is unchanged by this and still outstanding.

## Three independent brakes, not one

`maxRunners` alone is a setting — an upgrade, a merge or a typo can move it and nothing notices.
The design therefore stacks three mechanisms that fail in different ways:

1. **`maxRunners: 8` on the scale set** — the normal operating limit. Application-level.
2. **A `ResourceQuota` on `arc-runners`** — `pods: 9`, `requests.cpu: 4500m`,
   `requests.memory: 36Gi`. Enforced by the **API server**: no ARC version, value override or
   hand-applied pod can exceed it. It sits *one runner above* `maxRunners` so normal operation never
   touches it and a runaway still stops.
3. **A `LimitRange`** giving every container a default request and limit, and a hard `max` of
   4 CPU / 8Gi. An unbounded pod is the one shape that could starve a portal *regardless* of its
   priority, and this makes one unschedulable.

## The priority class, and why -10 exactly

Every production pod on this cluster runs at the **default priority 0 with no
`priorityClassName`** — measured, both portal Deployments plus `portal-next`, the website and
whisper. So any negative value ranks runners below all of them.

```yaml
value: -10
preemptionPolicy: Never
```

- **Negative** ⇒ when a portal pod cannot schedule, the scheduler preempts a *runner* to make room.
  That is the required ordering, and it is what makes a KEDA scale-out or a rolling update safe even
  if runners have taken the slack.
- **`preemptionPolicy: Never`** ⇒ a runner can never evict anything itself. The ordering holds in
  both directions rather than depending on which pod the scheduler happens to consider first.
- **-10 and not lower** is deliberate. The cluster autoscaler treats pods *below*
  `--expendable-pods-priority-cutoff` (default **-10**) as expendable: it will not grow the pool for
  them **and it will delete a node out from under them**. At exactly -10 a runner is not expendable,
  so a pending runner may still grow the silos pool **within its existing max of 4 nodes**, and a
  running job is not killed by a scale-down. Going to -11 would make runners free but would trade
  away both of those.

**Cost consequence, stated plainly:** because runners are not expendable they *can* pull the silos
pool from 3 nodes to its configured maximum of 4. That is capacity the pool is already allowed to
use, and one node-hour is far cheaper than the hosted minutes it replaces — but it is not free, and
it is the one way this design spends money on Azure rather than saving it on Actions.

## The arithmetic

All figures are **requests**, because the scheduler places pods on requests, not on usage. Measured
on the silos pool (3 × `Standard_D16s_v5`) on 2026-09-07.

**Capacity.** Allocatable per node 15,740m CPU / 59,144 Mi ⇒ pool **47,220m CPU / 177,432 Mi**.
Already requested **26,517m CPU (56.2%) / 54,466 Mi (30.7%)** ⇒ free **20,703m CPU / 122,966 Mi**.

> 🚨 **Do not size this from `kubectl top`.** Actual usage on the same pool read 2% / 14% / 3% CPU,
> which invites a cap five times too large. The portals *request* 4 CPU + 8Gi per replica and use a
> fraction of it; that reservation is what a new pod has to fit around.

**What must stay free for the portals** — the two ways production grows without anyone deploying:

| reserve | why | CPU | memory |
|---|---|---|---|
| KEDA scale-out, +2 replicas | the cloud portal is a `ScaledObject` with **min 2 / max 8**, sitting at 4 replicas and **74% of an 80% memory target** — it is one nudge from scaling | 8,000m | 16,384 Mi |
| Rolling-update surge, +2 pods | two portal Deployments at `maxUnavailable: 0` / `maxSurge: 1` | 8,000m | 16,384 Mi |
| **total reserved** | | **16,000m** | **32,768 Mi** |

**What is left for runners:** 20,703 − 16,000 = **4,703m CPU**, and 122,966 − 32,768 =
**90,198 Mi**. CPU is the binding constraint, so it sets the cap.

**Per-runner request: 500m CPU / 4Gi. Limit: 2 CPU / 6Gi.**

- **Memory is requested honestly** (4Gi) because it is incompressible — under-requesting it is how a
  node reaches memory pressure and the kubelet starts killing things.
- **CPU is requested modestly** (500m) because a CI job is bursty and the pool's CPU is genuinely
  idle. Under contention the kernel shares CPU in proportion to requests, so a portal at 4,000m gets
  **8× the share** of a runner at 500m — CPU contention degrades the runner first, by construction.

**Cap:** 4,703m ÷ 500m = 9.4 ⇒ **`maxRunners: 8`**, with the quota one above at 9.

Resulting pool state at full runner load, on 3 nodes:

| | requested | of allocatable | free | reserve needed |
|---|---|---|---|---|
| CPU | 30,817m | 65.3% | 16,403m | 16,000m ✅ |
| memory | 87,746 Mi | 49.5% | 89,686 Mi | 32,768 Mi ✅ |

Both reserves are satisfied **without relying on preemption or on a 4th node**. Preemption and the
autoscaler are the second and third lines of defence, not the plan.

**Worth doing?** Yes. The `Module bundles` matrix is 34 jobs / ~152 job-minutes. At 8 concurrent
runners that is ~19 minutes of wall clock **starting immediately**, against up to an hour spent
queued today — and it removes those 152 minutes per run from a budget that was 66% consumed on
day 7.

## How to scale it

In order of preference. **Re-derive the table above before any of them.**

1. **Raise `maxRunners`** — only if the reserve arithmetic still holds. The `ResourceQuota` must be
   raised in the same change or it becomes the real cap and runners sit `Pending` for no visible
   reason.
2. **Raise the silos pool maximum** (`az aks nodepool update --max-count`). Each node adds
   15,740m / 59,144 Mi of headroom, so the reserve is satisfied with room to spare and the cap moves
   with it. Costs a node only while the autoscaler holds it.
3. **A dedicated node pool** — `az aks nodepool add` with `--enable-cluster-autoscaler
   --min-count 0`, a taint, and a matching toleration + `nodeSelector` on the scale set. This is the
   right answer once CI load is steady rather than bursty: runners then contend with **nothing**,
   the priority class stops being load-bearing, and cost is strictly proportional to CI usage. It is
   deliberately *not* the starting point, because it spends money to solve a problem the measured
   headroom does not yet have.

Never raise a cap to make a queue shorter without re-reading the reserve. The reserve is what keeps
a portal scale-out from having to preempt anything.

## Moving a job family onto it

**One family at a time, behind a variable, hosted as the fallback.** The first candidate is the
plugins repo's `Module bundles` matrix: **34 jobs / ~152 of the ~350 job-minutes** per run, all of
them independent, none of them holding a credential that a runner would newly see.

The mechanism is **cross-repo**, which is the part worth knowing before starting:

- `modules-floor` and `modules-rest` in the plugins repo do **not** carry a `runs-on`. They are
  `uses:` calls into **core's** reusable `node-repo-module-pack.yml`, pinned by full sha.
- That reusable hard-codes `runs-on: ubuntu-latest` in **six** jobs — `select`, `prepare`,
  `build-workspace`, `pack`, `tests`, `verify`. `pack` and `tests` are the matrix ones and carry
  almost all of the minutes.

So the change is: add a `runs-on` **input with a default of `ubuntu-latest`** to the reusable, use
it on the matrix jobs, then pass it from the caller as
`runs-on: ${{ vars.MW_RUNNER_LABEL || 'ubuntu-latest' }}`. Reverting is then one variable, with no
workflow edit at all.

🚨 **The default is load-bearing.** A *required* input added to a reusable workflow is a silent
startup failure in every caller that has not been updated in the same breath — no checks appear at
all, which reads exactly like CI being slow rather than like a break.

🚨 **Do not point a workflow at the label before a job has provably run on a runner.** A `runs-on`
label nothing serves does not fail — it queues, until the job hits its 45-minute cap. That is
strictly worse than the hosted queue it was meant to escape.

Bumping the caller's pinned sha is a deliberate act in its own PR, per that workflow's own rule
about a reusable that receives secrets.

## Telling a self-hosted job from a hosted one

- **In the run log**, the first line of the job's *Set up job* group names the runner:
  `Runner name: '<scale-set>-...'` and `Runner group: Default` for self-hosted, against
  `Runner Image: ubuntu-...` / `Runner ImageOS` on a hosted one. A hosted job always prints a
  `Runner Image` block; a self-hosted job never does.
- **In the API**, `GET /repos/{o}/{r}/actions/runs/{id}/jobs` gives each job a `labels` array and a
  `runner_name`. A job that ran on ARC carries the scale-set name as its label.
- **From the cluster**, `kubectl -n arc-runners get pods` shows one ephemeral pod per running job,
  named after the scale set. Zero pods and a queued job means the listener is not connected — check
  `kubectl -n arc-systems logs deploy/arc-gha-rs-controller`.
- **From GitHub**, `gh api /orgs/<org>/actions/runners` lists every registered runner with its
  `status` (`online` / `offline`) and `busy` flag.

🚨 **A registered runner is not a working runner.** The failure that looks most like success here is
a scale set that registers, shows `online`, and never picks up a job — because its labels do not
match any `runs-on`. Verify with a run whose job log names the runner, never with the runner list
alone.

## Tearing it down

Removing it is two commands and leaves nothing behind:

```bash
az aks command invoke -g <aks-resource-group> -n <aks-cluster> --command "\
  helm uninstall <scale-set-release> -n arc-runners; \
  helm uninstall arc -n arc-systems; \
  kubectl delete ns arc-runners arc-systems; \
  kubectl delete priorityclass arc-runner-low"
```

Uninstall the **scale set before the controller** — the controller is what finalizes the scale-set
CRs, and removing it first leaves them stuck deleting. Nothing in the portal namespaces references
any of these objects, so a teardown cannot affect a portal.

Any workflow pointing at the self-hosted label must go back to `ubuntu-latest` **first**, or its
jobs queue forever against a label nothing serves.

## The credential

ARC authenticates as a **GitHub App** — app id, installation id, private key — never a stored PAT
(see the fleet rule on minting App tokens). The App needs, at organization scope,
**Self-hosted runners: Read and write** (`organization_self_hosted_runners`), plus repository
**Metadata: Read**.

🚨 **The org's existing App cannot be reused, and this was measured rather than assumed.** Its
declared permission set is `contents:write, emails:write, metadata:read, pull_requests:write` — no
runner permission at all — and an installation token minted from it answers **HTTP 403 `Resource not
accessible by integration`** on both `GET /orgs/{org}/actions/runners` and
`POST /orgs/{org}/actions/runners/registration-token`.

Reusing it would also be the wrong shape even if it worked: that App holds `contents:write` across
every repository in the fleet, and adding runner administration to it widens the blast radius of a
single key. **A dedicated App whose only permission is runner administration is the correct
credential** — it can be revoked without touching content sync, and it grants nothing else if it
leaks.

A GitHub App's permissions are **not writable through any API**, and App creation goes through the
browser-based manifest flow. Both are maintainer actions; no agent can mint this credential for
itself, which is the same reason the fleet's PAT-elimination work landed on Apps in the first place.

## Related

- [Deployment — AKS](/Doc/Architecture/DeploymentAKS) — the cluster, and why every `kubectl` goes
  through `az aks command invoke`.
- [Chart Ownership and the Runner Pool](/Doc/Architecture/ChartOwnershipAndRunnerPool) — the
  lane shape this implements the first half of, and the parts still outstanding.
- [Reading CI Signals](/Doc/Architecture/ReadingCiSignals) — why a skipped or absent check reads as
  satisfied, which is the failure mode this page's "registered but never picks up a job" mirrors.
