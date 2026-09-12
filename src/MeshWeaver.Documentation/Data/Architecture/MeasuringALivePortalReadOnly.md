---
Name: Measuring a Live Portal Read-Only
Category: Architecture
Description: How to re-measure an ops issue on a live portal without mutating anything — /health first (public, unauthenticated, past RLS, and it samples a different replica each call), then the incident store, then the four break-glass cluster instruments; plus the traps that turn "I could not find it" into a false close.
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

## 🚨 Start with `/health` — the one instrument that is not break-glass

Everything further down this page needs `az aks command invoke` and is break-glass. **`/health` is
not.** It is a plain unauthenticated HTTP GET against the portal's public ingress, it needs no
credential, no cluster, no grant and no MCP session, and it is the only read on this page that a
person with a browser can take.

```bash
curl -s https://memex.meshweaver.cloud/api/version    # ALWAYS first — which portal am I reading?
curl -s https://memex.meshweaver.cloud/health
```

Measured 2026-09-11 06:49Z, with no credential of any kind:

| Portal | `/api/version` | `/health` |
|---|---|---|
| memex.meshweaver.cloud (the public portal / plugin registry — MCP server `memex`) | `3.0.0+6231c4da` | HTTP 200, 671 B |
| memex.systemorph.com (the CONTROL instance — MCP server `systemorph`) | `3.0.0+45306a33` | HTTP 200, 2570 B |

🚨 **Say which portal every number came from.** The two hold same-named nodes and answer differently;
reading the wrong one and concluding "the sync is frozen" cost two sessions an hour on 2026-09-10.
`/api/version` is the cheapest possible confirmation and it is one call.

### What makes it strictly better than a `search` sweep

The NodeType sweep (`search 'nodeType:NodeType content.compilationStatus:Error'`) runs **as you**, so
it is RLS-filtered: a type parked at `Error` in a partition you hold no grant on is silently not
counted, and `get` answers `Not found` for it — the same string an absent node gets. The sweep
returns a smaller number, never an error.

**`/health` is composed by the process, as the system.** It is past RLS by construction, so its
denominator is *this replica*, whole, whoever is reading. That is the property, and it is why the
entries below can answer a question the sweep cannot.

### 🚨 Repeated calls sample DIFFERENT replicas — that is a feature, and a trap

There is no session affinity on the health path. Ten consecutive calls to memex.meshweaver.cloud
(2026-09-11 06:49Z) returned **two distinct bodies**, 7 × 671 B and 3 × 873 B, and the two readings
were disjoint:

| | replica A | replica B |
|---|---|---|
| `content-types` | 3 types (`Edu/LearningJourney`, `Store/Tier`, `rsalzmann/GemschiGame`) | 7 types (`Store/Catalog`, `Store/Plugin` ×249, `AgenticPrimer/WishBook`, …) |
| `pending_module_activation` | 6 modules | 3 modules, **none of them the same six** |

So: **one call answers about ONE replica and you do not get to choose which.** A single clean read is
not a verdict about the deployment — call it until the body stops changing, and say how many samples
you took. Conversely, the variation is itself the measurement when the question is *"do my replicas
agree?"*, which is exactly the question a half-rolled deploy raises.

The per-replica form with no guesswork is the control instance's `Sample` action, which carries each
pod's whole `/health` body — see [Operating from the
Portal](/Doc/Architecture/OperatingFromThePortal).

### 🚨 Only non-Healthy entries print — EXCEPT the census entries, which always do

`WriteHealthWithDetail` puts the aggregate status on line one and then one line per check that is
**not** Healthy. A check that is Healthy prints nothing, so for most entries **an absent line does not
mean a clean one** — it is equally consistent with the check never having been registered in this
host at all. `nodetype_bake`, for instance, is registered only `if (gateBake)`: its silence on a
given portal says nothing until you have confirmed it is registered AND armed.

