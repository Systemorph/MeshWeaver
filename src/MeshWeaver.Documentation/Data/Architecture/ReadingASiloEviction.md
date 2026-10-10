---
Name: Reading a Silo Eviction
Category: Architecture
Description: >-
  "I have been told I am dead" with a heartbeat newer than the suspect votes looks like a
  false-positive kill and usually is not. The two readings that make a correct eviction look wrong,
  the control arm that separates them, and the measured 2026-09-02 case.
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

`IAmAliveTime` is refreshed by a **single cheap row upsert on its own timer**, and one gap between
pauses is enough for it. A probe (`IMembershipService.Ping`) has to be **answered inside its timeout,
several times in a row**. Orleans 10 answers it in the connection's receive loop
(`SiloConnection.HandlePingMessage`), not in a grain turn (the second shape below turns on exactly
that), but the receive loop is ordinary managed code on thread-pool threads: it does not run while
the GC has the process suspended, and under pool delays it waits for a worker like everything else.
A process deep in GC-stall territory therefore does the first and not the second, so during a stall
the membership row keeps looking fresh while every probe times out. That is not a contradiction — it
is the *signature* of a stalled host.

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

## The second shape: a silo wedged from boot, with no stall at all

The 2026-10 recurrences (#6395 on memex, #6432 twice on memex-cloud) had **no** memory-pressure
signature, so they reopened the membership question (procedure step 5 below). They resolve the
same way, as a correct eviction of a silo that was not serving, but the evidence looks different.

**The boot fingerprint.** About 30 s after the silo starts, its own hosted client gets no answer
from a grain placed on **that same silo**:

```
Response did not arrive on time in '00:00:30' for message: 'Request
[S<ip> sys.client/hosted-<ip>]->[S<ip> messagehub/Agent/_Activity/import-manifest] … DeliverMessage'
```

The first minute then reads the same way every time. `GetMeshNode('Agent/_Activity/import-manifest')
timed out … NO LOCAL HUB … it never activated here`, the platform-startup `CreateNode` reports
"post-creation handlers had not finished within 30s", and the static-repo import fails partition
by partition. Measured: 3 of 3 evicted pods carried it. None of the healthy new pods in the same
rolls did; they created their import manifest within 3 s. The query is
`did not arrive on time.*_Activity/import-` over the roll window.

**Threads are consumed, not starved.** `[LIVENESS]` on the doomed pod keeps a 10.00 s gap and a
flat GC, so it shows no pause and no thread-pool stall, and `LocalSiloHealthMonitor` stays quiet.
But `poolThreads` climbs steadily (20 → 55 over 16 minutes on 5j9gx) while the work completed per
10 s collapses (about 500 against about 47 000 on a healthy pod in the same roll, which ran on 16
threads). That is what the counters establish: workers stay blocked long enough for the pool to
keep injecting new ones. It is **not** established that any individual thread never returns.
`poolThreads` is the number of workers the pool retains, so it stays high after a worker comes
back, and the completed count never reaches zero, so some work does finish. Whether the blocking
is permanent, and on what, is the question the heap dump below answers.

**Why it takes minutes, not seconds, to be evicted.** Orleans 10 answers a membership ping
**inside the connection's receive loop** (`SiloConnection.HandlePingMessage`). Every other message
goes through `MessageCenter.ReceiveMessage`, which runs synchronously on that loop and calls
`Catalog.GetOrCreateActivation`, where the grain is constructed. A silo whose activation or
dispatch path blocks therefore keeps passing probes on every connection whose loop has not yet hit
a blocking message. Meanwhile it answers nothing else in either direction, writes `IAmAlive` to
the membership table normally, and logs no connection error. The measured eviction times were 8,
15 and 20 minutes. A silo failing probes from the start would be gone in about 2 to 3 minutes
under the deployed `ProbeTimeout` of 15 s and `NumMissedProbesLimit` of 5.

Those two values are the portal host's own (`Memex.Portal.Distributed`, MeshWeaver.Plugins, with
indirect probes enabled), in its code since before any of the three incidents, #6395 included; the
running options were not read back from the pods. Core's
`ClusterMembershipTolerance` baseline (10 s × 3, added for #6395) is inserted first so that a host's
explicit configuration wins, so it changes nothing on these deployments; it applies to a host that
configures no membership options. #6395 was therefore not a silo evicted after 15 s of Orleans
defaults. It was this shape, evicted under the wider vote, and no tolerance setting addresses it.

So "requests reach it, nothing comes back" is **not**, by itself, evidence of a broken pod network
path. Check the thread trend first.

**The scatter it files.** Peers see 30 s timeouts against the silo: a stream `RegisterConsumer`
against a rendezvous grain on a remote silo (#6429) and grain-call cancellation batches that all
target one silo (#6392). Each names a different innocent subsystem, as in the stall case above.
Two more incidents from the same roll windows are **correlated, not established**: a placement
timeout (#6394) whose sample does not say which silo was chosen, and a memory-stream dequeue that
timed out while 5j9gx called a queue grain on another silo (#6431). Neither shows that this silo
received a response and failed to dispatch it. Count them as the same failure only once the heap
dump or the remote silo's own log shows the same path.

**What is still open.** Which call blocks is not in any log. The instrument is the `FailFast` heap
dump the self-kill writes (step 4). Read it with the governed `AnalyzeDump` action
([Debugging Native Crashes](../DebuggingNativeCrashes), "Reading a production dump"): filed on the
control instance against `Deployments/<id>` with `dumpPod` or `dumpAround` naming the evicted pod, it
runs `threadpool`, `syncblk`, `clrthreads`, `clrstack -all` and `dumpasync` in the cluster and lands
the grouped stacks at `Ops/Dumps/<action id>`. No direct cluster access is needed.

A self-kill does not always leave a dump, so check before filing. The pod's first `[crash-dumps]`
line after the roll says which it was: `armed` means the next crash writes one; `HELD: dumps are
DISABLED` means it wrote **nothing**, which is what every pod logs where the shared claim is smaller
than the dump gate requires. An instance whose dump root is still an `emptyDir` lost the dump with
the pod. In both cases there is nothing for `AnalyzeDump` to read from that incident: fix the
storage first (declare a `dumps` volume on the record and Reconcile; see Debugging Native Crashes),
and the next recurrence is the one that can be read. In the result, look for receive-loop threads (`SiloConnection`,
`MessageCenter.ReceiveMessage`) or activation threads parked on one lock or one `Wait`. Until it has
been read, any tolerance change or watchdog is a band-aid over a blocking call nobody has seen.

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
6. **No stall and no memory pressure? Look for the boot fingerprint** (above): unanswered
   `_Activity/import-*` deliveries to the silo's own grains in its first minute, and `poolThreads`
   climbing while throughput falls. Then read the `FailFast` dump (`AnalyzeDump`) for the blocked
   threads before touching membership options.

## See also

- [The Pod-Hub Claim Must Be Re-Asserted](../PodHubClaimReassertion) — what a membership change
  costs the mesh once a silo really does leave
- [Pod-Hub Delivery Roll Plan](../PodHubDeliveryRollPlan) — the stranded-address shape during
  membership churn
- [Debugging Native Crashes](../DebuggingNativeCrashes) — reading exit codes, `createdump`, and why
  139 is not proof of a segfault
- [Error Propagation & Wedges](../ErrorPropagationAndWedges) — the accumulation shapes that grow a
  heap like this
