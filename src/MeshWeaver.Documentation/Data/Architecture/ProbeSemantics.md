---
Name: Probe semantics — what each Kubernetes probe may answer
Category: Architecture
Description: Readiness, liveness and startup ask three different questions with three different remedies, so they get three different paths and three different health-check tags. Why a shared path made a progress-aware /alive evict a GC-bound replica 60 s before it restarted it, why the readiness predicate is an allow-list rather than a deny-list, and the two guards that hold the chart and the code to each other.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12h4l3 8 4-16 3 8h4"/></svg>
---

# Probe semantics — what each Kubernetes probe may answer

A probe is not a health report. It is a **request for a specific remedy**, and Kubernetes applies
that remedy without asking anything else. Three probes, three remedies:

| probe | question | remedy Kubernetes applies | who pays if it is wrong |
|---|---|---|---|
| `startupProbe` | *is everything I need up yet?* | keep waiting; suspend the other two | the roll stalls |
| `readinessProbe` | *can I take a request?* | **take me out of the Service — give my traffic to my siblings** | **the siblings** |
| `livenessProbe` | *am I making progress?* | **restart me** | this pod only |

The asymmetry in the last column is the whole design. Liveness is a claim about *one* pod and it is
self-limiting: restarting a pegged replica removes load from the system. Readiness is a claim about
*the siblings* — it asserts that they have the spare capacity to absorb this pod's traffic — and it
has a feedback loop: every pod that leaves makes the next one likelier to leave.

So the two questions must be answerable **independently**, and that is not a matter of taste:

> **Liveness and readiness cannot be given different semantics while they share a path.**

## What happened when they shared one (#3330)

The chart pointed both post-startup probes at `/alive`. That was deliberate and, for a while,
correct: `/alive` filters the health-check registry down to checks tagged `live`, **nothing carried
that tag**, and a `MapHealthChecks` whose predicate matches nothing is *vacuously healthy*. `/alive`
therefore meant "this process can still execute a delegate" — a fine readiness answer, and the fix
that ended the 2026-07-21 death spiral, when readiness had been on the heavy `/health`.

The blindness in that arrangement is the subject of
[Why a GC-bound pod stays in rotation](../WhyAGcBoundPodStaysInRotation): a replica pegged in GC
answers `/alive` perfectly while serving nothing. `MeshWeaver.Plugins#1234` closed it by registering
`ProcessProgressHealthCheck` on the `live` tag (merged 2026-09-03). That change was correct **in its
own repository**. What nobody joined up was that the tag it chose was, in this repository's chart,
also the readiness predicate.

Readiness then inherited a progress-aware verdict — and readiness trips first:

```
readiness   10 s × 3  =  30 s   → leaves the Service
liveness    15 s × 6  =  90 s   → restarts
```

A GC-bound replica was therefore **evicted a full minute before it was repaired**, and for that
minute its traffic went to siblings sitting at the same point on the same curve. Replicas *converge*
on the GC's hard limit with age rather than diverging — measured 2026-09-04 in namespace `memex`,
two 28-hour replicas at 9936Mi and 9409Mi, a ratio of **1.06**. So the condition arrives nearly
simultaneously across a namespace, and the pod that inherits the evicted traffic is the one closest
to tipping itself. One sick replica became a cascade: the 2026-07-21 death spiral, rebuilt out of the
containment that was supposed to prevent it.

The failure mode fires *during exactly the incident the containment was added for*. That is what
made it worth a gate rather than a note.

🚨 **The chart had already written the rule down, on the readiness probe, in these words:** *"a
readiness check that fails under load removes the pod, and the survivors inherit its traffic — the
2026-07-21 death-spiral"*. It was correct, it was two lines above the probe it governed, and it did
not stop the change — because the change was made in a different repository, where nobody was
reading this chart. **A rule stated in a comment is not a gate.**

## The shape that is now enforced

Three paths, three predicates. They are declared once, in
`memex/aspire/Memex.Portal.ServiceDefaults/ProbeEndpoints.cs`, and consumed by name:

| path | predicate | tagged today |
|---|---|---|
| `/health` | every registered check | the database, the mesh, the NodeType bake gate |
| `/alive` | checks tagged `live` | `self`, plus `ProcessProgressHealthCheck` from the host |
| `/ready` | checks tagged `ready` | `self`, and deliberately nothing else |

### The readiness predicate is an ALLOW-list, and that is the actual fix

The cheaper-looking answer is a deny-list — readiness runs *everything except* the `live` checks.
It is wrong, and wrong in the more expensive direction: the checks that carry **no** tag are the
heavy ones `/health` is made of, so a deny-list sweeps the database and the mesh into readiness and
rebuilds the 2026-07-21 spiral in its original form.

More importantly, a deny-list repeats the root cause rather than removing it. The defect was never a
number; it was **a check registered in another repository joining readiness's predicate by carrying
a tag chosen for liveness**. Under an allow-list a host that wants to leave the Service has to say
so in exactly those words:

