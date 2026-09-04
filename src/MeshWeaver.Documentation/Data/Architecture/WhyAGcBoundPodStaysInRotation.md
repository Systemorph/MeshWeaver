---
Name: Why a GC-bound pod stays in rotation
Category: Architecture
Description: The .NET GC's heap hard limit sits below the container limit and covers only the managed heap, so a portal short of memory gets defended by the GC rather than restarted by the kubelet — measured across seven replicas over 28 hours, zero OOMKills and a 66% GC pause share held for over two hours while every probe answered Healthy. The measured mechanism, why convergence did not prevent the recurrence, and the detector that goes blind exactly when the incident becomes fleet-wide.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 20h18"/><path d="M5 20V9l7-5 7 5v11"/><path d="M3 12h18" stroke-dasharray="3 2"/><path d="M9 20v-5h6v5"/></svg>
---

# Why a GC-bound pod stays in rotation

A portal replica can reach a state where it burns 4–5 cores, answers requests in minutes rather
than milliseconds, and stays in the Service endpoints for hours. Every signal says it is fine: no
OOMKill, no probe failure, no restart, no alert. A human reading `kubectl top` is the only thing
that has ever caught it.

That is not bad luck, and it is not merely a missing alert. **It is structural, and this page names
the structure**: the containment that would clear such a pod in one second never fires, and the
containment that does fire is the one that costs 66 % of wall time and lasts for hours.

## The ceiling under the ceiling

The portal container carries `limits.memory: 16Gi` on both production namespaces. Nothing sets
`DOTNET_GCHeapHardLimit` or `DOTNET_GCHeapHardLimitPercent` — not the inline `env:` on the
Deployment, not `memex-portal-config`, not any envFrom Secret (audited by key name, 2026-09-04).

So .NET's default applies: **when a process runs under a container memory limit, the GC's heap hard
limit is 75 % of that limit.** There are therefore two ceilings, they are not the same number, and —
this is the part that matters — **they do not cover the same memory:**

| ceiling | value at `limits.memory: 16Gi` | covers | who enforces it | what happens at it |
|---|---|---|---|---|
| **GC heap hard limit** | **12 GiB** | the managed heap only | the .NET runtime, in-process | continuous blocking gen2; allocation slows to a crawl; the process survives |
| container memory limit | 16 GiB | everything the process maps | the kubelet | `OOMKilled`, container restarts, pod rejoins healthy |

The 4 GiB between them is not slack. It is the margin the default *reserves for native memory* —
loaded assemblies, thread stacks, Kestrel buffers, the runtime itself. On the degraded replica that
margin is already spent:

```
working set 14 096 MiB  −  managed 10 065 MiB  =  ~4 031 MiB native
```

The native side has consumed essentially the whole reserve while the managed heap still has ~2 GiB
of its own budget left to grow into. So the runtime keeps expanding the heap toward *its* ceiling
out of headroom the container no longer has, and the two limits are reached at roughly the same
moment rather than 4 GiB apart.

**What is measured is which one wins.** Across seven replicas over 28 hours: **zero OOMKills.** The
only two restarts were `exitCode 255`, reason `Unknown`, at one shared timestamp — a node-level
event, not memory. What happens instead is the GC doing exactly what it was designed to do: it
refuses to exceed its hard limit and pays for that with full collections, back to back, for hours.

**So in practice the GC converts a memory shortage into a CPU shortage**, and that is a much worse
trade than it sounds. Memory exhaustion is a condition Kubernetes knows how to fix, in one second,
by restarting the container. CPU starvation inside a process that still answers TCP is a condition
Kubernetes has no opinion about at all.

> The inference to be careful with: this is *not* "OOMKill is unreachable by construction" — with
> ~4 GiB of native memory the container limit is reachable in principle. It is the stronger
> empirical claim that the GC gets there first and holds the line, so the restart that would clear
> the pod in one second has never once fired.

### Measured

`memex-cloud`, 2026-09-04, replica `5nqbz`, 28 h old, from the runtime's own watchdog line:

```
.NET Runtime Platform stalled for 00:00:03.37. Total GC Pause duration during that period:
00:00:03.33. We are now using a total of 10065MB memory.
Collection counts per generation: 0: 19719, 1: 7027, 2: 609
```

10 065 MiB managed against a 12 GiB hard limit is **82 % of the ceiling the managed heap actually
has**, while `container_memory_working_set_bytes` reads 14 096 Mi — 86 % of a limit that has never
been reached on any replica. The 2026-09-03 incident is the same pod one step further along:
10.1 GB managed, gen2 7 957, GC pause share 0.55–0.72 (mean **0.66**) sustained for **2 h 15**,
`/alive` answering `Healthy` throughout.

A pause share of 0.66 means two thirds of wall-clock time is spent in the garbage collector. That
is a pod which is *up* by every definition Kubernetes uses and *down* by every definition a user
uses.

## The heap growth is retention, not a working set

It would be comfortable to read a 10 GB heap as a big-but-healthy cache that the GC simply has not
bothered to trim. The pod's own telemetry refuses that reading.

Same replica, 2026-09-04, one row per watchdog emission:

| time (UTC) | managed | gen2 |
|---|---|---|
| 06:55 | 6 856 MB | 342 |
| 09:47 | 7 981 MB | 410 |
| 12:48 | 8 660 MB | 454 |
| 15:41 | 8 744 MB | 537 |
| 18:36 | 9 371 MB | 575 |
| 21:51 | **10 065 MB** | **609** |

**+3.2 GB in 14.9 h — about 215 MB/h — across 267 full (gen2) collections, and the floor after each
collection never returns to any earlier level.** A cache the GC has not got round to trimming does
not survive 267 gen2 collections. This is retention: something roots the objects, and the collector
is doing its job and finding nothing to free.

The growth also does not track current work, which rules out "the load is simply heavy":

| replica | age | working set | log lines / 30 min |
|---|---|---|---|
| `fxbhd` | 28 h | 11 443 Mi | 512 (and zero of every hot family) |
| `zn7nf` | 15.7 h | 4 134 Mi | 2 476 — the **most** of any replica |

The near-idle pod holds nearly three times the memory of the busiest one. Working set tracks
**uptime**, not throughput. Whatever is retained is retained across the quiet hours too.

## What roots it, and what does not

Two log families settle this, and both were counted over a pod's entire 28-hour life
(186 137 lines), with `info:`-level lines confirmed to be shipping (2 791 of them) so that a zero
is a real zero and not a log-level artefact:

```
Stale-build offer:                    0
Stale-build convergence:              0
EnrichWithNodeType: self-heal recompile #   0
BatchBake:                            0
KEEPING its load context              0
NOT unloading collectible context     0
Grain … deactivating: reason=         0
```

Read those together and they say two things.

**First: no build was ever superseded.** Not one publication, bake, recompile or stale-build offer
in 28 hours. `Modules:AutoRecycleOnStaleBuild` is armed on both namespaces and has never fired,
because nothing has ever been stale. Convergence is the correct fix for a state that did not arise.

**Second: nothing is ever released.** `deactivating: reason=` is **0** across five sampled replicas
and 514 687 log lines. Not one hub or grain has been deactivated since boot on any of them. That
matters because an instance hub takes a **lifetime lease** on the `AssemblyLoadContext` of the
NodeType whose types it runs, and `NodeAssemblyLoadContext.Dispose` defers `Unload()` while any
lease is held. A hub that never deactivates never releases its lease, so the context it pins is
loaded for the life of the process — along with everything the hub itself holds.

The compile load that mints those contexts is large, and it is a *coverage* failure rather than a
publication wave. The two oldest replicas logged this at boot:

```
DynamicTypePreWarmer: ADOPT-ONLY boot complete in 00:02:17 — adopted=9 uncovered=368 of 377
dynamic NodeType(s); nothing was compiled at boot
368 dynamic NodeType(s) were NOT covered by the prebuilt bundles and will each pay a Roslyn
compile on FIRST ACCESS
```

