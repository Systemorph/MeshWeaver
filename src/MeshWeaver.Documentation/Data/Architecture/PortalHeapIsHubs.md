---
Name: The Portal Heap Is Hubs
Category: Architecture
Description: Heap-dump evidence that the memex-cloud retention leak is 9,386 undisposed MessageHub instances — 89.6% of them sync/ stream hubs — that 45% of the live heap is per-hub Autofac and TypeRegistry metadata duplicated once per hub, and that the ALC, lazy-compile and GC-fragmentation candidates are all falsified by measurement.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><circle cx="5" cy="5" r="2"/><circle cx="19" cy="5" r="2"/><circle cx="5" cy="19" r="2"/><circle cx="19" cy="19" r="2"/><path d="M10 10L6.5 6.5"/><path d="M14 10l3.5-3.5"/><path d="M10 14l-3.5 3.5"/><path d="M14 14l3.5 3.5"/></svg>
---

# The Portal Heap Is Hubs

A `memex-cloud` portal replica grows ~215 MB/h, monotonically, through hundreds of full
collections, and never returns to an earlier floor. This page is the heap-dump evidence for
**what is actually in that memory**, taken read-only from a live production pod on
2026-09-04. It exists because the answer contradicts the two hypotheses the investigation was
carrying, and because the measurement was expensive to assemble.

**One sentence:** the heap is 9 386 `MessageHub` instances that were never disposed — 89.6 % of
them `sync/{clientId}` stream hubs — and it is large because each one carries its own copy of the
framework's dependency-injection and type-registry metadata; not because of assembly load
contexts, not because of Roslyn, and not because of GC fragmentation.

## The specimen

`memex-cloud/memex-portal-deployment-5b9cbf46dd-gs6j6`, 26 h old, working set 6 859 MiB,
container limit 16 Gi. A `dotnet-dump collect --type Heap` through an ephemeral
`--profile=sysadmin` container (the recipe is in
[Debugging Disposal, Storms and Leaks](/Doc/Architecture/DebuggingDisposalAndLeaks)), then
`dumpheap -stat`. The dump file was deleted from the pod afterwards; no cluster object was
mutated.

```
Total 49,827,806 objects, 5,544,881,838 bytes
Free                                            411,422    2,031,989,944
```

**≈3.51 GB live, 2.03 GB free.** The heap is not mostly garbage awaiting a collection.

## What is in it

The exact hub count, from the dump:

```
MT               Count      TotalSize  Type
7f9d080a7478     9,386      2,928,432  MeshWeaver.Messaging.MessageHub
7f9d0953b320       962        123,136  MeshWeaver.Hosting.Orleans.MessageHubGrain
```

**9 386 live hubs against 962 grain activations.** `dumpobj` segfaults SOS on this dump, so the
addresses were read with a 40-line ClrMD walk instead (`MessageHub` → `Configuration` → `Address`
→ `Segments[0]`), run inside the same ephemeral container against a second dump taken 22 minutes
later (22:32:46Z → 22:55:16Z):

```
TOTAL MessageHub: 9393
--- by address type segment ---
    8419  sync
     184  MeshWeaver
     144  Ifrs17
      67  Crm
      61  Underwriting
      47  Planning
      26  ReinsuranceDemo
      …  (the tail is partition names — node hubs)
--- sample full addresses ---
   sync/oeTOuFSg1kSZgDMysMaQ4Q
   sync/QP8kKCTa2k-bgSYV8-5moQ
   Plugins/OpenAI
```

🚨 **8 419 of 9 393 hubs — 89.6 % — are `sync/{clientId}`.** The ~974 remaining are node hubs
spread across partitions, which matches the 962 `MessageHubGrain` activations almost exactly. So
the grain-hosted working set is *not* what grows: **each node hub carries ~8.6 synchronization
sub-hubs, and those are the heap.** At the ≈390 KB a hub retains (below), 8 419 of them is
**≈3.2 GB of the 3.51 GB live heap**.

⚠️ **One honest caveat from the pair of dumps: creation is bursty, so do not read this curve as
purely uptime-driven.** 9 386 → 9 393 is **+7 hubs in 22 minutes** (≈19/h) — while the pod's
lifetime average is 9 386 over 26 h ≈ **361/h**. Both dumps were taken at ~22:40 UTC, off-hours.
So hubs arrive with *load* and then never leave; the memory curve looks uptime-driven only because
the leaving half is missing. That distinction matters for the fix: nothing here is a background
timer minting hubs on an idle pod.

The `MessageHub` objects themselves are 2.9 MB. Everything below is what they *root*:

| Type | Count | Bytes | Per hub |
|---|---:|---:|---:|
| `Autofac.Core.Resolving.Pipeline.MiddlewareDeclaration` | 10,001,898 | 480 MB | 1,066 |
| `Autofac.Core.Registration.ExternalComponentRegistration` | 2,455,373 | 275 MB | 262 |
| `Autofac.Core.Resolving.Pipeline.ResolvePipelineBuilder` | 2,534,453 | 101 MB | 270 |
| `Autofac.Core.Service[]` | 2,534,378 | 81 MB | 270 |
| `ConcurrentQueueSegment<IComponentRegistration>+Slot[]` | 56,330 | 83 MB | — |
| `ExternalComponentRegistration+NoOpActivator` | 2,455,373 | 59 MB | 262 |
| `Autofac.Core.IComponentRegistration[]` | 278,690 | 35 MB | 30 |
| `System.Action<ResolveRequestContext>` | 508,761 | 33 MB | 54 |
| `Autofac.Core.Registration.ServiceRegistrationInfo` | 255,878 | 24 MB | 27 |
| `List<Autofac.Core.IComponentRegistration>` | 434,807 | 14 MB | 46 |
| **Autofac subtotal** | | **≈1.17 GB** | **≈128 KB** |
| `MeshWeaver.Messaging.Serialization.TypeDefinition` | 848,844 | 75 MB | 90 |
| `ConcurrentDictionary<String,TypeDefinition>+Node` | 1,261,018 | 60 MB | 134 |
| `System.Func<String>` / `System.Func<KeyFunction>` | 1,695,786 | 108 MB | 181 |
| `System.LazyHelper` | 1,696,944 | 54 MB | 181 |
| `Lazy<String>` / `Lazy<KeyFunction>` | 1,695,767 | 68 MB | 181 |
| `TypeDefinition+<>c__DisplayClass2_0` | 846,918 | 27 MB | 90 |
| `ConcurrentDictionary<String,TypeDefinition>+VolatileNode[]` | 18,860 | 20 MB | — |
| **TypeRegistry subtotal** | | **≈0.41 GB** | **≈45 KB** |

**≈1.58 GB of a 3.51 GB live heap — 45% — is framework metadata duplicated once per hub.**
262 component registrations and ~90 type definitions, identical in content, held 9 386 times.
Nothing in that table is application data. Averaged over the whole live heap a hub retains
**≈390 KB**, of which **≈173 KB is this duplicated metadata**.

The rest of the live heap is the same shape one level up: `LayoutAreaDefinition` 61,610 objects
(7.9 MB) and `ObservableRenderer` 65,279 (4.2 MB) against 2,473 `LayoutDefinition` — the layout
catalogue, re-materialised per hub.

## The retaining root

`HostedHubsCollection.messageHubs` — `src/MeshWeaver.Messaging.Hub/HostedHubsCollection.cs:29`:

```csharp
private readonly ConcurrentDictionary<Address, IMessageHub> messageHubs = new(AddressComparer.Instance);
```

It has **no idle sweep, no TTL, no size cap and no LRU**. The one removal is on the hub's own
disposal (`:203`):

```csharp
hub.RegisterForDisposal(h => messageHubs.TryRemove(h.Address, out _));
```