The exception is deliberate. A check tagged `ProbeEndpoints.CensusTag` prints its reading whatever
its status, because for a census the NUMBER is the publication and a silent clean reading would be
byte-identical on the wire to an unregistered check. Two entries carry it today, and both exist
because their verdict used to live only in a boot log that nobody here is authorised to read:

| Entry | Answers | Silent when… |
|---|---|---|
| `content-types` | which node types this replica cannot TYPE — their pages render empty | Healthy (not a census) |
| `pending_module_activation` | modules landed but not loaded in this process; a restart activates them | Healthy |
| `required_modules` | a required module that is store-delivered and not here | Healthy |
| `bundle_adoption` | prebuilt bundles the registry was meant to serve and this replica compiled instead | Healthy |
| `nodetype_bake` | the readiness gate's own phase — **only when `gateBake` is on** | Healthy **or not registered** |
| `bake-report` **(census)** | this replica's bake report: `total`/`baked`/`pending`, the per-state breakdown, the adoption-stamp count, and `ClassifiedFromLocalAdoption` — MeshWeaver#3703's verdict | **never** — Degraded when there is no report at all |
| `source-discovery` **(census)** | the batched source discovery's folded-change count and LARGEST inter-chunk gap against the completion window — MeshWeaver#3704's discriminator | **never** — Healthy and still printed when no pass ran |