| replica | booted | adopted / total | compile activities since boot |
|---|---|---|---|
| `fxbhd` | 09-03 17:49 | **8 / 377** | 35 |
| `5nqbz` | 09-03 17:51 | **9 / 377** | 167 |
| `zn7nf` | 09-04 06:06 | 86 / 399 | 1 |
| `q8v6s` | 09-04 07:10 | 96 / 399 | **6 254** |
| `lgwrk` | 09-04 13:34 | 174 / 407 | 0 |

Coverage improves as bakes land, which is the adopt-only lane doing its job. But a replica that
boots having adopted 9 of 377 types will compile the other 368 **lazily, one per type, on first
access, spread over its whole life** — and pin each result. That is the same assembly accumulation
the original incident described, arriving as a drip instead of a wave, and driven by bundle
coverage rather than by supersession.

> **The distinction that matters:** a *superseded* build is retained because a serving instance
> stays on the old one — convergence fixes that. A *first* build is retained because the hub that
> ran it never deactivates — convergence cannot fix that, because there is no newer build to move
> to. Only shipping prebuilt bundles that actually cover the mesh removes the compile, and only
> releasing leases removes the retention.

## Why the detector goes blind exactly when it matters

`PortalReplicaWorkingSetDiverged` (in `deploy/aks/scripts/values.observability.yaml`) requires
`max > 3 × min` across a namespace's replicas. That conjunct is what makes it a *divergence*
detector, and it was chosen deliberately — its comment argues that *"a namespace whose replicas all
sit at 12 GB is a sizing question, not this incident"*.

Today's measurement falsifies that premise. `memex` runs two replicas, both 28 h old:

```
memex-portal-deployment-77898c4947-6mffn   195m   9936Mi
memex-portal-deployment-77898c4947-bfnft   167m   9409Mi
```

Ratio **1.06** against a threshold of 3. Both replicas are at ~80 % of the 12 GiB GC ceiling, both
are on the curve that produced the 2026-09-03 outage, and the alert sits at roughly a third of what
it needs to fire — and will keep falling as they converge. **The ratio is highest early, when one
replica is ahead of the others, and lowest at the end, when every replica is degraded.** A detector
built only on divergence is loudest during the warning and silent during the incident.

Divergence is still a good *early* signal and is kept — it read 7.22× correctly on 2026-09-01, and
lowering the 3× would only make it fire on every rollout, where a cold pod legitimately sits beside
warm ones. What was missing is the terminal case, which a ratio cannot express at all: an absolute
reading against the ceiling that binds. `PortalHeapEnteringGcDefenceBand` covers it — per namespace,
no sibling comparison, thresholded at 75 % of the container limit, which is the point at which the
working set has reached the *numeric value* of the GC's hard limit and any further managed growth
must come out of the margin the default reserved for native memory.

The expression was checked against the live Prometheus before it was committed, because an alert
that cannot fire is worse than no alert:

```
count(container_spec_memory_limit_bytes{container="memex-portal"})                    → 9 series
max by (namespace) (container_memory_working_set_bytes{container="memex-portal"}
                    / container_spec_memory_limit_bytes{container="memex-portal"})
                                            → memex-cloud 0.851 · memex 0.605
```

Both halves matter. `memex-cloud` — the namespace with 14 GB replicas and `Platform stalled` lines
in its log — is **above** the 0.75 threshold. `memex` is **below** it. The rule discriminates
between the two namespaces rather than firing on both, which is the control arm that a threshold
picked from a curve usually lacks.

🚨 **Neither rule is armed until `values.observability.yaml` is applied.** On 2026-09-01 the live
Prometheus answered `rule groups: 0` and the `loki` release was still on its 31 May revision — the
divergence alert had been merged for a week and was watching nothing, while the exact condition it
was written for was true. Committing a rule and arming a rule are different acts; `git grep` is
evidence of the first and never of the second. The only evidence for the second is
`GET /api/v1/rules` returning a group that contains the alert by name.

