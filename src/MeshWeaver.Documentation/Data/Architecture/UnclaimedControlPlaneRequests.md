---
Name: Unclaimed Control-Plane Requests
Category: Architecture
Description: An InstanceAction that shows no progress means four different things — queued, unclaimed, withdrawn by its requester, or running under an observer that died — and the node spells three of them the same way. The 2026-09-10 measurement, what it falsified, and the two defects it actually contains.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/><path d="M4.5 4.5l15 15" opacity="0.35"/></svg>
---

# Unclaimed Control-Plane Requests

Operations on a deployment go through the control instance's Hosting API: a roll, restart, suspend,
audit, reconcile, `Sample` or `Logs` is a `Hosting/InstanceAction` node that the in-cluster operator
picks up and reports itself on. See [Operating From The Portal](/Doc/Architecture/OperatingFromThePortal).

> **The gap, in one sentence.** A request nobody has claimed yet and a request nobody will ever claim
> are **byte-identical** — `version 1`, `log: []`, no `state`, no `phase`, no `startedAt` — and a
> request whose operator died mid-flight renders as `⏳ Step 5/8 …`, indistinguishable from work in
> progress. The plane cannot report its own deadness in any of the three cases.

## What was measured (2026-09-10, control instance, read-only)

**Version is the evidence, not `lastModified`** — `stream.Update` never re-stamps `lastModified`, so
an executed action still shows its creation time.

| Node | Reading |
|---|---|
| `memex-restart-20260910-1140-activate-ai16` | created 11:33:56Z, **`state: Running`, `phase: Run operator job`, step 5/8, no `finishedAt`** — still, nine hours later. Log ends 11:36:54Z. |
| `memex-sample-20260910-1207-settled` | created 12:02:03Z, `version 11`, `state: Done`, started 12:05:44Z, **finished 12:05:49Z** |
| `Ops/Status/memex`, `Ops/Status/memex-cloud` | both written **16:01:18Z**, both `Healthy` |
| `memex-cloud-restart-20260910-1601-activate-mail16` | created 16:00:53Z — **withdrawn by its requester at 16:05Z**; the content carries no `requestedAction` at all |
| `memexcloud-logs-20260910-gitsync237` | created 17:32:21Z, `requestedAction: Logs`, `version 1`, `log: []`, still untouched at 18:28Z |

## Three things this falsifies

**1. "Every request since the restart is never processed" — false.** The four requests named as stuck
(11:44Z, 11:46Z, 11:50Z, 11:52Z) all now carry `version 11`–`12` with populated logs. **They
executed.** The observation was made at 11:57Z, while they were still slow; the conclusion "never
processed" outlived the state it described. A snapshot of a transition is not a property.

**2. The stuck restart did not stop the plane.** Six actions completed *after* it froze at 11:36:54Z
— including a **Restart** (`memex-cloud-restart-…-1150`, `version 21`) and repeated **Samples of the
very deployment it is stuck on**. Neither the action class nor the deployment is blocked, so it holds
no lease either needs. The plane was still writing at 16:01:18Z, **4 h 24 m** after the freeze.

**3. The 16:03 node is not evidence about the plane.** It reached `version 2` because a *human* edited
it — withdrawing the request after waiting **four minutes** and going break-glass instead. Reading it
as "the plane got this far and stalled" reads a person's edit as the plane's behaviour.

## Two defects, not one

### A. An action outlives its observer, and nothing records that it did — **live, reproduced**

`kubectl rollout status` lost its watch three seconds in and never recovered:

```
E0910 11:34:03 reflector.go:158 "Unhandled Error" err="…informerwatcher.go:146: Failed to watch *unstructured.Unstructured: unknown"
Waiting for deployment "memex-portal-deployment" rollout to finish: 1 out of 2 new replicas have been updated...
```

Nine retries with backoff, the last at 11:36:54Z, then silence. **The rollout itself succeeded** — the
12:05:44Z `Sample` reports `2/2 ready, 0 restarts, 3.0.0-ci.8238`. So the operation completed and the
record never learned it had.

The action's own plan predicted the mechanism and said so in its log:

