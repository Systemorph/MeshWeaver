---
Name: Peer Death Is Dated by Its Exception Class
Category: Architecture
Description: "A silo that is frozen and a silo that is gone produce DIFFERENT Orleans exceptions, so the changeover between them timestamps the crash to the second — measured on 2026-09-17, when one pod's SIGSEGV was filed as three separate issues because each replica's view of it was fingerprinted on its own."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><polyline points="12 6 12 12 16 14"/><path d="m4.9 4.9 14.2 14.2"/></svg>
---

# Peer death is dated by its exception class

> **A peer silo that is FROZEN and a peer silo that is GONE fail differently, and the changeover
> between the two exception classes timestamps the death to the second.** A frozen process still
> holds its sockets open, so calls to it hang and die on a client-side timeout. A dead process has
> had its sockets closed by the kernel, so calls to it are refused immediately. When a log shows
> `TimeoutException` stopping and `SiloUnavailableException` / `ConnectionFailedException` starting,
> the boundary between them is the moment the peer's process actually exited — and everything before
> it is the peer dying, not a defect where the log site is.

This is the discriminator that settles "why did the silo drop?", and it is available from an ordinary
red-log feed with no cluster access at all.

## The measured case, 2026-09-17

Three issues were opened against the control instance `memex` (memex.systemorph.com) for what turned
out to be one pod's crash. The deployment ran two replicas of
`meshweaver.azurecr.io/memex-portal-ai:3.0.0-ci.8710`:

| | pod | node | Orleans silo |
|---|---|---|---|
| **A** | `…-6c967c7c4c-2k4xb` | `aks-silos-10171001-vmss00001u` | `10.244.5.183:11111` gen `148598547` |
| **B** | `…-6c967c7c4c-kdff5` | `aks-silos-10171001-vmss00001x` | `10.244.3.244:11111` gen `148598327` |

B logs its own address as the sender on every failure line, so **B = 10.244.3.244 is measured**.
A's identity is one inference step, and the pod CIDRs settle it: `Ops/Status/memex` (scraped,
`notScraped: false`) shows the *replacement* pods as `10.244.5.112` on node `…1u` and `10.244.3.205`
on node `…1x`, so node `…1u` carries `10.244.5.0/24` and node `…1x` carries `10.244.3.0/24`. B on
`…1x` is `10.244.3.244`, consistent; A on `…1u` must be a `10.244.5.x`, and with exactly two replicas
the only other silo in the namespace is `10.244.5.183`. **A = 10.244.5.183.**

### The timeline

