---
Name: Measuring a Live Portal Read-Only
Category: Architecture
Description: How to re-measure an ops issue against the private AKS cluster without mutating anything — the four read-only instruments, the retention check that has to come first, and the four traps that turn "I could not find it" into a false close.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="11" cy="11" r="7"/><path d="m21 21-4.3-4.3"/><path d="M8 11h6"/><path d="M11 8v6"/></svg>
---

An ops issue filed off a log incident decays fast. The pod is gone, the node is gone, the
deployment has rolled a dozen times, and the tempting move is to look, find nothing, and close it.

**That move is the single most expensive error in this codebase**, because *"I could not find it"*
and *"it is not happening"* are different claims and only one of them justifies a close. This page
is the method that keeps them apart: which read-only instrument answers which question, what to
establish *before* you are allowed to read an absence as evidence, and the traps that have already
produced false verdicts here.

Nothing on this page mutates anything. Every command is a read.

## The rule that governs everything else

> **Establish that the instrument COULD have seen the event, before you report that it did not.**

An absent log line means nothing until you know the log store's retention covers the timestamp. An
empty alert list means nothing until you know the alert rule is loaded. A green probe means nothing
until you know the probe reaches the replica that was sick. Each of those has produced a wrong
answer on this cluster, and each is one command away from being settled.

The positive form: **every "not happening" verdict must cite a coverage fact** — retention ≥ the
age of the event, the rule group exists, N samples across M replicas. A verdict with no coverage
fact is a guess.

## Reaching the cluster at all

The AKS cluster is **private**. `kubectl` reaches it only through `az aks command invoke`, which
runs your command in a pod inside the cluster — which is also what makes it the right place to
query in-cluster services directly:

```bash
az aks command invoke -g memex-aks-rg -n memexaks-cluster \
  --command "kubectl get pods -n memex-cloud -o wide" -o tsv --query "logs"
```

Two properties of that pod matter. It sits **on the cluster network**, so `curl` against a
`ClusterIP` service works and no port-forward is needed. And it is a minimal image — **there is no
`python3` in it**, so parse JSON on your own machine by piping the `--query "logs"` output out,
never inside `--command`.

## The four instruments

| Question | Instrument | Retention |
|---|---|---|
| *What did the process log at time T?* | **Loki**, `loki.monitoring.svc.cluster.local:3100` | **31 d** — check it, don't assume |
| *What did resource usage look like at time T?* | **Prometheus**, `loki-prometheus-server.monitoring.svc.cluster.local` | scrape-dependent |
| *Did the infrastructure change at time T?* | **Azure activity log** — on the **node** resource group | 90 d |
| *What is true right now?* | `kubectl get/describe/top`, an HTTP probe | now only |

### Loki — the log seam

Always check retention **first**, and quote it in the finding:

```bash
az aks command invoke -g memex-aks-rg -n memexaks-cluster \
  --command 'curl -s http://loki.monitoring.svc.cluster.local:3100/config | grep -A2 retention_period'
```

Then query. Counting is usually more informative than reading — a rate over time separates *"it
happened once"* from *"it is a standing storm"*, which is exactly the judgement an issue needs:

```bash
az aks command invoke -g memex-aks-rg -n memexaks-cluster --command \
 'curl -sG "http://loki.monitoring.svc.cluster.local:3100/loki/api/v1/query_range" \
   --data-urlencode "query=sum by (pod) (count_over_time({namespace=\"memex-cloud\"} |= \"<phrase>\" [1h]))" \
   --data-urlencode "start=2026-08-31T00:00:00Z" \
   --data-urlencode "end=2026-09-01T19:45:00Z" \
   --data-urlencode "step=3600"' -o tsv --query "logs"
```

`sum by (pod)` is not a detail. Several defects here are **per-pod and persistent**, not per-request
random, and an aggregate hides that completely — one replica at a steady 40/h next to five at zero
is a different bug from six replicas at 7/h.

### Prometheus — the metric seam, and its rules

Three endpoints, and the last two are the ones people forget:

```bash
.../api/v1/query?query=<expr>   # what a value is now
.../api/v1/alerts               # what is firing
.../api/v1/rules                # what could ever fire
```

**`/api/v1/rules` returning zero groups means no alert can ever fire, no matter what the metrics
do.** An empty `/api/v1/alerts` then says nothing at all about system health. See the worked example
below — this exact reading was needed to tell a healthy system from an unarmed detector.

### The Azure activity log — on the NODE resource group

Node-pool scaling, VMSS updates and evictions are recorded against the **node** resource group
(`MC_<rg>_<cluster>_<region>`), *not* the cluster's own resource group. Querying the cluster RG
shows only control-plane calls such as `runCommand`, and reads as "no infrastructure churn" when
the node pool was being rebuilt the whole time:

```bash
az aks show -g memex-aks-rg -n memexaks-cluster --query nodeResourceGroup -o tsv
az monitor activity-log list --resource-group MC_memex-aks-rg_memexaks-cluster_swedencentral \
  --start-time 2026-08-31T14:00:00Z --end-time 2026-08-31T17:00:00Z \
  --query "[].{time:eventTimestamp,op:operationName.localizedValue,status:status.value}"
```

## 🚨 Where the portals actually run

Verify this before attributing any measurement, because it has already gone stale once:

| Hostname | Served by | Notes |
|---|---|---|
| `memex.meshweaver.cloud` | **AKS**, namespace `memex-cloud` | |
| `memex.systemorph.com` | **AKS**, namespace `memex` | *not* Container Apps |
| ACA `memex-prod` (rg `prod-memex`) | nothing | `configuration.ingress: null` — no FQDN, no traffic |