So the chain is: an entry leaves iff the hub disposes; a grain-hosted node hub disposes iff
`MessageHubGrain.OnDeactivateAsync` runs (`src/MeshWeaver.Hosting.Orleans/MessageHubGrain.cs:890-892`
— `hub.CancelCurrentExecution(); hub.Dispose();`); and that method logged its own first line
(`:864`, `LogInformation`, *"Grain {GrainId} deactivating: reason={Reason}"`*) **zero times across
five replicas and 514 687 log lines in 28 hours**, in a window carrying 2 791 other `info:` lines.

Deactivation never happens because the activation is re-pinned faster than it can age out.
`MessageHubGrain.cs:560` installs

```csharp
.Set(new GrainKeepAliveCallback(() => TryDelayDeactivation(TimeSpan.FromMinutes(10))))
```

and every live sync stream fires a `HeartBeatEvent` at it every **45 seconds**
(`src/MeshWeaver.Data/Serialization/SyncStreamOptions.cs:18` →
`JsonSynchronizationStream.cs:1062` → `MeshExtensions.cs:537` `callback.KeepAlive()`). A 45 s
heartbeat against a 10-minute window is a **13× over-provision**, and because the heartbeat is
itself a routed grain call it also resets Orleans' own idle clock independently. Orleans'
defaults (`CollectionAgeLimit` 2 h) are never configured anywhere in `src/` or `deploy/` and are
never reached.

The dump shows what is doing the heart-beating: **7 396 live synchronization streams**
(`SynchronizationStream<MeshNode>` 4,205 + `SynchronizationStream<EntityStore>` 3,191) against
**976 `MeshNodeStreamCache+Entry`**. The stream cache's idle sweep works and is bounded; the
streams that outlive it are not.

🚨 **The heartbeat is evidence that a transport is alive, not that anyone wants the data.** It is
today the only input to the lifetime decision, and it can only ever vote "keep".

## What this falsifies

Three explanations were live before the dump. All three are refused by measurement.

**Collectible AssemblyLoadContexts accumulating because nothing deactivates.** The reasoning was
sound — a lifetime lease is taken per instance hub
(`src/MeshWeaver.Graph/MeshDataSource.cs:1016-1036`) and `NodeAssemblyLoadContext.Dispose` defers
`Unload()` while any lease is held. But the count is not there:

```
dotnet.assembly.count ({assembly})   451     (gs6j6, 6.9 GB)
dotnet.assembly.count ({assembly})   475     (5nqbz, 14.0 GB)
```

450 distinct `.dll` mappings in `/proc/1/maps`. **The fattest replica carries 24 more assemblies
than the thin one, not thousands more.** Whatever the ALC lease costs, it is not this heap.

**Lazy first-compile from prebuilt-bundle coverage failure.** Same refutation, same numbers — 368
uncovered NodeTypes cannot be a 10 GB retainer at 451 loaded assemblies. It remains a real CPU
cost and `Modules:RequirePrebuilt` remains the right lane for it; it is not the memory.

**GC fragmentation, or committed memory the runtime declines to return.** Plausible on the thin
replica (gen2 fragmentation 537 MB, LOH 1.27 GB) — and decisively false on the fat one. From
`5nqbz` at 14.0 GB:

```
dotnet.gc.last_collection.heap.size (By)[gen2]           10,251,252,048
dotnet.gc.last_collection.heap.fragmentation.size[gen2]       1,075,128
dotnet.gc.last_collection.memory.committed_size          12,391,981,056
```

**10.25 GB of gen2 with 1.07 MB of free space in it — 0.01 %.** That is live, reachable data
sitting at the 12 GiB container-derived hard limit. It answers the question the issue asked
head-on: *267 full collections free nothing because there is nothing to free.*

## Why it presents as CPU starvation rather than a restart

Nothing sets `DOTNET_GCHeapHardLimit[Percent]`, so .NET's container default gives the managed
heap 75 % of the 16 Gi limit — 12 GiB. The runtime defends that ceiling with back-to-back
blocking gen2 rather than letting the kubelet restart the container: 0 OOMKills across 7 replicas
over 28 h against a GC pause share of 0.66 sustained for 2 h 15, with `/alive` answering
`Healthy` throughout. A memory shortage is something Kubernetes fixes in one second; CPU
starvation inside a process that still answers TCP is something it has no opinion about.

## Two defects, not one

They are independent, and conflating them is how this stayed open.

1. **`sync/` hubs are never released** — 8 419 of 9 393, ~8.6 per node hub. Unbounded growth, and
   the same objects supply the 45 s heartbeat that pins their owning grain, so this one defect is
   *both* the memory and the reason nothing deactivates. The lifetime decision has exactly one
   input — a heartbeat that can only ever vote "keep". This is what makes the curve monotone.

2. **A hub costs ~390 KB of retained heap, ~173 KB of it duplicated framework metadata.** A
   constant factor, but a large one: even a *legitimate* 962-node working set costs
   962 × 9.75 × 390 KB ≈ 3.5 GB, which does not
   fit a 12 GiB ceiling with room to serve. Each child `ILifetimeScope` wraps 262 parent
   registrations in `ExternalComponentRegistration`, each with its own ~4-stage resolve pipeline.
   Fixing (1) alone leaves a portal whose working set is set by its DI cost per actor.

🚨 **Neither is fixed by raising the memory limit, lowering the GC hard limit, recycling pods on a
timer, or deleting pods by hand.** The pod-deletion stopgap has been applied at least four times
and is recorded as a stopgap.

## What is NOT yet answered, and the next measurement

The address histogram settles *which* hubs (`sync/`, 89.6 %). It does not settle **why each one
outlives its subscriber**, and that is the whole remaining question — because the disposal code is
correct. `SynchronizationStream.Dispose()` completes the store, walks its own composite
synchronously and then disposes its hub
(`src/MeshWeaver.Data/Serialization/SynchronizationStream.cs:2198-2251`):

```csharp
if (Hub is not null && Hub.RunLevel <= MessageHubRunLevel.Started)
    Hub.Dispose();
```

So a sync hub survives only if (a) its stream is still referenced by something that never lets go,
or (b) `Dispose()` ran but the hub's *shutdown never completed* — `MessageHub.Dispose()` posts
`ShutdownRequest(Quiescing)` and returns, and the `RegisterForDisposal` callback that removes the
entry from `messageHubs` runs in the ShutDown phase, several action-block turns later, bounded by
nothing. **These are different defects with different fixes, and the dump cannot tell them apart**
— a hub in state (b) and a hub in state (a) look identical in a type histogram.

**The next measurement discriminates them in one pass, and it is a small extension of the same
ClrMD walk:** for every `sync/` hub, read `MessageHub.RunLevel` (and `isDisposed` on its stream)
and histogram *that*.

- A large population at `RunLevel >= ShutDown`/`Disposing` ⇒ **(b)**: disposal was requested and
  never finished. That is a teardown-completion defect in `MessageHub`, and it is bounded — the
  fix is that the registry entry is released on the *request*, not on the completion.
- A large population at `RunLevel == Started` with a live stream ⇒ **(a)**: nothing asked. The
  question then moves to who still holds the stream, and the answer is a `gcroot`-equivalent walk
  from one such stream back to a GC root — also ClrMD, since SOS `gcroot` will crash the same way
  `dumpobj` does.

🚨 **Do not skip to a fix on the strength of the histogram alone.** "89.6 % are `sync/`" names the
population; it does not name the defect, and the two candidates above call for opposite changes.
The counted, uncomfortable possibility is that a large share is *neither* — legitimately live
streams for subscribers that really are still attached, in which case the portal's working set is
simply larger than its ceiling and defect (2) is the whole story.

A second, independent reading worth having alongside it: the growth *rate* of `MessageHub` count
against replica age, taken on three replicas of different ages. #3321 observed that working set
tracks uptime rather than load — the near-idle `fxbhd` holds 11 443 MiB on 512 log lines per
30 min while the busiest replica holds 4 134 MiB. The +7-in-22-minutes reading above refines that
rather than contradicting it: hubs are created by load and simply never removed, so an idle pod
*holds* its history without adding to it. A per-replica hub count against age would confirm that
directly, and it is the cheapest way to tell a genuine leak from a working set that is merely
larger than its ceiling.

## Reading the counters yourself

The whole discrimination above is four counters, and they need no dump — `dotnet-counters collect
-p 1 --format csv System.Runtime` inside an ephemeral `--profile=sysadmin` container with
`TMPDIR=/proc/1/root/tmp`:

| Counter | What it settles |
|---|---|
| `dotnet.gc.last_collection.heap.size[gen2]` | how much survives a full collection |
| `dotnet.gc.last_collection.heap.fragmentation.size[gen2]` | retention vs. fragmentation — the whole question |
| `dotnet.assembly.count` | whether ALCs are accumulating (they are not) |
| `dotnet.timer.count` | a cheap proxy for live hubs: 11,387 at 6.9 GB, 37,796 at 14.0 GB |

🚨 **`dotnet-gcdump` is the wrong tool here and will mislead you.** Its walk stopped at exactly
10 000 000 objects on this process and reported a 568 MB heap — a sixth of the truth — with a type
table skewed to whatever it reached first. The `Total … objects` line from `dumpheap -stat` is the
number to trust.

🚨 **And SOS `dumpobj` / `gcroot` segfault on this process's dumps**, so anything that needs to
*follow a field* has to be ClrMD. That is not a hardship: the SDK image the debug container runs
has full internet, so `dotnet new console` + `dotnet add package Microsoft.Diagnostics.Runtime`
+ `dotnet run -- <dump>` works inside the pod, start to finish, in about ten seconds. The walk that
produced the address histogram above is forty lines
(`MessageHub` → `<Configuration>k__BackingField` → `<Address>k__BackingField` →
`<Segments>k__BackingField[0]`), and reading fields *by name-substring* off `ClrType.Fields` keeps
it robust against the compiler's backing-field spelling.

## Related

- [Debugging Disposal, Storms and Leaks](/Doc/Architecture/DebuggingDisposalAndLeaks) — the profiling recipe and the ClrMD root-chasing method.
- [Hub Disposal Model](/Doc/Architecture/HubDisposalModel) — what disposal means for a hub, and the RunLevel phases.
- [Mesh Node Stream Cache](/Doc/Architecture/MeshNodeStreamCache) — the idle sweep that *is* bounded, and its "never release an entry with a live subscriber" contract.
- [Dead Circuit Fan-Out Storm](/Doc/Architecture/DeadCircuitFanOutStorm) — the sibling failure where a departed subscriber keeps costing.
- [Actor Model](/Doc/Architecture/ActorModel) — why there is one hub per addressable thing in the first place.
