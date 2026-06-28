---
name: storm
description: Diagnose and CURE storms that wedge the portal — resubscribe/retry storms, NotFound/DeliveryFailure storms, per-instance-hub accumulation, create-in-render loops. A storm makes the process unresponsive → liveness /healthz times out → the pod is pulled from the Service → ingress returns 502 → SIGKILL + restart. Use when a portal pod restarts/502s, memory climbs unbounded, the log volume explodes, or you are reviewing reactive code that could re-fire on every emission. Storms come from exactly two roots: (1) sync-over-async blocking, (2) uncaught / not-forwarded exceptions or delivery failures. Grounded in ErrorPropagationAndWedges, AccessContextPropagation.md; pairs with the /async skill.
user-invocable: true
allowed-tools:
  - Read
  - Bash
  - Grep
  - Edit
---

# /storm — Storms wedge the portal. Cure the root, never the symptom.

A **storm** is the same cheap operation re-firing without bound — a resubscribe, a retry, a
NotFound re-post, a thread/hub re-activation, a write that fails and is retried. Individually each
is fast; together they starve the scheduler / exhaust memory / pin the action block. The process
stops responding → the k8s **liveness probe `/healthz` times out** → the pod is removed from the
Service endpoints → **the ingress has no healthy backend → 502** → liveness SIGKILLs (exit **137**)
→ restart. Users see a 502 outage. **This must never happen.**

> Canonical references:
> - [ErrorPropagationAndWedges](../../../src/MeshWeaver.Documentation/Data/Architecture/ErrorPropagationAndWedges.md) — the wedges-to-zero principle this skill enforces.
> - [/async](../async/SKILL.md) — the sync-over-async + AccessContext half of every storm.
> - [AccessContextPropagation.md](../../../src/MeshWeaver.Documentation/Data/Architecture/AccessContextPropagation.md) — lost identity → silent write failure → swallowed → retried → storm.
> - [MeshNodeStreamCache.md](../../../src/MeshWeaver.Documentation/Data/Architecture/MeshNodeStreamCache.md) — the existing storm-breaker (negative cache / backoff) to build ON, not around.

## The two roots (there are only two)

Every storm traces to one of these. Find which, fix THAT:

1. **Sync-over-async / blocking.** `async`/`await`/`Task<T>`/`.ToTask()`/`.FirstAsync()`(blocking)/
   `.Wait()`/`.Result`/`.GetAwaiter().GetResult()`/`Observable.FromAsync`/`SemaphoreSlim` on a hub
   action block, grain turn, or Blazor circuit. It parks the scheduler; the awaited message can
   never be processed → deadlock that presents as a hang. → fix per **[/async](../async/SKILL.md)**.

2. **Uncaught / not-forwarded exceptions or delivery failures.** A write fails (often because
   AccessContext was lost across a `.Subscribe`/IIoPool hop → RLS denies), a stream errors, a
   request gets `NotFound`/`DeliveryFailure` — and the error is **swallowed** (`.Catch(Observable.
   Empty)`, `catch {}`, `_ => { /* ignore */ }`) or **retried** (a 1 s ticker, a watchdog, a
   resubscribe, a `.Retry()`) instead of surfaced. The amplifier turns one mishandled error into a
   flood. (The 2026-06-08 outage was an initial-state retry watchdog; the 2026-06-25 atioz outage
   was thread-hub accumulation + a swallowed-and-retried heartbeat read.)

## The cure (one principle)

> **Surface errors until you reach a boundary where you can error gracefully — then error
> gracefully.** Do NOT swallow, and do NOT retry a "shouldn't happen" state. Forward the error up
> the reactive chain (`OnError`) until it reaches a real sink: an activity's terminal `Status =
> Failed`, the GUI error area / `PortalErrorSink`, a thread's output cell, a logged warning at a
> known boundary. Everything before that sink must *forward*, not absorb.

Band-aids that are FORBIDDEN as "the fix" (they hide the storm): raising a timeout/pool size/retry
count; a watchdog/poller that resubscribes; `.Catch(Observable.Empty)`; `catch {}`; a `Clear()`
"for isolation"; a sleep. See AGENTS.md → "No band-aids".

## Diagnose a live/just-happened storm (the procedure)

The portal hung silently while wedged (a deadlocked/starved process stops logging), then SIGTERM at
shutdown emits a **mass-disposal storm** that can rotate the pre-hang logs out of the buffer. Work
the timeline:

```bash
# 1. Did the pod restart, and how? exit 137 + "failed liveness probe" = wedge (not OOMKilled).
az aks command invoke -g <rg> -n <cluster> --command \
  "kubectl get pod <portal-pod> -n <ns> -o jsonpath='{.status.containerStatuses[0].restartCount}{\" \"}{.status.containerStatuses[0].lastState}'; \
   kubectl get events -n <ns> --sort-by=.lastTimestamp | tail -30"

# 2. Find the SILENCE = the hang window. Per-minute line counts around the kill.
#    Zero lines for minutes, then a huge burst in the final minute = hung, then mass disposal.
az aks command invoke ... --command \
  "L=\$(kubectl logs <pod> -n <ns> --previous --timestamps 2>/dev/null); \
   for m in 45 46 47 48 49 50 51 52 53 54 55 56; do echo -n \"HH:\$m \"; echo \"\$L\" | grep -c \"THH:\$m\"; done"

