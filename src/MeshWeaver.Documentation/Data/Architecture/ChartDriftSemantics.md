---
Name: Chart Drift — what a deploy actually does
Category: Architecture
Description: The measured semantics behind the Chart Drift report — which divergence classes are live-wrong, which are hygiene, and why a helm upgrade resolves neither
Icon: BranchCompare
---

# Chart Drift: what a deploy actually does

`Chart Drift` compares what the chart *describes* against what the cluster *runs*
(`deploy/aks/scripts/check-chart-drift.sh`, scheduled daily by `.github/workflows/chart-drift.yml`).
It has been red every day since it first completed on 2026-08-26, and its report was ranked by a
model of `helm upgrade` that **measurement does not support**. This page records the measurement, so
the ranking is not re-derived from intuition every time somebody re-reads the backlog.

## The claim that was wrong

The script's legend, and [MeshWeaver#2355](https://github.com/Systemorph/MeshWeaver/issues/2355)
which was triaged against it for a week, said:

| class | claimed consequence |
|---|---|
| `CLUSTER-ONLY` | *"the next `helm upgrade` **deletes** it"* |
| `DIFFERS` | *"the render would **overwrite** the running value"* |

That framing ranks the backlog by **what a deploy would destroy**. Every re-measurement of #2355
therefore led with "these are one deploy away from being gone", and the probes were called
*"a loaded gun"* and *"the highest-value thing on the list"*.

## What actually happens — measured 2026-09-03

A throwaway release was installed on a local k3s cluster, drifted in the four shapes the detector
reports, and then upgraded with a chart that **genuinely changed** — a new image, a new ConfigMap
value, and a new `periodSeconds` on the same probe object the drift had touched.

| drift applied to the live objects | after `helm upgrade` |
|---|---|
| live-only ConfigMap key | **survives** |
| live-only inline `env:` entry | **survives** |
| inline `env:` duplicating a chart ConfigMap key | **survives** |
| `initialDelaySeconds` added to a live probe | **survives** |

The chart's own three changes all landed — that is the positive control, without which the test
could not have failed. Run on **helm v3.21.1**, the exact binary `az aks command invoke` executes,
and repeated on helm v4.2.4 with an identical result.

The mechanism is Helm's three-way strategic merge: a patch removes only what Helm **previously
owned**. Anything applied out-of-band is in neither the old nor the new manifest, so no patch
touches it. This is also why `Systemorph/Memex#148` records section B as *"a helm upgrade PRESERVES
them"* — that inventory was right and the detector's legend was wrong.

> **A deploy does not clean up drift, and it does not destroy it either.** Both halves matter: the
> urgency the old legend created was false, *and* the reassurance "it will sort itself out on the
> next deploy" is equally false. Drift is only ever cleared deliberately.

## The hazard that is real, and was invisible

An inline `env:` entry **overrides `envFrom`**. So an inline entry whose name matches a key the
ConfigMap supplies does not merely duplicate it — it makes the ConfigMap value **dead** while it
still renders, still reads correctly in `values.*.yaml`, and still shows in `kubectl get configmap`.

This is not hypothetical on this fleet:

- **#2235** — the ConfigMap said `Hosting/PlatformBuilds`, the pod ran `Store/Payments`, every
  signal was green, and release broadcast 404'd for eleven days.
- **memex boot crash, 2026-08-30** — `EMAIL__*` patched onto the Deployment beside the chart's own
  `Email__*` ConfigMap keys. Linux env is case-**sensitive** so the pod carried both; .NET's
  configuration provider is case-**insensitive** and the last enumerated wins, so the effective
  value was a coin toss per pod start. It died on `EmailConfigurationGuard` with SIGABRT, and the
  restart on identical config came up fine.

The detector's header had described this override from the day it was written — but nothing in it
ever **checked** for it. Every such entry was reported as a plain `CLUSTER-ONLY`, sorted in among
dozens of harmless ones, which is precisely why #2355 read as an undifferentiated backlog nobody
owned.

## The classes now, worst first

| class | meaning | when it bites |
|---|---|---|
| `COLLIDES` | inline `env:` + a ConfigMap key differing **only in case** | **already**, at random, every pod start |
| `SHADOWS` | inline `env:` + the **same** ConfigMap key | **already** — the ConfigMap value is dead |
| `CLUSTER-ONLY` | live, in no committed source | on a rebuild or restore, and at every review |
| `CHART-ONLY` | rendered, never applied | nobody is getting it today |
| `DIFFERS` | both sides, values disagree | never resolves itself; needs a decision |

`SHADOWS` reports whether the two values **agree today**. Agreeing is not safe — it means the next
change to the chart will silently fail to take effect. Disagreeing means somebody is already
reading a setting no pod uses.

> Fixing a `SHADOWS` or `COLLIDES` takes **both** steps, in order: put the intended value in the
> chart, *then* delete the inline entry (`kubectl set env deploy/… KEY-`). Either step alone leaves
> the pod on the inline value — so a plan that reads "add these to the chart and the drift clears"
> leaves the cluster exactly where it was.

## The probes are inert, and the chart is authoritative

`DIFFERS livenessProbe` / `readinessProbe` on `memex` — live carries `initialDelaySeconds` 60 and
20, this chart carries none — was read three times as *"live is authoritative, add them to the
chart"*. It is the opposite:

- Kubernetes **suspends liveness and readiness entirely until the `startupProbe` succeeds**, and
  the live pod carries that startup probe byte-identically (`/health`, `periodSeconds: 5`,
  `failureThreshold: 60` — a 300 s window). A delay measured from container start can therefore
  never be the thing protecting a slow boot.
- `memex-cloud` runs this exact probe block with **no `initialDelaySeconds` at all**, and is
  healthy — the "loaded gun" already fired there, harmlessly.

So the divergence is real but **inert**, and adding `initialDelaySeconds` to the chart would
enshrine dead config. The reasoning now lives beside the probes in
`deploy/helm/templates/memex-portal/deployment.yaml`, so the next reader of the drift report does
not re-derive the wrong answer.

## Reading the report

Findings are printed worst-first and the summary counts each class. Rank the work that way:
`COLLIDES` and `SHADOWS` are live-wrong now; `CLUSTER-ONLY` is the migration tracked by
`Systemorph/Memex#148` (*nothing may live only on the cluster* — every value belongs on the
`Hosting/Deployment` record, rendered by the chart); `DIFFERS` is a decision, not a deploy.

See also [DeploymentAKS.md](/Doc/Architecture/DeploymentAKS) and
[Deployment.md](/Doc/Architecture/Deployment).
