---
Name: A probe must answer inside its own timeout
Category: Architecture
Description: /health is the startupProbe's instrument, and on one instance it grew to 8-10 s against a 5 s probe timeout — so a healthy new replica could never leave startup, was killed at its 3 h budget and started over. Why a startup timeout is the unrecoverable one, why the slowest check was by construction the one nobody could name, and the timing line /health now publishes.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/></svg>
---

# A probe must answer inside its own timeout

[Probe Semantics](../ProbeSemantics) says what each probe may *answer*. This page is about the other
half, which that page assumes: a probe endpoint has to be able to **answer at all**, inside the
budget the probe gives it. Once it cannot, its verdict stops mattering — the instrument decides the
rollout, not the health of the pod — and nothing in the fleet says so.

## The startup timeout is the unrecoverable one

| probe | what a timeout costs | how it recovers |
|---|---|---|
| `livenessProbe` | a restart | the restart is the recovery |
| `readinessProbe` | out of the Service | back in as soon as one probe succeeds |
| `startupProbe` | **the container never starts** | **it does not** |

Kubernetes suspends readiness and liveness until the startup probe records **one** success. Until
then the container is NotReady, so a roll with `maxSurge: 1 / maxUnavailable: 0` sits at
"1 of N new replicas updated" forever — correctly; the old pods keep serving and there is no
outage. When `periodSeconds × failureThreshold` runs out, the kubelet kills the container and the
next attempt starts the same clock again. On an instance running a gated NodeType bake that budget
is hours, so the loop is slow, quiet, and indefinite.

That is the asymmetry: a readiness or liveness timeout is self-correcting, and a startup timeout is
a state the pod cannot leave.

## What was measured

memex.systemorph.com, 2026-09-17, consecutive reads from outside the cluster:

| endpoint | response | 14:53Z | 15:22Z |
|---|---|---|---|
| `/health` | 200 `Degraded` | **8.12 s, 9.62 s, 9.52 s** | **10.71 s, 13.55 s** |
| `/alive` | 200 | 0.12 s | — |
| `/ready` | 200 | 0.12 s | — |

🚨 **It is getting worse, which is the argument against ever fixing this with a bigger
number.** Half an hour apart, on the same two serving replicas, with no deploy in between, the
endpoint went from ~9 s to ~12 s. A timeout raised to cover today's reading is a treadmill: it buys
the interval until the next growth, and the failure it hides is the unrecoverable one.

Same host, same pod, same TLS and the same ingress — so the seconds were entirely in the untagged
checks that only `/health` runs, not in the network or the process. The chart reads `/health` as the
`startupProbe`, and this instance's committed values give it **`timeoutSeconds: 5`**, with
`periodSeconds: 10` and `failureThreshold: 1080` — a 3 h budget, raised deliberately to fit a cold
bake, made of probes that each run out of time after five seconds.

So the endpoint takes **eight to ten seconds to answer a probe that waits five**. Not marginally
over: never once inside. `failureThreshold: 1080` buys 1080 attempts at a wall.

The replica rolled onto `3.0.0-ci.8812` at 11:23:40Z was still not Ready at 14:51Z, with
`restarts: 1` — and 11:23:40Z plus the 3 h budget is 14:23:40Z. The fleet watch, which gives each
replica's `/health` its own 8 s budget, reported `TaskCanceledException` not only for that pod but
for the two **healthy, serving** replicas beside it.

🚨 **And the reading exonerates the release.** The Service routes only to READY pods, so those
three timings were taken on `3.0.0-ci.8710` — core commit `afde4eab` (2026-09-15 21:50Z), which
**predates** every merge of the night the roll was meant to deliver (the unloadable-build fix merged
2026-09-16 21:59Z; an ancestry check says it is not in that image). The latency is not something the
candidate image introduced — and `memex-cloud`, on the older `3.0.0-ci.8411`, answers in 0.14–0.75 s,
so it is not a property of the image line either. It is a property of **this instance**.