- tag a check **`live`** when failing it should make Kubernetes **restart this pod**;
- tag a check **`ready`** when failing it should make Kubernetes **hand this pod's traffic to its
  siblings** — a statement about the siblings' spare capacity, not only about this pod.

`NodeTypeBakeHealthCheck` states the same rule from the other side and is deliberately tagged
neither: a long bake, a missing module and an unreachable registry are all wrong answers to a
restart *and* to an eviction. They belong on `/health`, where the startup probe reads them.

### `/ready` runs a check rather than nothing

A readiness endpoint whose predicate matched nothing would answer 200 forever, which is the
behaviour we want today and a **detector that cannot fail** tomorrow — precisely how `/alive` stayed
blind through the 2026-08-25 incident. So the trivial process-up check carries both tags, and
`ProbeSeparationTest` fails a readiness endpoint that cannot be made to answer 503.

## What is *not* changed, and why

**Liveness stays on `/alive`.** It is the Aspire and Kubernetes convention for the liveness path, and
"am I making progress" is the liveness question; moving it to a new path would invert the convention
and would need a matching change in MeshWeaver.Plugins for no semantic gain.

**The thresholds stay 30 s / 90 s.** Once the probes ask different questions the ordering is no
longer a hazard: readiness answers "yes" throughout a GC episode, and liveness restarts the replica
at 90 s with no eviction before it. Retuning the numbers was the third option on #3330 and it treats
the arithmetic as the defect; the defect was the shared predicate.

**`ProcessProgressHealthCheck` is untouched.** It was right. It just needed the readiness probe to
stop listening.

## The guards

Two, because they can fail for different reasons and neither can see what the other sees.

| guard | where | what it proves |
|---|---|---|
| `ProbeSemanticsGuard` | `test/MeshWeaver.Documentation.Test` | resolves each chart probe path through `ProbeEndpoints.cs` to the health-check **tag** it filters on, and requires readiness and liveness to land on different tags — so two different paths wired to one predicate fails too |
| `ProbeSeparationTest` | `test/Memex.Portal.Shared.Test` | drives the paths the **chart** names over real HTTP through the real `MapDefaultEndpoints`, with a `live`-tagged check reporting Unhealthy: liveness must answer 503 *and* readiness 200 |
| invariant 9 | `deploy/aks/scripts/check-chart-invariants.py` | the same separation on the **rendered** manifest, per values combination — where an overlay could re-merge what the template separates |

Both C# guards run inside `Consolidate test results`, the one required check. Both read the paths out
of the chart rather than restating them: a restatement would agree with itself while the deployment
probed something else.

## Rolling it out: the image first, the chart second

A probe-path change is one of the few chart edits with an **order** attached, and the wrong order is
an outage rather than a warning.

`/ready` is mapped by the portal **image** (`MapDefaultEndpoints`). So a `helm upgrade` that carries
this chart onto pods running an image from before 2026-09-05 points the readiness probe at a path
that answers **404** — which the kubelet reads as a failing probe, on **every replica at once**. The
reverse is inert: a new image with the old chart simply serves a `/ready` nobody probes.

The fleet's usual sequence is already the safe one and it is not an accident: CD rolls the image
continuously with `set image`, while a `helm upgrade` is always a deliberate human action (the
self-updater cannot run one — it is helm-revision-scoped). The case to be careful about is a
namespace **pinned** to an older image and then given a chart upgrade. Confirm before upgrading it:

```bash
kubectl -n <ns> exec deploy/memex-portal-deployment -- \
  curl -sf -o /dev/null -w '%{http_code}\n' http://127.0.0.1:8080/ready
# 200 ⇒ the running image maps it; anything else ⇒ roll the image first
```

Until a namespace has had that `helm upgrade`, `check-chart-drift.sh` will legitimately report
`DIFFERS readinessProbe` against it — the chart is the authoritative side and the drift is the work
not yet done, not a defect in either.

## Adding a health check

1. Decide the remedy first, not the check. *If this fails, should Kubernetes restart the pod, take
   it out of rotation, or neither?*
2. **Neither** — the overwhelmingly common answer — means no tag. It lands on `/health`, which the
   startup probe reads, and it gates the roll without touching a running pod.
3. **Restart** means `ProbeEndpoints.LiveTag`. The bar is a condition a restart actually fixes.
4. **Out of rotation** means `ProbeEndpoints.ReadyTag`, and it needs an argument about the siblings:
   on a fleet whose replicas converge, "this pod is degraded" is usually true of all of them at once,
   and evicting one is then strictly worse than keeping it.

## See also

- [Why a GC-bound pod stays in rotation](../WhyAGcBoundPodStaysInRotation) — the measurement behind
  the convergence figure, and why the GC defends a pod instead of the kubelet restarting it.
- [Deployment (AKS)](../DeploymentAKS) — the chart, the rollout, and how to read a live deployment.