> ⚠️ The deployment's rollout budget exceeds the 3000-second observation window of one operator job.
> The job may stop waiting before the rollout is complete. Check the rollout status afterwards.

🚨 **And the second half is worse than the warning.** That window expired around 12:24Z. Whatever the
job then did — exited non-zero, was reaped, or is still parked — **the node did not move**. The
failure of the observer is as unrecorded as the success of the operation. A viewer sees
`⏳ Step 5/8 — Run operator job…` for nine hours, which is exactly what a healthy long rollout looks
like.

**A bound the phase is measured against is the missing piece**, not a watchdog that re-runs the
action: re-running a restart whose rollout already succeeded restarts a healthy deployment.

### B. No acceptance signal on the request itself — **real, but the live evidence is thin**

| State | What the node shows | How long it is normal |
|---|---|---|
| queued, will be picked up | `version 1`, `log: []` | seconds to minutes |
| nobody is listening | `version 1`, `log: []` | for ever |
| withdrawn by its requester | `version 2`, `log: []` | for ever |

Only **one** clean instance is in the record — the 17:32Z `Logs` request, unclaimed for 56 minutes —
and it is one deployment, one action class. That supports *"a Logs request on memex-cloud went
unclaimed for ~1 h"*. It does **not** support *"the control plane executes nothing"*: after 12:02Z
only two `InstanceAction` nodes exist at all and **both target `memex-cloud`**, so whether `memex`'s
requests are being served is **unknown, because nobody has asked**.

That is [Controls That Cannot Fail](/Doc/Architecture/ControlsThatCannotFail)'s *"a count over a
population whose membership varies"*: a zero here means "nothing was requested" as readily as
"nothing was served".

## The discriminator that exists, and the one that does not

**Exists — `Ops/Status/<deployment>.sampledAt`.** Written by the operator, covering every deployment,
and — unlike an action node's silence — *staleness relative to now* is a real signal. On 2026-09-10 it
was 2 h 27 m stale while its own `health` field still read `Healthy`. **Read `sampledAt` before
believing `health`:** the node says the deployment is fine and says nothing about whether anyone is
still looking.

**Does not exist — a claim on the request.** The shape a fix would take is a **claim with a heartbeat
behind it**: the operator stamps "seen by operator `X` at `T`" when it picks a request up, and keeps a
liveness stamp the requester can read, so an unclaimed request is separable from a pending one by
comparing against a clock that is still moving — and a phase that has not advanced within its own
declared window becomes a statable fact rather than an ellipsis.

🚨 That spans the operator and the `Hosting/InstanceAction` NodeType, **both of which live in
`MeshWeaver.Plugins`, not here**, and choosing where the stamp lives (the request node, the deployment
record, or a per-operator lease node) has real write-volume consequences on a shared record. **It is a
maintainer decision, and this page states it rather than guessing.**

🚨 What it must NOT be: a watchdog that re-posts the request, or a timeout that marks the action
failed. Neither answers *why* nothing claimed it, and re-posting against a live-but-slow operator
executes the operation twice — which for a `Restart` means restarting a healthy deployment.

## Relationship to module-generation substitution

[Module Generation Substitution](/Doc/Architecture/ModuleGenerationSubstitution) fixes a *different*
silence from the same incident: a process running a module generation other than the one its
activation record names. It makes that legible at boot, in the loader.

**It covers neither defect on this page.** A request nobody claimed still reads `version 1, log: []`,
and a phase frozen at 5/8 still renders as an hourglass, whatever the loader has learned to say about
module generations. Three neighbours in one incident, not one defect.

## The rule

**When an InstanceAction shows no progress, do not read the action node first.** Read the instrument
whose population is not the thing under suspicion:

1. `Ops/Status/<deployment>` — is `sampledAt` moving? If it is stale, nothing will be picked up and
   the action node has nothing to tell you.
2. The action's `state`/`phase` — `Running` with an old `startedAt` and no advancing log is **defect
   A**, not progress. Check whether the underlying operation *already succeeded*.
3. `version 1` with `log: []` is **unclaimed OR pending**, and the difference is not on the node.
   `version 2` with no `requestedAction` is a **withdrawal by a person**.
4. State the **window**, never a cause — and say how many requests were in it.