🚨 **Read a census line's SENTENCE, not just its status.** `bake-report: Degraded — NO bake report on
this replica` and `bake-report: Degraded — the NodeType enumeration snapshot PREDATED …` are two
completely different findings wearing one word, and the first is an absence of measurement rather
than a fault. Likewise `source-discovery: Healthy — NO source-discovery pass recorded` means nothing
needed building on this replica; it is not a clean gap reading, because there was no gap to read.

### What STILL has no portal surface

Two of the three reads this page used to call unanswerable now have a control-instance action
(`Sample`, `Logs`). The third — *"can THIS replica load NodeType X"* — is now **partly** answered and
no longer entirely dark:

- **`content-types` names every type this replica could not type**, from the system's own denominator,
  which is the half a `search` sweep cannot reach.
- **`bake-report` says whether the replica's bake was even measured**, and `nodetype_bake` (where
  armed) names every non-`Ok` type.
- **What is still missing** is the per-TYPE, per-REPLICA answer for a type nothing has tried to read
  yet: `content-types` records a degradation only once a read degrades, so a type nobody has opened on
  this replica appears in neither list. For that, the boot log is still the only source.

## The incident store: `Admin/_LogIncident`, and the three ways to misread it

The log-watch pipeline folds every red burst into a `LogIncident` node
(mechanics: [Log-Watch Triage](/Doc/Architecture/LogWatchTriage)). For *measuring* purposes it is the
cheapest read on this page after `/health`, and all three of its traps produce a confident wrong
answer rather than an error.

**1 · It lives on ONE portal, and it is not the one you are investigating.** Measured 2026-09-11:
`namespace:Admin/_LogIncident scope:subtree` on **memex.systemorph.com** returns incidents and is
truncated at any limit; the identical query on **memex.meshweaver.cloud** returns **0**. The control
instance is where the store is, and it covers the other portals — the `nodetype_bake` incident
`0a24845deb486a56`, read on memex.systemorph.com, carries `"namespace": "memex-cloud"` and 400+
memex-cloud pod names. So *"I searched the portal that had the problem and found nothing"* is the
expected outcome of looking in the wrong place, not evidence.

**2 · It is invisible to an unscoped query.** Measured the same day on memex.systemorph.com:

```text
search 'nodeType:LogIncident'                              → count 0
search 'namespace:Admin/_LogIncident scope:subtree'        → truncated at the limit
```

Same portal, same moment, same nodes. The Admin partition is not in an unscoped query's reach, so
**the namespace is load-bearing** — exactly like the `content.` prefix on the NodeType sweep. A zero
from the first form is a statement about the query, not about the mesh.

**3 · 🚨 A frozen `occurrences` count can mean RENAMED, not FIXED.** The node id *is* the
fingerprint, and the fingerprint's third part is the masked **message** — the exception's, or the log
line's where there is no exception. So re-wording a message mints a **new fingerprint**, which means
a **new node**: the old incident stops accruing at the moment of the re-wording and looks cured,
while the identical fault carries on under an id nothing links to the old one. Before reading a flat
`lastSeen`/`occurrences` as a fix, check whether the message text moved in the same window — `git
log -S` on the literal is the cheapest form — and compare against the coverage rule at the top of
this page: an instrument that stopped being able to see the event did not observe its absence.

## Reaching the cluster at all

> 🚨 **Every read on this page is break-glass** (maintainer, 2026-09-08: no direct cluster access;
> operations and diagnostics go through the control instance's Hosting API). A read taken this way
> must be written up as break-glass, never as the procedure. What exists, what does not:
> [OperatingFromThePortal](/Doc/Architecture/OperatingFromThePortal).
>
> 🚨 **Two of the three questions this page used to name as unanswerable through the API now have an
> action** (Systemorph/MeshWeaver.Plugins#1521, live on the control instance and in daily use —
> measured 2026-09-10: 30 `Logs` runs, 24 of them that day). *Per-replica image / restarts / what
> each pod's own `/health` says* is `{ "requestedAction": "Sample" }`; *what did the process log at
> time T* is `{ "requestedAction": "Logs", "query": "…", "sinceMinutes": …, "limit": … }`, which
> takes the LogQL and lands the lines as `Hosting/LogEntry` nodes with the run's own `logQl`,
> `entryCount` and `truncated` beside them. **Ask the action first**; the `az aks command invoke`
> shapes below are the fallback for when the control plane itself cannot act, and the reference for
> what the action runs on your behalf. Only *"can THIS replica load NodeType X"* still has no direct
> answer. How to phrase the query, and why the log nodes already sitting on a portal are **not** a
> feed to search: [Log Entries Are a Query Result, Not a
> Feed](/Doc/Architecture/LogEntriesAreAQueryResult).

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

#### 🚨 `since=` is SILENTLY IGNORED — always write `start`/`end`

The Loki running here is **2.6.1** (built 2022-07-18), which predates `since` on `query_range`. It
does not reject the parameter; it **ignores** it and falls back to the endpoint's default window of
**one hour**. So `since=168h` does not ask for a week and get trimmed — it asks for nothing, and
gets the last hour:

| Query over `{namespace="memex-cloud"}` | Oldest line it can see |
|---|---|
| `since=168h`, `direction=forward&limit=1` | **1.0 h** ago |
| *no time parameters at all*, same otherwise | **1.0 h** ago — within 36 s of the line above |
| `start`/`end` as explicit ns, same otherwise | **167 h** ago — the whole window |

Rows one and two landing on the same window is the proof: the parameter is inert, not clipped.

**This is not a limit you can raise.** The cluster's limits are `max_query_lookback: 0s` (no
lookback cap at all) and `max_query_length: 30d1h`, against `retention_period: 31d` — nothing was
capping anything. A reading that blames a cap will send the next person to raise a bound that is
already unlimited.

The consequence is a **false zero that looks exactly like a real one**: you write `since=72h`, get
`0` lines, and report three days of silence that you never queried. This has already happened here
and nearly parked an issue on it.

**The control — run it every time, alongside the query, never instead of it:** ask the same
selector for its *oldest* visible line and check the age against the window you meant to search.

```bash
END=$(date -u +%s); START=$((END - 168*3600))          # the window you actually mean