What is still unmeasured is the latency on a pod that is **starting**. The fleet watch's own 8 s
probe timed out on the new replica three times out of four (11:46Z, 14:43Z, 14:51Z) and answered
once (14:38Z) — consistent with it being at least as slow as the serving pods, and nothing sharper
than that. A booting replica runs its cold bake on the same CPU and volume as these checks, so the
expectation is worse, not better.

And the bake gate was green across it. At 14:38:15Z the fleet watch read a body from the new pod
whose first word was `Degraded` — and the fleet watch's reader throws on any non-2xx, so that
reading was a **200**, which means no registered check was Unhealthy at that instant. A pod whose
every verdict is fine still could not leave startup.

## Why nothing could name the cost

The health-check framework logs a duration for every check. It logs it at **Information** when the
check is Healthy, and this fleet filters the `Microsoft.*` categories to Warning — measured: not one
line matching `with status Healthy` has ever reached the log store. And `/health` itself printed only
entries that are *not* Healthy, plus the census-tagged ones.

So an expensive check that is perfectly healthy appeared **nowhere**: not in the body, not in the
log. The slowest check on the endpoint was, by construction, the one kind of check no reader could
name — which is why
[#4588](https://github.com/Systemorph/MeshWeaver/issues/4588) had to file its attribution as a
caveat: *"the fleet watch reports /health unreachable … TaskCanceledException for that pod, and for
one of the two healthy ci.8710 pods as well, so that signal does not separate them."* It does not
separate them because **both** are over that watch's budget, for a reason the endpoint never stated.

## What `/health` publishes now

Line one is unchanged — the bare status word, which is what every caller parses (the fleet watch
takes the first whitespace-delimited token and writes it onto the deployment record). Line **two** is
the timing:

```
Degraded
timing: 9412ms total over 17 check(s), slowest first — db_version 9180ms; required_modules 402ms; 15 more under 10ms
content-types: Degraded — …
```

- The **total** is the number the probe's `timeoutSeconds` has to cover.
- Checks at or above 10 ms are **named**, slowest first. That is a threshold for NAMING, not a
  claim about blame: enough checks just below it would consume the budget between them. Which is
  why the total comes first and unconditionally — it always includes them, so *"total 9412ms,
  nothing named"* is itself an answer: the cost is spread, read the count rather than hunting for
  one culprit.
- The rest are **counted**, not dropped — the line states its own denominator, so "nothing else was
  slow" and "I stopped listing" are different sentences. Same rule as the census entries
  ([A Census That Counts Must Name](../ACensusThatCountsMustName)).

It is on line two, not at the end, because a reader of a truncated body gets the timing before any
description: the fleet watch keeps the first 2000 characters of the body on the deployment record.

`HealthTimingIsPublishedTest` drives the real `AddDefaultHealthChecks` + `MapDefaultEndpoints`
composition over a TestServer with a deliberately expensive check that answers **Healthy** and
carries **no census tag** — the exact combination the body used to drop entirely — and asserts it is
named with its cost, that a cheap check is counted rather than named, and that line one is still the
bare status word.

## What this does and does not fix

It makes the cost **attributable**. It does not make `/health` fast, and it deliberately does not
raise `timeoutSeconds`: a bigger timeout would move the cliff without removing it — and on the
instance measured above the number was still climbing while the page was being written, so the
cliff moves by itself. The real fix is
whatever the timing line names — and the shape that fix takes is already settled here. (What it
named was `required_modules`; see "Memoising the work is not moving it" below for what that took,
in two steps, and which half of it is still open.)

## What the timing line named, and what it cost first

The line shipped at 17:36Z on 2026-09-17. At **19:20Z the control instance went 503** and stayed
down for 27 minutes: both portal containers had restarted, and neither could pass startup again —
54 `context deadline exceeded` startup failures in nine minutes. The pods that had been serving all
day were serving only because they had passed startup *hours* earlier, under a `/health` that was
faster then. Nothing had been deployed. The instance had simply become unable to boot, and nobody
knew until something restarted it.

The line named the cost exactly:

```
timing: 10068ms total over 17 check(s), slowest first — required_modules 10068ms;
        pending_module_activation 30ms; data_volume_free_space 11ms; 14 more under 10ms
```

**One check was the entire budget.** `RequiredModulesHealthCheck` read the activation sidecar and
then probed the shared module volume twice per declared entry, on every call —
`File.Exists(MeshBuilder.ResolveModulePath(entry))` (itself up to three metadata round trips) and
`LandedModuleDllExists`. It is the same defect [#3664](https://github.com/Systemorph/MeshWeaver/issues/3664)
had already fixed on the sibling reader, on the same volume, for the same reason, and the fix is the
same one: the probe now takes `PendingModuleActivations.ReadProbeInputs()`, which answers all three
questions off one snapshot memoised behind a fingerprint of three directory timestamps
([#4609](https://github.com/Systemorph/MeshWeaver/issues/4609) in core, the portal half in
MeshWeaver.Plugins).

🚨 **What made it an outage rather than a slow boot is the asymmetry at the top of this page.** The
crash that restarted the pods was incidental; a container that cannot pass startup cannot recover,
and `maxUnavailable: 0` — correct, and what makes a roll zero-downtime — means the ingress had no
backend at all once the serving pods were gone.

## Memoising the work is not moving it, and the difference is the two probes that matter

The memo above answers a different question from the one the probe budget asks. It made the walk
happen *once per change of the volume* instead of once per probe — and left the probe as the caller
who performs it. Two callers are still exactly that caller, and between them they are every occasion
that decides a rollout:

| when the memo is cold | which probe pays it |
|---|---|
| nothing has been read yet | the **FIRST** probe of a fresh pod — i.e. the `startupProbe`, the one whose timeout a container cannot recover from |
| a module has just landed | the first probe after the module lane did the thing this check exists to report |

So "the first probe is slow instead of all of them" is not a smaller version of the same fix; on the
first row it is the whole defect, and on the second it is a gate that goes over budget precisely
while someone is watching a delivery.

[#4655](https://github.com/Systemorph/MeshWeaver/issues/4655) removes the shape rather than the
frequency. `PendingModuleActivations.ReadProbeInputs()` now performs **no filesystem call at all**:
it returns the last reading this process TOOK and asks `IIoPool` for a new one, and
`ModuleVolumeReadingHostedService` takes the first reading at host start, where nothing has a
five-second budget. The work has an owner, and the owner is not a probe — which is the rule the
section below already states for the rest of the endpoint.

Measured locally (APFS SSD; the share's per-round-trip cost is three orders of magnitude higher,
which is the whole of the seconds above), over a synthetic module volume:

| volume | walk, on the probe thread (before) | held read, on the probe thread (after) |
|---|---|---|
| 120 modules, 482 files | 17.9 / 18.1 / 18.6 / 19.2 / 27.1 ms | median **0.0117 ms** over 200 probes |
| 400 modules, 1,602 files | 48.2 / 48.9 / 50.6 / 51.4 / 56.4 ms | median **0.0074 ms** over 200 probes |

🚨 **Read the columns, not the ratio.** The left column is what grows with the volume — 3.3× the
files cost ~2.7× the time, and memex's share is two orders of magnitude larger again. The right one
does not move, because it is no longer a function of the volume at all. A probe whose first reading
does not exist yet measured **0.30 ms** and performed **zero** walks.

### One walk at a time, and the token that ends it

Two things the first cut of this got wrong, both found in review and both observable as a COUNT
rather than a duration:

- **The host-start reading took a walk of its own.** It called the refresh past the collapse, so the
  first `/health` of a booting pod — which arrives while that reading is still crawling the share —
  started a SECOND concurrent walk, on exactly the volume and exactly the minute this mechanism
  exists to protect, with the two snapshots free to land in either order. The reading is now a
  promise-cached one-shot (`PromiseSlot` over `IIoPool.RunBlocking`, an instance field): a caller
  arriving while a walk runs JOINS it. Nothing waits — joining hands back an observable. The
  regression test drives the host-start reading and two probes through a pool that refuses to run,
  and counts what was handed over: **1** with the collapse, **3** without it.
- **The pool's cancellation was discarded.** `ReadDisk` took no token, so a teardown only
  unsubscribed while the walk ran on. It now observes the token between its units of volume work —
  before the activation sidecar, and between it and the set index. It does **not** interrupt a
  single enumeration already inside `ModuleActivationSidecar.Read` or `ModuleSetStore.Read`; a
  per-file token is a change to those readers, whose callers all sit elsewhere. Stated rather than
  implied, because the comment it replaced claimed a stop the code could not make.

### Having taken no reading is not a clean reading

The window before the first reading lands is short and it is real, and the one thing it must not do
is read as a pass. An unread volume answers `false` to "does this module resolve from the image?" —
which is indistinguishable, at the call site, from a module the build genuinely lost. Reported as
that, the first probe of every pod would tell an operator to go and fix a build that is fine.

So the unread case is its own verdict. `ModuleProbeInputs.NotRead` carries the
`ModuleProbeInputs.VolumeNotRead` sentinel as its image-side resolver; `RequiredModuleStatus.Classify`
recognises it and answers `RequiredModuleState.Unmeasured` for every entry its **in-process** evidence
does not already settle — a module loaded in this process is still `Present`, which is what keeps
the state from becoming a blanket. And `RequiredModuleStatus.Absent` **counts** `Unmeasured`, so a
caller that has never heard of the new state still refuses: "I could not check" costs what "I
checked and it is missing" costs, because the expensive direction is the safe one for a rollout
gate. The state clears itself — the reading is in flight while it is reported.

**Still to move, and named rather than assumed:** `pending_module_activation` calls
`PendingModuleActivations.Read()`, which is the pull-on-demand reader and still walks a cold or
changed volume on its caller's thread. The host-start reading warms the same snapshot, so in
practice it finds one — but "in practice" is a race, not a property, and the durable fix is the
portal half in MeshWeaver.Plugins: both probes read the reading, and `required_modules`'s own
top-line sentence says "not measured" instead of inheriting the absent branch's wording.

## Where the probe may point, and what moving it costs

The break-glass mitigation was `startupProbe.httpGet.path` → `/ready`, patched live with the
maintainer's approval, and it did bring the instance back in ~6 minutes. It is a **mitigation, not
the fix**, and the chart now makes that choice expressible (`probes.startup.path`, default
`/health`) for one reason: a path changed with `kubectl patch` is undone by the next `helm upgrade`
without anyone deciding to undo it, and a deployment that must move it should move it where it is
reviewed and where chart drift can see it.

Moving it costs two things, and both are load-bearing:

| what is lost | why |
|---|---|
| the **NodeType bake gate** | `nodetype_bake` is deliberately tagged neither `live` nor `ready` (see [Probe Semantics](../ProbeSemantics)), so it lands on `/health` alone and the startup probe is its ONLY reader. Point the probe elsewhere and `PreWarm__GateReadiness` goes on being configured, goes on reporting healthy, and gates nothing. |
| **no traffic to a booting pod** | the startup probe is what holds readiness on the heavy path until the mesh is up. On `/ready` a pod is "started" the instant the process accepts a socket, so it joins the Service while the mesh is still booting. |

So `/ready` is what you reach for when an instance is down and cannot boot — and what you reconcile
away once the endpoint answers again. Two guards make the trade visible instead of silent:
`PreWarmGateReadinessGuard` fails when the chart arms the gate and `probes.startup.path` is not
`/health`, and **invariant 10b** in `deploy/aks/scripts/check-chart-invariants.py` fails the same
combination on the *rendered* manifest, where an overlay could arm the gate the chart does not.
Invariant 10 already refuses the narrower case of readiness and startup sharing one path.

🚨 **The durable rule is not about the path at all.** The startup probe's budget is fixed and
`/health`'s cost is not: it is an aggregate census whose members are registered by several
repositories and whose per-check cost grows with the mesh, the module volume and the partition
count. So the invariant that has to hold is the one the section below states — **every check on
that endpoint reads a reading, and does not take it** — and the timing line is what makes a breach
nameable in one `curl` instead of an argument.

## The headroom is the number to watch, and it is per instance

`timeoutSeconds` for this probe is **5 s** on the shipped chart and on three of the four environment
overlays — instances raise `periodSeconds` and `failureThreshold` to fit a cold bake, and leave the
per-probe timeout alone. The fourth is `memex`, raised to **30 s** during the 2026-09-17 incident,
which is a stopgap with a condition attached: **it comes back to 5 once the image carrying the
`required_modules` fix is running.** Read that number as debt, not as the setting — a raised
per-probe timeout is how the next growth becomes invisible, and the `build`, `memex-cloud` and
`pearl` overlays are at 5 precisely because nothing has needed to hide anything from them.

So the quantity that decides whether a roll can ever finish is `/health` latency against a fixed
five seconds:

| instance | `/health` warm | headroom against 5 s |
|---|---|---|
| memex | 8.12 / 9.62 / 9.52 s | **none — every probe times out** |
| memex-cloud | 0.14 / 0.75 s | ~7× |

Re-measured after the outage, 2026-09-17 **20:19Z**, from outside the cluster (so TLS and ingress are
in the number), with the fix merged in core and its portal half still open:

| instance | `/health` | what its own timing line says | headroom against 5 s |
|---|---|---|---|
| memex | **9.10 s** | `timing: 8968ms total over 17 check(s) … required_modules 8968ms; pending_module_activation 21ms; data_volume_free_space 14ms; 14 more under 10ms` | **none** — it answers only because the startup probe is patched to `/ready` |
| memex-cloud | **1.66 s** | *no timing line* — this instance is on an image from before the line shipped | ~3× |

🚨 **Read the second row as the next one to watch, not as the comfortable one.** `memex-cloud` is the
instance with 714 module generations and 33,383 files on its share — the volume whose size is the
whole of the first row's number — and it is at the chart's `timeoutSeconds: 5` with no override.
Three times is not seven, it cannot yet say which check is spending it, and the growth that closed
memex's headroom is growth it has more of.

Two things follow. First, this is **not** a property of the image: both instances were running
images from the same line. Second, a warm reading is not the reading that matters — a booting
replica runs its cold bake on the same CPU and the same volume as these checks, so the number to
compare is `/health` **on a pod that is still starting**, which nothing sampled until the timing
line existed.

## A health check reads a registry; it does not do the work

Every check that answers quickly on this endpoint follows one pattern: the **work** writes a reading
into a mesh-scoped instance registry, and the **check** reads the last reading. `content-types`
(0.06 ms), `bake-report`, `source-discovery` and `publication-seal` are all that shape, and
`pending_module_activation` was moved to it — "the module-activation probe reads the volume once per
change" — precisely because reading a volume per probe had already made memex-cloud's rollouts fail
startup once.

A check that performs live IO per request — a database round trip, a `statfs` against a network
share, a registry fetch — puts unbounded latency on the one endpoint whose latency is a rollout
gate. Two checks added on 2026-09-08 read volume capacity synchronously on every call
(`StorageCapacityHealthCheck`, `DataVolumeHealthCheck`), and their own doc comments already claim
they "only take the reading, exactly as `ContentTypeHealthCheck` only reads its registry". Whether
they are what the timing line names is now a one-`curl` question rather than an argument — and the
answer, on the first line it published, is **no**: `data_volume_free_space` measured **11 ms**
against `required_modules`'s 10 068 ms. One `statfs` per configured path is a constant; a probe of
the volume per declared module entry is not, and the difference between those two shapes is the
whole of this page.

🚨 **`/health` is not the only fixed budget spent by that aggregate.** The fleet watch gives each
replica's `/health` **8 s** (`ObservationQueries.cs`, MeshWeaver.Plugins) and the public host **15 s**
— which is why, on the day of the incident, it reported `TaskCanceledException` for the stalled
replica *and* for the two healthy ones beside it, and therefore could not tell them apart. A second
consumer of the same endpoint, with the same fixed budget and no way to say which check spent it:
when the timing line names a slow check, it is naming it for every reader of `/health` at once.

### The rest of the endpoint, audited (2026-09-17)

The question the incident raises is *"where else does a fixed budget pay for something that grows?"*
Every check registered on the portal — core and `Memex.Portal.Distributed` — read, with the shape of
its per-call work:

| shape | checks |
|---|---|
| **reads a registry or a counter** — cost independent of the mesh | `content-types`, `bake-report`, `source-discovery`, `publication-seal`, `bundle_adoption`, `entitlement_anchor`, `nodetype_bake`, `process_progress`, `pending_module_activation` (memoised by [#3664](https://github.com/Systemorph/MeshWeaver/issues/3664)) |
| **one bounded call per probe** — live IO, but a constant | `db_version` (one round trip), `storage_capacity` and `data_volume_free_space` (one `statfs` per *configured path*, 11–14 ms measured) |
| **per declared entry, per probe** | `required_modules` — and it is the only one. Since [#4655](https://github.com/Systemorph/MeshWeaver/issues/4655) it is in the first row: it reads a reading a background owner takes |

So the defect was singular on this endpoint, and it is now fixed at the root rather than tuned. Two
things next to it are the same shape and are worth naming rather than filing:

- **the NodeType bake sweep.** ~2.4 s per NodeType, strictly sequential, inside the startup probe's
  `periodSeconds × failureThreshold`. That budget was deliberately widened for it (3 h on the
  instances that arm the gate), which is the *right* answer to per-item work — a budget sized to the
  work, not a timeout sized to a reading — but the measured worst case is already **> 63 min** for
  ~230 types under serving load, i.e. more than a third of the ceiling. It is bounded, watched, and
  the one to re-derive when the type count next jumps.
- **the fleet watch's own pass.** Its per-replica budget is the 8 s above, fanned out over a roster
  that grows with the fleet, against a `staleAfter` derived from the sweep interval. See
  [#4611](https://github.com/Systemorph/MeshWeaver/issues/4611) — which should be re-measured once
  `/health` answers in milliseconds again, because until then its slow-pass reading has an
  explanation that is not its own.

Not this class, checked and dismissed: the `Hosting/InstanceAction` deadlines (`JobCap` 45 min,
`ResolutionBudget` 30 s, `IndexGrace` 10 s) and the migration Job's `budgetMinutes`, which bound
whole operations rather than per-item fan-out — and the migration's own doc already states the rule
this page states, in its own words: *"a migration that needs longer is not a migration to make room
for, it is one to rewrite as bulk work (one set-based statement per partition, never a request per
row)."*

## Known: two policies that are configured and applied to nothing

`AddDefaultHealthChecks` registers a request-timeout policy named `HealthChecks` (20 s) and an
output-cache policy named `HealthChecks` (20 s). `MapDefaultEndpoints` attaches neither, and no
`CacheOutput` or `WithRequestTimeout` call exists anywhere in the repository — so both read as
protections that are not there, which is the shape this repository refuses everywhere else.

Wiring them is **not** the fix for the above and must not be done as a side effect of it:

- the request timeout is 20 s against a 5 s probe timeout, so the probe never waits long enough to
  see it fire;
- the output cache cannot fill on an endpoint whose every request is aborted by the client before it
  completes, and a 20 s cache on `/health` makes the startup gate up to 20 s stale — a change to
  what the gate means, which needs its own decision rather than inheriting one.

## See also

- [Probe Semantics](../ProbeSemantics) — which question each probe answers, and the two guards that
  hold the chart and the code to each other
- [Measuring a Live Portal Read-Only](../MeasuringALivePortalReadOnly) — `/health` as the public,
  past-RLS instrument, and how to read an absence on it
- [A Census That Counts Must Name](../ACensusThatCountsMustName) — why a count without its denominator
  reads as clean
- [Applying Is Not Rolling Out](../ApplyingIsNotRollingOut) — the other place a fixed timeout decided
  the outcome of a correct operation