Both hostnames resolve to the AKS ingress IP. The Container Apps deployment still exists, still runs
and still burns resources, but it serves no request — so a remediation applied there (a revision
restart, say) cannot affect either portal, and an observability gap measured there is a gap on an
app carrying no traffic. Confirm with two commands rather than memory:

```bash
dig +short memex.systemorph.com
az network public-ip list -g MC_memex-aks-rg_memexaks-cluster_swedencentral --query "[].ipAddress"
```

The mapping is settled by `kubectl get ingress -A`, which names the host per namespace.

## Worked examples

Four findings from one sweep, each showing a different half of the method.

### 1 · A single framework fault was a symptom — count the warnings on the TARGET

An `Orleans.Runtime.GrainDirectory.ClientDirectory` publish timed out to one silo, once. The
tempting close ("deploy churn") had already been falsified: the nearest merge's CD run started
eleven minutes *after* the failure.

What settled it was counting long-turn warnings on the **silo that failed to answer** — not on the
one that reported the error:

```
"took elapsed time" per 10 min, target pod:  9 · 14 · 22 · 73 · 196 · 439
```

A 50× monotonic ramp peaking at the exact minute of the timeout. The reporter's error was a symptom
of a **scheduler-stalled silo**, and the stall was concurrent with a VMSS node-pool operation found
in the node-RG activity log. Then the coverage fact that permits the close: **zero recurrences in
the following 28 h, against a log store with 31 d retention** — a measured absence, not a missing
one.

> **Generalise:** an error names the component that *noticed*. Measure the component that *failed*.

### 2 · A merged alert that never fires

An issue's detection remedy was recorded as shipped — the alert exists in the repo, in a values
file, in a merged PR. On the cluster:

```
/api/v1/rules   → rule groups: 0
configmap loki-prometheus-server → alerting_rules.yml: {}
helm list -A → loki  monitoring  revision 1  updated 2026-05-31
```

The observability values had never been applied; the release had not moved since May. Meanwhile the
alert's own condition was **true at that moment** (working-set ratio 7.2× against a 3× threshold,
peak 13.8 GB against an 8 GB threshold). Every repo-side check said "shipped"; nothing was armed.

> **Generalise:** a guard is only shipped when the *runtime* says it is loaded. `git grep` proves
> authorship, not deployment. This is the same class as
> [Reading CI Signals](../ReadingCiSignals) — a skipped gate and a passed gate look identical.

### 3 · A green probe that measured the wrong thing

Three URLs from a flapping-503 report answered `206` twelve times out of twelve. That is *not* a
fix: the deployment had been pinned to one replica, and the reported defect was **per-replica**. The
coin toss was removed, not the bug. The honest reading needed a second instrument — the log count
for the underlying timeout, which showed the fault still occurring the same day at a reduced rate.

> **Generalise:** when a defect is per-replica, a probe through a load balancer is a *sample*, and
> its power depends on replica count. State the sample size, or measure the log instead.

### 4 · A creation-time log beat every after-the-fact read

A credential that 503'd forever was analysed through the read seams — point read, query index,
version store — and the seams disagreed. The creation window was still inside Loki's retention, and
the log settled in one query what the seams could not:

```
15:44:49  Node created at <partition>/MeshWeaverInstance/<id> by system-security
15:44:49  Node created at Admin/_PluginGrant/<id> by system-security
16:04:14  Response did not arrive on time in '00:00:30' … sys.svc.dir.mem … IDhtGrainDirectory
```

Both writes landed; what failed nineteen minutes later was the **per-node hub's activation through
the Orleans grain directory** — the same subsystem, the same window and the same silo family as
example 1. Three issues filed as unrelated were one degradation.

> **Generalise:** if the event is inside retention, read the log *at the moment of the write*
> before theorising from the state left behind. Related: [Durable But Unreadable](../DurableButUnreadable).

## The traps, in one table

| Trap | Looks like | Costs you |
|---|---|---|
| Reading absence without a retention check | "no such log line" | a false close |
| `/api/v1/alerts` empty, rules never loaded | "nothing is wrong" | an unarmed detector, indefinitely |
| Activity log on the cluster RG | "no infrastructure churn" | the node-pool operation that caused it |
| Aggregating a per-pod defect | "7/h across the fleet" | the one broken replica |
| Probing a load-balanced host once | "it is fixed" | a per-replica fault, still live |
| Attributing to a stale topology | "restarted the app" | a remediation on something serving no traffic |
| `python3` inside `--command` | `not found` | a silently empty result |

## What a verdict must contain

Whatever the outcome, an honest re-measurement states three things:

1. **What was measured** — the query, the window, the sample size.
2. **The coverage fact** — retention, rule presence, replica count. This is what licenses reading an
   absence as evidence.
3. **What would change the verdict** — a concrete, falsifiable condition, not "needs more
   investigation".

If retention, access or a missing instrument prevents a verdict, the verdict is **keep the issue
open and name the blocker**. Closing on absence of evidence is the failure this whole page exists to
prevent.

## Related

- [Deployment — AKS](../DeploymentAKS) — the deploy routes and the private-cluster rule.
- [Red-Log Watching & Ticketing](../LogWatchTriage) — how these issues get filed in the first place.
- [Reading CI Signals](../ReadingCiSignals) — the same "absent reads as satisfied" hazard in CI.
- [Orleans Stream Pub-Sub Durability](../OrleansStreamPubSubDurability) — a publish with no subscriber
  succeeds, so a cross-silo reply can vanish with nothing logged.
- [Node Type Compilation](../NodeTypeCompilation) — the retention cost model behind replica fattening.