az aks command invoke -g memex-aks-rg -n memexaks-cluster --command \
 "curl -sG 'http://loki.monitoring.svc.cluster.local:3100/loki/api/v1/query_range' \
    --data-urlencode 'query={namespace=\"memex-cloud\"}' \
    --data-urlencode 'start=${START}000000000' \
    --data-urlencode 'end=${END}000000000' \
    --data-urlencode 'direction=forward' \
    --data-urlencode 'limit=1'" -o tsv --query "logs"
```

`direction=forward` is what makes this a control: the default is `backward`, which returns the
*newest* entries, so it reports the freshness of the stream no matter how small the window really
was. Forward returns the oldest, which is the only end that can expose a truncated window. Bounds are
nanoseconds, hence the `000000000` suffix; RFC3339 works too, but mixing the two invites the same
silent-default failure this section is about.

A zero without that control beside it is not a measurement, and per the rule at the top of this page
it cannot license a "not happening" verdict.

#### 🚨 `count_over_time(…[R])` counts a window that starts R *before* your `start`

A range vector is evaluated at each step over `[t-R, t]`, so the **first** bucket of a
`start`/`end` query reaches `R` before `start`. With `[24h]` at `step=86400` over a 72 h window,
two of the four buckets lie almost entirely outside the window you asked for.

This is not academic: it over-counted one signal here **by 17×** — 124 summed across the buckets
against 7 lines actually inside the window, because the burst being counted sat just before
`start`. The two readings disagreeing is what exposed it; either alone looks authoritative.

**For "how many in window W", use an instant query with W as the range**, so there is exactly one
bucket and it is the window:

```bash
curl -sG ".../loki/api/v1/query" \
  --data-urlencode 'query=sum(count_over_time({namespace="memex-cloud"} |= "<phrase>" [72h]))' \
  --data-urlencode "time=$(date -u +%s)000000000"
```

Keep `query_range` with a range vector for the **shape** of a signal over time — a burst that ended
looks completely different from a steady drip, and that difference usually decides the severity. Just
do not read the sum of its buckets as a total.

#### 🚨 A rate needs a denominator, and `Information` is not emitted here

Before reporting *N failures*, check that the **success** line is observable at all. A success
logged at `LogInformation` against a category the deployment filters is simply absent, and the
failure count then has no denominator: N could be 5 % of traffic or 100 % of it, and the logs cannot
distinguish those.

The test is one query, and its answer is binary:

```
sum(count_over_time({namespace="…"} |= "<the success phrase>" [30d]))   -> empty
sum(count_over_time({namespace="…"} |= "<the failure phrase>" [30d]))   -> 154
```

Empty-against-nonzero **over the same chunks** means the success path is not being logged, not that
it never ran. Report the absolute count and say the rate is unavailable — do not silently upgrade
"154 failures" into "failing".

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
| Loki `since=` on 2.6.1 | "nothing in 168 h" | a false zero over the last **1 h** |
| `count_over_time(…[24h])` summed across buckets | "124 in the window" | 17× over-count; the burst was before `start` |
| Counting failures with no success line | "154 failures" | a count read as a rate, with no denominator |

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

- [Operating from the Portal](../OperatingFromThePortal) — the actions that replaced two of the
  break-glass reads, and the `Sample` that carries every replica's `/health` body at once.
- [Source Set Establishment](../SourceSetEstablishment) — what a short discovery pass may conclude,
  and the verdict `source-discovery` now publishes.
- [Deployment — AKS](../DeploymentAKS) — the deploy routes and the private-cluster rule.
- [Red-Log Watching & Ticketing](../LogWatchTriage) — how these issues get filed in the first place.
- [Reading CI Signals](../ReadingCiSignals) — the same "absent reads as satisfied" hazard in CI.
- [Orleans Stream Pub-Sub Durability](../OrleansStreamPubSubDurability) — a publish with no subscriber
  succeeds, so a cross-silo reply can vanish with nothing logged.
- [Node Type Compilation](../NodeTypeCompilation) — the retention cost model behind replica fattening.