| UTC (2026-09-17) | Event | Basis |
|---|---|---|
| ~18:36:35 | **A takes a SIGSEGV**; `createdump` suspends every thread and begins writing | inferred — 30 s before the first logged placement timeout |
| 18:37:05.302 | first of **8 `TimeoutException: Grain placement operation timed out`** on B, firing at 18:37:05 / 18:37:35 / 18:38:05 — one Polly 30 s budget apart | measured |
| 18:38:04 | **A's container exits 139** (`reason=Error exitCode=139`) | measured (`kubectl describe`) |
| 18:38:05.833 | **last** placement timeout on B | measured |
| 18:38:07.368 | **first** `ConnectionFailedException: Unable to connect to S10.244.5.183` on B | measured |
| 18:38:07.768–770 | **13 `SiloUnavailableException`** on B, deregistering `MessageHubGrain` activations from A's directory partition | measured |
| ~18:38:49 | A's container restarts in place — same pod IP, **new silo generation `148675152`** | inferred from generation arithmetic |
| 18:38:07 → 19:19:50 | 39 further connect failures — A never becomes reachable again for **41 minutes** | measured |
| 18:56:32 | **B takes a SIGSEGV** | measured — its dump is named `coredump.1.1789671392`, and that suffix is a Unix timestamp |
| 18:57:49 | B's container exits 139; a 12.8 GB dump lands on the data volume | measured |
| 19:19:20 → 19:19:41 | **A crashes a SECOND time — `signo 6` (SIGABRT), not SIGSEGV**; 5.2 GB written in 22.75 s, `Target process is alive` | measured (A's own log) |
| 19:19:50 | B's last complaint about A — 9 s after A's second dump completed | measured |
| 19:40:23 / 19:42:16 | replacement pods start on `3.0.0-ci.8844` | measured (`Ops/Status/memex`) |

Two things in that table were not in any of the three issues. **A crashed twice**, and its second crash
was a *different signal* — `SIGABRT`, which is a `FailFast`/abort path, not the `SIGSEGV` the issues
describe. And B's 41 minutes of connect failures end within ten seconds of that second dump, so the
"A never came back" stretch has an ending as well as a beginning.

🚨 **The retrospective window was reachable, and assuming otherwise would have lost all of this.**
Fleet Loki retention [has been measured as short as one hour](/Doc/Architecture/LogEntriesAreAQueryResult),
which is a good reason to distrust a zero — but it is not a reason to skip the query. Filtering a
`Logs` action to a pod that **no longer exists** makes every returned line in-window by construction
and makes a zero a clean negative; asked that way on 2026-09-18, this deployment still held pod A's
lines back to **06:14:24Z on 09-17**, nearly 24 hours. Measure the coverage; do not assume it.

The same probe shows this instance had also written core dumps on **2026-09-14 at 20:24:45Z**, from
an older replica set — so the 09-17 pair is a recurrence, not a first.

**Read the middle three rows together.** The timeouts stop at 18:38:05.833. The container exits at
18:38:04. The refusals start at 18:38:07.368. One class of failure ends and another begins across
the exit second, and neither class appears on the other side of it. That is not a coincidence to be
argued about — it is the two mechanisms, and it dates A's death without any cluster read.

### What each issue was actually looking at

- The placement timeouts are **B watching A freeze**. `createdump` suspends every thread in the
  target process but the kernel keeps its listening sockets, so Orleans' directory lookup for the
  grain connects and then waits forever, and Polly cuts it at 30 s. The grain named in the timeouts
  is incidental — it is whichever grain happened to need a directory hop into A's partition.
- The deregistration failures are **B watching A's socket close**. The batch was B's own routine
  idle-activation collection; only its *failure* came from A, and it failed at the first instant that
  failing was possible.
- The crash is the event. Both other log sites are downstream of it and neither can be fixed where it
  is logged.

🚨 **A consequence for automated triage.** Each replica fingerprints its own view, so one crash
produced three issues, two of them filed against the surviving pod. An incident whose `pods:` list
names only the *survivor* is a strong hint that the subject is the pod that is missing — a dying
process logs least about its own death.

## The crash itself is a known fingerprint — and it had never been seen in production before

🚨 **Do not re-theorise this crash. Compare its fingerprint first.**
[Debugging Native Crashes](/Doc/Architecture/DebuggingNativeCrashes) already carries eight CI
sightings between 2026-08-06 and 2026-08-18 of one fault: the GC heap walk dereferencing an object
whose **MethodTable word is exactly zero**.

```
si_signo=11  si_code=1 (SEGV_MAPERR)  si_addr=0x0   TRAPNO=14  ERR=0x4  CR2=0x0
```

What the crashed portal wrote was `[createdump] … signo 11 (000b) code 0001 errno 0000 addr (nil)`
— **`si_signo`, `si_code` and `si_addr` all match**, which is three of the four recorded fields. The
remaining discriminators (`TRAPNO`, `ERR`, `CR2`, and the `RIP` RVA that should land in
`background_sweep` or `plan_phase`) were not extracted when the issue was filed, and the dump is
still on the instance's `memex-data` PVC. **Getting them is the whole next step**, and that page
gives the recipe.

If they match, this is not a new defect: it is the CI crash family arriving on a **production
portal** for the first time, and it should be tracked there rather than re-diagnosed.

### The collectible-ALC reading is the wrong one here, and the reason is in the address

It is tempting — and it is what we reached for first — to blame ALC churn, because the workload
supports it spectacularly. `NodeTypeRelease` nodes carry a store-wide `assemblyStoreVersion`, and
each is a distinct compiled assembly (the artifact path embeds it, `Essentials_Email/v4136-…dll`,
and consecutive releases of one NodeType carry different content hashes and *non-consecutive*
numbers, so other NodeTypes consumed the numbers between):

| Release | UTC | `assemblyStoreVersion` | rate since previous |
|---|---|---|---|
| `Essentials/OperationRequest/…145447` | 14:54:47 | 2731 | — |
| `Essentials/Email/…181121` | 18:11:21 | 4136 | **1,405 assemblies in 3 h 16 m — one every 8.4 s** |
| `Essentials/Email/…185613` | 18:56:13 | 4150 | 14 in 45 m (A already dead) |
| `Essentials/Email/…193655` | 19:36:55 | 4192 | 42 in 41 m |

`Essentials/Email` alone was recompiled **47 times on 2026-09-17** — against a maximum of **seven**
on any other day back to 08-30, and **zero on 09-16** (counted over the complete 79-release history
of that NodeType, `truncated: false`). Every publishing build calls
`EvictSupersededContexts(nodeName, keep: ctx)` → `UnloadContext` → `NodeAssemblyLoadContext.Dispose()`
→ `CompleteUnload()` → `Unload()`, so the portal was minting and retiring collectible load contexts
at roughly one every eight seconds for hours before it died.

🚨 **That is the workload, not the cause — and the dump forensics say so directly.** The RCA rejects
the collectible-ALC reading three separate times, and the decisive ground is the faulting address:
**a freed `LoaderAllocator` yields a non-null *unmapped* pointer, while this fault is at exactly
`0x0`.** The 2026-08-17 sighting had eleven collectible NodeType ALCs live at the moment of the
fault, and the page records the verdict plainly — *"that is the workload that grows gen2 free lists,
not the cause"*. A high recompile rate is therefore a reason this instance faults sooner than a quiet
one, not an explanation of the zero.

What actually zeroes the word is **explicitly unsettled** in that page, across all eight sightings.
Anyone reopening this should start from its table of shapes, not from a fresh theory.

> The standing guidance that a SIGSEGV in this codebase is *"a disposal overlapping a new instance,
> not a runtime fault"* is about where to look for the **cause**; it does not convert this
> `si_addr=0x0` fingerprint into a dangling-pointer use-after-unload, which is the one reading the
> dumps have repeatedly excluded. Hold both: a disposal race remains a candidate for *what performs
> the errant zero store*; a stale pointer *into an unloaded ALC* is not what faulted.

### The separate, real finding: this instance's recompile rate

Independent of the crash, one NodeType recompiling 47 times in a day — and the store taking 1,405
new assemblies in three and a quarter hours — is worth a question of its own. A `NodeType` rebuilds
when any node in its `sourceVersions` set moves, and that set is wide (the `Essentials/OperationRequest`
release of 14:54:47 lists about fifty source nodes, many of them under `Store/**`), so a single
busy source fans out across every NodeType that names it.

## How to use this

1. When a silo "drops", find the two exception classes and the second between them before reading any
   log site as a defect. `TimeoutException` on one side and `SiloUnavailable`/`ConnectionFailed` on
   the other brackets the exit.
2. Identify the peer by **pod CIDR**, not by guessing: a scraped `Ops/Status/<deployment>` gives
   pod→node→IP for the live replicas, and each node owns one pod subnet.
3. Treat an incident whose `pods:` names only surviving replicas as a report *about* the missing one.
4. Read the silo **generation** in the address (`…:11111:148598547`): it is allocated at silo start,
   so a changed generation at an unchanged IP is a container restart in place, and the difference
   between two generations is the difference between their start times in seconds.
5. To get a dead pod's own account, aim a `Logs` action at its **pod name**. A pod that no longer
   exists cannot emit new lines, so every row is in-window by construction, nothing the probe itself
   logs can match it, and a zero is a clean negative. Narrow the `query` to the header lines
   (`NT_SIGINFO|Dump successfully written|Writing minidump` returns three rows per dump) — a bare
   `.` spends the whole 300-line budget on one dump's `ReadVirtual` noise.
6. A `createdump` file name ends in a **Unix timestamp** (`coredump.1.1789671392` = 18:56:32Z), so it
   dates the fault itself, which is earlier than the container's `finishedAt` by however long the
   dump took — 79.7 s for a 12.8 GB dump here. During that write every thread is suspended and the
   sockets stay open, which is precisely the interval the peer spends timing out.

Related: [Debugging Native Crashes](/Doc/Architecture/DebuggingNativeCrashes) ·
[Log Entries Are a Query Result](/Doc/Architecture/LogEntriesAreAQueryResult) ·
[NodeType Compilation](/Doc/Architecture/NodeTypeCompilation) ·
[Operating from the Portal](/Doc/Architecture/OperatingFromThePortal)
