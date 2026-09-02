---
Name: Reading a Silo Eviction
Category: Architecture
Description: "I have been told I am dead" with a heartbeat newer than the suspect votes looks like a false-positive kill and usually is not. The two readings that make a correct eviction look wrong, the control arm that separates them, and the measured 2026-09-02 case.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12h3l2-6 3 12 2.5-8 1.5 4h6"/><circle cx="19" cy="19" r="3"/><path d="M17.2 16.8 20.8 21.2"/></svg>
---

# Reading a Silo Eviction

An Orleans silo that reads its own `Dead` row stops itself and says so:

```
fail: Orleans.Runtime.MembershipService.MembershipTableManager[100627]
  I have been told I am dead, so this silo will stop! Reason: I should be Dead according to
  membership table (in CleanupTableEntries): entry = [SiloAddress=S10.244.4.154:11111:147256594
  … Status=Dead … IAmAliveTime=2026-09-02 03:12:40.984 GMT
  Suspecters=[S10.244.3.122:…, S10.244.5.16:…, S10.244.3.122:…]
  SuspectTimes=[2026-09-02 03:12:40.394 GMT, …, …]]
```

Two details in that line look like proof of a false-positive kill, and **both are misreadings**:

| Looks like | Actually is |
|---|---|
| `IAmAliveTime` (`…40.984`) is **newer** than every `SuspectTime` (`…40.394`) — the silo was still heartbeating when it was declared dead | The two operations have nothing like the same cost. See below. |
| All three suspect timestamps are **identical to the millisecond** — three independent clocks cannot agree that precisely, so they must share one stale snapshot | That is what an **indirect probe** looks like. One table write records the requester's vote and the intermediary's confirmation together. |

Getting either wrong sends you to fix Orleans' failure detector, which is fine, instead of the
process that stopped answering — and the process that stopped answering is still doing it.

## Why a live heartbeat proves nothing

`IAmAliveTime` is refreshed by a **single cheap row upsert on its own timer**. Answering
`IMembershipService.Ping` is a **grain call**: it needs a thread-pool thread and a turn. A process
deep in GC-stall territory does the first and not the second, so during a stall the membership row
keeps looking fresh while every probe times out. That is not a contradiction — it is the *signature*
of a stalled host.

The victim's own log is where this is decided, and on 2026-09-02 it settled the question in one line.
The heartbeat cited as proof of liveness — `IAmAliveTime=03:12:40.984` — carries the **same
timestamp** as:

```
2026-09-02T03:12:40.984  .NET Runtime Platform stalled for 00:00:06.5910746.
                         Total GC Pause duration during that period: 00:00:06.5384310.
                         We are now using a total of 11886MB memory.
                         Collection counts per generation: 0: 10594, 1: 5443, 2: 2880
```

The heartbeat did not land because the silo was healthy. It landed **at the instant the process came
out of a 6.6-second GC pause** — one of the gaps between pauses, which is exactly enough for a row
upsert and nowhere near enough for an RPC.

And the silo was measuring itself, too. Orleans' `LocalSiloHealthMonitor`, on the victim, minutes
before the vote:

```
03:11:04  .NET Thread Pool is exhibiting delays of 10.8451907s
03:11:36  .NET Thread Pool is exhibiting delays of 38.1501219s
03:12:34  Self-monitoring determined that local health is degraded. Degradation score is 6/8
```

**A silo with a 38-second thread-pool delay is, for failure-detection purposes, dead.** The eviction
was correct.

## The control arm: what would falsify it

A rule that cannot say *no* is worth nothing, so this one has an explicit discriminator. Count the
probe-failure **targets** across the suspecters' whole retained log:

```bash
az aks command invoke -g <rg> -n <cluster> --command \
  "kubectl logs <suspecter-pod> -n <ns> \
   | grep -oE 'Did not get response for probe #[0-9]+ to silo S[0-9.]+:[0-9]+' \
   | grep -oE 'S[0-9.]+:[0-9]+' | sort | uniq -c"
```

| Result | Reading |
|---|---|
| Failures against **one** silo only | The target was the problem. A correct eviction — go and look at the target's heap, GC and thread pool. |
| Failures against **several** silos, or spread across suspecters | The network or the cluster was the problem. **This is the genuine false-positive shape** — take the membership hypothesis seriously. |

On 2026-09-02 the two suspecters had 12 and 59 probe failures between them **and every single one
named `S10.244.4.154`** — zero against any other silo, over 17 h and 9 h of retained log. That is
not a shared stale snapshot and not a network event; it is one process.

Three further checks, each of which could have flipped the verdict and did not:

- **Was a suspecter itself shutting down?** (`#3053`'s hypothesis.) `kubectl get pod … -o
  custom-columns=…,RESTARTS:.status.containerStatuses[0].restartCount`. Both suspecters had
  `restartCount=0` and were still running — neither was stopping.
- **Was a rollout in progress?** `kubectl get rs -n <ns>`. One active ReplicaSet, 5/5 ready, 22 h
  old, every other scaled to zero. No surge, so no shutting-down peer to blame.
- **Was it OOMKilled by the kernel?** No — and this is worth reading carefully, because the exit code
  lies about the cause. `lastState.terminated` showed `exitCode: 139, reason: Error`, and the dump
  header reads `signo 6` (SIGABRT), i.e. the `Environment.FailFast` from `FatalErrorHandler` — the
  self-kill the log already announced, not a segfault. See
  [Debugging Native Crashes](../DebuggingNativeCrashes).

## The kill is the cleanup, not the fault

The managed-heap trace is where the actual defect is:

| Time (UTC) | Managed heap | Longest stall in the window |
|---|---|---|
| 09-01 20:16 | 5 204 MB | 3.5 s |
| 09-02 02:22 | 9 767 MB | 4.6 s |
| 09-02 02:54 | 11 489 MB | 4.3 s |
| 09-02 03:11 | 11 850 MB | 6.3 s |

**No `DOTNET_GCHeapHardLimit`-family variable is set**, so .NET takes 75 % of the 16 GiB cgroup
limit → a **12 GiB managed hard limit**. 11 850 / 12 288 = 96.4 %. That is why 43 managed
`OutOfMemoryException`s appeared between 02:53:29 and 03:12:45 (the last one 7 s before the kill)
while the kernel never OOMKilled anything — the GC refused to grow past its own ceiling with ~1.4 GiB
of cgroup slack still free.

🚨 **Raising the limit is the band-aid here.** The heap grew ~6.6 GB in four hours; a bigger ceiling
buys hours, not a fix. The OOM sites already filed against that window —
[#3044](https://github.com/Systemorph/MeshWeaver/issues/3044),
[#3045](https://github.com/Systemorph/MeshWeaver/issues/3045),
[#3046](https://github.com/Systemorph/MeshWeaver/issues/3046),
[#3049](https://github.com/Systemorph/MeshWeaver/issues/3049) — are unbounded message/response
serialisation, which is both a consequence of the pressure and a plausible amplifier of it: an OOM
raised *inside* `ReportFailure` while reporting an OOM is a cascade.

### The same stall filed a second, unrelated-looking incident

A stalled host does not only fail Orleans probes. On this pod, in the same window, **89.4 % of all
`Npgsql.NpgsqlException` lines were emitted within 100 ms of a GC-stall report** — 294 of 329, in
windows covering 1.77 % of the log's span (≈ 50× enrichment). Those became
[#3050](https://github.com/Systemorph/MeshWeaver/issues/3050) /
[#3051](https://github.com/Systemorph/MeshWeaver/issues/3051), both of which concluded
"database-host unavailability" with high confidence. The database was fine; the connect timeout is
wall-clock and does not pause for GC, so a frozen client produces the identical exception.

🚨 **When a host stalls, expect a scatter of incidents that each name a different innocent
subsystem.** Before accepting any of them, check the pod's own stall lines in the same window. See
[An Unreachable Store Is Not a Refusal](../StoreUnreachableIsNotARefusal) for that measurement and
the reading rule it produces.

## Procedure

1. **Read the victim's own log first**, not the membership table. `LocalSiloHealthMonitor`
   complaints, `.NET Runtime Platform stalled`, `Thread Pool is exhibiting delays`, and the managed
   heap number those lines carry. A degraded self-score is the silo agreeing with its peers.
2. **Count the probe-failure targets on each suspecter** (above). One target ⇒ the target. Many ⇒
   membership/network.
3. **Check the suspecters' `restartCount` and the ReplicaSet state** before believing any
   shutting-down-peer or rolling-deploy story.
4. **Read the signal, not the exit code** — `signo 6` in the dump header is a `FailFast`, not a
   crash.
5. **Count the fingerprint's occurrences.** One occurrence with a full memory-pressure signature is
   an incident about the memory, not about membership. A recurrence *without* that signature is the
   sample that reopens the membership question — name it explicitly rather than closing on
   "could not reproduce".

## See also

- [The Pod-Hub Claim Must Be Re-Asserted](../PodHubClaimReassertion) — what a membership change
  costs the mesh once a silo really does leave
- [Pod-Hub Delivery Roll Plan](../PodHubDeliveryRollPlan) — the stranded-address shape during
  membership churn
- [Debugging Native Crashes](../DebuggingNativeCrashes) — reading exit codes, `createdump`, and why
  139 is not proof of a segfault
- [Error Propagation & Wedges](../ErrorPropagationAndWedges) — the accumulation shapes that grow a
  heap like this