# 3. What stormed? Source frequency in the wedge/disposal window; storm counts.
az aks command invoke ... --command \
  "L=\$(kubectl logs <pod> -n <ns> --previous 2>/dev/null); \
   echo \"\$L\" | grep -E 'fail:|warn:' | grep -oE '(fail|warn): [A-Za-z0-9._]+' | sort | uniq -c | sort -rn | head; \
   echo -n 'NotFound: '; echo \"\$L\" | grep -c NotFound; \
   echo -n 'distinct sync hubs disposed: '; echo \"\$L\" | grep -oE 'sync/[A-Za-z0-9_-]+' | sort -u | wc -l"

# 4. Distinct vs looping. Is it N things created (creation storm) or 1 thing re-firing (loop)?
az aks command invoke ... --command \
  "kubectl logs <pod> -n <ns> --previous 2>/dev/null | grep -oE '<the-repeating-token>' | sort | uniq -c | sort -rn | head"

# 5. Is the CURRENT (restarted) container re-accumulating? memory trend + the repeating block.
az aks command invoke ... --command "kubectl top pod -n <ns>; kubectl logs <pod> -n <ns> --since=60m | grep -A1 '<token>' | head -30"
```

Read the **last lines before the silence** (`grep -v 'THH:<kill-minute>' | tail -50`) — the last
thing logged before it froze is the trigger. If those are rotated out, the mass-disposal volume
(distinct `sync/` hubs) tells you the *scale* of the accumulation, and step 5 tells you whether
it's still happening.

## Storm taxonomy — tell → cure

| Storm | Tell in logs / code | Cure |
|---|---|---|
| **Resubscribe / initial-state retry** | `Observable.Interval`/`Timer` or `.Catch` that re-subscribes the source on a "shouldn't happen" state; repeated SubscribeRequest | Delete the retry; let the error surface. If the initial state is genuinely missing, find why it's dropped/errored. |
| **Swallow-and-retry** | `_ => { /* swallow; next tick retries */ }` on a per-tick read; `.Catch(Observable.Empty)` inside a loop | Forward the error to a sink; drop the path from the retry set on terminal failure; don't re-read a NotFound every tick. |
| **NotFound / DeliveryFailure** | `[ROUTE] NotFound` repeating; ownerless satellite (`_Thread`/`_Activity`/… with no real owner); a path advertised before its `CreateNode` completed | Never advertise a node path until Create completes (stamp inside `.Subscribe`). Anchor satellites under a real owner. Lean on the MeshNodeStreamCache negative cache. |
| **Per-instance hub accumulation** | thousands of `sync/` hubs at disposal; a per-hub `Observable.Interval` heartbeat that runs until dispose; memory climbing | Ensure per-instance hubs (thread/round/sync) are disposed when their round/thread reaches terminal state, not only at mesh shutdown. A live timer must not outlive its purpose. |
| **Create-in-render** | `StartThread`/`SubmitMessage`/`CreateNode` inside a `.Select`/`.Subscribe` on `GetMeshNodeStream(...)` that re-fires every emission | Move creation out of the render/subscription; one-shot it (`.Take(1)`/init) or make it idempotent (deterministic path), guard with `DistinctUntilChanged`. |
| **Sync-over-async deadlock** | `/healthz` timeout with no error; `.Result`/`.Wait()`/`.ToTask()` on a hub/circuit | Per [/async](../async/SKILL.md): compose `IObservable` + Subscribe; push the leaf to `IIoPool`. |

## Where to look first (highest-yield)

Storms cluster where an error crosses an async boundary and isn't forwarded — **usually because
AccessContext was lost** (write fails closed → swallowed → retried). Grep for the amplifiers:

```bash
rg -n "Catch\(Observable\.Empty|Catch<[^>]+>\(_ *=> *Observable\.Empty|catch *\{\s*\}|/\* *swallow|/\* *ignore|\.Retry\(" src
rg -n "Observable\.Interval|Observable\.Timer\(|RegisterForDisposal\(_ *=> *sub" src   # unbounded tickers — does the sub die with its purpose?
rg -n "\.Subscribe\(\s*_? *=> *\{ *\}\s*,\s*_? *=> *\{\s*\}\)" src                      # write with both arms empty = swallowed failure
```

For each hit decide: **graceful sink** (logged/surfaced at a real boundary → OK) or **silent
swallow / blind retry** (→ fix: surface, or stop retrying a terminal state). When the swallowed
thing is a *write*, check the [/async](../async/SKILL.md) AccessContext rule first — the failure is
usually a fail-closed RLS denial from a lost identity, and the real fix is to carry the context,
then let any *remaining* error surface.

## Checklist before merging reactive code

- [ ] No blocking async on a hub/grain/circuit (`.Result`/`.Wait()`/`.ToTask()`/`.FirstAsync()`-blocking/`Observable.FromAsync`/`SemaphoreSlim`). → [/async](../async/SKILL.md)
- [ ] Every `.Subscribe` write has an **onError** that surfaces or logs at a real boundary — never an empty swallow inside a loop/ticker.
- [ ] No timer/watchdog/`.Catch` that re-triggers a "shouldn't happen" state. If state is missing, fix the source, don't retry.
- [ ] Writes on `.Subscribe`/IIoPool/`Observable.Create` hops carry AccessContext (else fail-closed → swallow → storm).
- [ ] Node paths are advertised only after `CreateNode` completes; satellites anchor under a real owner.
- [ ] Per-instance hubs / timers are disposed at terminal state, not only at mesh shutdown.
- [ ] If a bound exists (top-N, no-retry, sampling), it's `log()`-ged — silent truncation reads as success.
