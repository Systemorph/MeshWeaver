# Applying is not rolling out

A deploy has two distinct moments, and conflating them destroys correct work:

```
  helm upgrade  ─────▶  APPLIED           the API server holds the new manifest
                                          (a new helm revision exists)
                          │
                          │  pods restart, probes run, a startup gate may take
                          │  a very long time on purpose
                          ▼
                        ROLLED OUT        updated == ready == replicas, at the new generation
```

**Only the first belongs to helm.** The second is observed, against a budget that comes from the
record being deployed — because how long it legitimately takes is a property of the instance, and
helm has never read the record.

## What conflating them cost

`hosting-deploy` ran `helm upgrade --install --atomic --wait --timeout 15m`. Measured 2026-09-09 on
memex ([#3782]):

| | |
|---|---|
| 02:35:20Z | `Deployments/memex-reconcile-20260909-final` starts; adoption 0/19, writable-kinds preflight passed |
| 02:50:11Z | read-only audit: **revision 44, 20 chart-managed objects** — the record's full render. The upgrade had LANDED |
| 02:50:20Z | helm's fifteen minutes expire. `--atomic` rolls back |
| 02:51:57Z | audit: **revision 45, 19 objects** = revision 43's manifest |

Nothing was wrong with revision 44. It was inside its own startup gate, which is what a gated
two-replica roll looks like while a generation recompiles — and the record budgets **10800 seconds**
for exactly that, in `startupProbe.budgetSeconds`. The portal answered 200 throughout. A correct
upgrade was reverted by a timer that had never read the record it was applying.

## Why a bigger timeout is not the fix

1. **A wait inside helm cannot outlast the process running it.** The operator Job's
   `activeDeadlineSeconds` (3600) caps every in-Job wait, so no `--timeout` can express a budget the
   record is allowed to set above it. Raising the number moves the cliff; the behaviour *at* the
   cliff — reverting work that succeeded — is unchanged.
2. **`--atomic` turns "slow" into "reverted".** Slow and broken are indistinguishable to a timer, and
   they deserve opposite treatments. Only something watching the rollout can tell them apart,
   because only it can see whether progress is being made.
3. **It was not preventing what it claimed to.** The argument for `--atomic` was that it avoids "a
   half-created release the next attempt must `helm uninstall`". A `failed` release is upgradable —
   helm rolls forward over it. The states that genuinely block the next upgrade are `pending-*`, and
   `hosting-deploy` refuses those **by name, before helm runs**, handing the revision choice to a
   person. That is the better guard, and it was already there.

## The shape now

```
hosting-deploy   helm upgrade --install          (no --atomic, no --wait, no --timeout)
                 helm status → ::hosting:: helm_revision=<n>
                          │
                          ▼
caller           observe generation <n> until updated == ready == replicas,
                 with a budget from the record; report Done only then
```

Two things are load-bearing on the boundary:

- **The applied revision is reported, read back from `helm status` rather than counted.** `--install`
  may have created revision 1 or upgraded to revision 45, and a caller that guessed would watch the
  wrong generation. If the revision cannot be read, the script **refuses** — a release the caller has
  no handle on would silently degrade "observed" back to "assumed".
- **Success from `hosting-deploy` means applied, not rolled out**, and it says so in the log. Never
  report a rollout no one saw complete.

This is the decision the configuration repository's helm-release lane had already made deliberately
(Memex#188): helm applies, a separate step observes with its own budget. Two implementations of one
operation held opposite answers; this is that disagreement resolved in favour of the one that did not
lose a good upgrade.

## The caller's half

Sizing the observation is the other side of the pair, in `InstanceActionPlan` (MeshWeaver.Plugins).
Every "Wait for rollout" step there is `kubectl rollout status --timeout=600s`, which would have
failed the same rollout one step later — a *failed report* rather than a *reverted deploy*, so the
core half alone is already a strict improvement, but it is not the whole fix. What it needs:

- a budget from the record — `min(startupProbe.budgetSeconds × replicas, Job deadline − elapsed)`;
- when the budget exceeds what one Job may run, end the plan with the rollout **observed, not
  complete**, and let the control plane re-observe until it finishes, stamping Done only then.

## The general rule

> A deploy step that reports success must have *seen* the thing it reports. When the budget for
> seeing it belongs to the record, the wait cannot live inside a command that never reads the record
> — and it must never be allowed to undo what it merely failed to watch.

Related: [Deployment (AKS)](../DeploymentAKS) · [Instance lifecycle — the state of record](../InstanceLifecycleStateOfRecord) ·
[Probe Semantics](../ProbeSemantics) · [Chart Drift](../ChartDriftSemantics)

[#3782]: https://github.com/Systemorph/MeshWeaver/issues/3782