## The runtime already measures this and the measurement is thrown away

The portal calls `AddRuntimeInstrumentation()`, so `dotnet_gc_*` — heap size, pause time, per-
generation collection counts — is produced on every pod. The collector then does this
(`deploy/aks/manifests/observability/otel-collector-config.yaml`):

```yaml
        metrics:
          receivers: [otlp]
          processors: [resourcedetection, batch]
          exporters: [file/metrics]      # → a rotating JSON file on the node
```

Logs and traces go to files too, but logs are also scraped into Loki. **Metrics have no second
path.** Every runtime metric the portal emits lands in a node-local JSON file that nothing queries
and that dies with the node.

That is why the only memory signal any alert can use is `container_memory_working_set_bytes` —
a kubelet-side number that cannot distinguish managed heap from native memory, and cannot see the
GC ceiling at all. Routing the metrics pipeline to Prometheus would let an alert read
`dotnet_gc_heap_size` directly against the hard limit instead of inferring it from working set.
Until then the working-set proxy is the honest best available, and this page is the reason its
threshold is 0.75 rather than a round number.

## What `/alive` measures depends on the image, and the chart used to claim otherwise

Core registers exactly one health check on `/alive` — `"self"`, tag `live`, returning
`Healthy()` unconditionally. A progress-aware handler is registered by the **host**
(`Memex.Portal.Distributed`, in MeshWeaver.Plugins) under the same `live` tag. So what `/alive`
answers is a property of the deployed image, not of this chart, and this repository cannot assert
it.

The chart briefly asserted both readings at once: the readiness comment called a progress-aware
check *"an open design item"* while the liveness comment ten lines below stated that `/alive`
*"answers 503 once GC pause share has stayed above 60 % for five consecutive checks"*. Both were
committed, and a status report quoted the stale half as though it were current. The comments now
say which side owns the handler and what core alone guarantees.

🚨 **Do not point readiness at `/health`.** A heavy readiness check that fails under load removes
the pod and hands its traffic to the survivors — the 2026-07-21 death-spiral. Liveness restarting
one pegged replica has no such feedback loop; readiness does.

## Triage

```bash
# 1. Who is fat? Compare against 12 GiB (0.75 × the 16Gi limit), not against 16 GiB.
kubectl top pods -n <ns> --no-headers

# 2. Is it GC-bound, or just large? The runtime says so itself.
kubectl logs -n <ns> <pod> --since=30m | grep 'Platform stalled'
#    pause ÷ stall ≳ 0.6 sustained  ⇒  GC-bound
#    gen2 climbing while managed MB does not fall ⇒ retention, not a working set

# 3. Is anything actually being published? If these are all zero, convergence is not the lane.
kubectl logs -n <ns> <pod> --since=30h | grep -c 'Stale-build convergence'
kubectl logs -n <ns> <pod> --since=30h | grep -c 'self-heal recompile'

# 4. How much did this pod have to compile for itself?
kubectl logs -n <ns> <pod> --since=30h | grep -o 'adopted=[0-9]* uncovered=[0-9]* of [0-9]*'
```

Deleting the pod is the remedy that works and it buys hours, not days — it has been applied by hand
at least four times. It is a stopgap and is recorded as one: the pod that inherits the hubs starts
the same climb.

## Related

- [Node Type Compilation](../NodeTypeCompilation) — the retention cost model for superseded builds,
  and the lease that defers `Unload()`.
- [Measuring a Live Portal Read-Only](../MeasuringALivePortalReadOnly) — the read-only instruments
  used for every measurement on this page.
- [Reading a Silo Eviction](../ReadingASiloEviction) — the watchdog line quoted here, and how to
  read a silo that stops itself.
- [Cross-Schema Fan-Out Elimination](../CrossSchemaFanOutElimination) — the unanchored query
  fan-outs that ride the same pods; a CPU multiplier on this curve, not its cause.
- [Deployment — AKS](../DeploymentAKS) — the deploy routes and the private-cluster rule.
