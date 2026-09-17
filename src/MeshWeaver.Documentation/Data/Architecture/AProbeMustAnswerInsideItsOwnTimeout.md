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

memex.systemorph.com, 2026-09-17, three consecutive reads from outside the cluster:

| endpoint | response | time |
|---|---|---|
| `/health` | 200 `Degraded` | **8.12 s, 9.62 s, 9.52 s** |
| `/alive` | 200 | 0.12 s |
| `/ready` | 200 | 0.12 s |

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
- Checks at or above 10 ms are **named**, slowest first. Ten milliseconds is a five-hundredth of the
  5 s these instances give the endpoint, so nothing below it can be part of an explanation for a
  probe that timed out.
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
raise `timeoutSeconds`: a bigger timeout would move the cliff without removing it, and the next
instance to grow past the new number would fail the same way with the same silence. The real fix is
whatever the timing line names — and the shape that fix takes is already settled here.

## The headroom is the number to watch, and it is per instance

`timeoutSeconds` for this probe is **5 s** on the shipped chart and on every environment overlay
measured — instances raise `periodSeconds` and `failureThreshold` to fit a cold bake, and leave the
per-probe timeout alone. So the quantity that decides whether a roll can ever finish is
`/health` latency against a fixed five seconds:

| instance | `/health` warm | headroom against 5 s |
|---|---|---|
| memex | 8.12 / 9.62 / 9.52 s | **none — every probe times out** |
| memex-cloud | 0.14 / 0.75 s | ~7× |

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
they are what the timing line names is now a one-`curl` question rather than an argument.

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
