---
Name: The Portal Heap Is Hubs
Category: Architecture
Description: Five heap dumps from a live memex-cloud replica: the retention is 9,386 MessageHub instances (89.6% sync/ stream hubs), 1,496 of them fully disposed corpses held by SynchronizationStream.Hub after the parent killed the hub under a stream that was never told; 45% of the live heap is per-hub Autofac and TypeRegistry metadata; the ALC, lazy-compile and GC-fragmentation candidates are all falsified.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><circle cx="5" cy="5" r="2"/><circle cx="19" cy="5" r="2"/><circle cx="5" cy="19" r="2"/><circle cx="19" cy="19" r="2"/><path d="M10 10L6.5 6.5"/><path d="M14 10l3.5-3.5"/><path d="M10 14l-3.5 3.5"/><path d="M14 14l3.5 3.5"/></svg>
---

# The Portal Heap Is Hubs

A `memex-cloud` portal replica grows ~215 MB/h, monotonically, through hundreds of full
collections, and never returns to an earlier floor. This page is the heap-dump evidence for
**what is actually in that memory**, taken read-only from a live production pod on
2026-09-04. It exists because the answer contradicts every hypothesis the investigation was
carrying, and because the measurement was expensive to assemble.

**One sentence:** the heap is 9 386 `MessageHub` instances — 89.6 % of them `sync/{clientId}`
stream hubs, of which **1 496 are fully disposed corpses held by the very streams that created
them** — and it is large because each hub carries its own copy of the framework's
dependency-injection and type-registry metadata; not because of assembly load contexts, not
because of Roslyn, and not because of GC fragmentation.

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

## Why the LIVE ones are never released

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

## …but that is only three quarters of the population

A third dump (23:03Z, same replica) read `MessageHub.runLevel` alongside the address. The
population is not homogeneous:

```
TOTAL MessageHub: 9398
    6925  sync  RunLevel=1Started
    1495  sync  RunLevel=6Dead
     974  <node-or-other>  RunLevel=1Started
       4  sync  RunLevel=0Starting
```

`Dead` is the enum's terminal state — *"the hub is fully disposed and inert"*
(`MessageHubRunLevel.cs:22`). **1 495 `sync/` hubs have completed their entire shutdown and are
still reachable**: ≈580 MB of corpses at ≈390 KB each. Unlike the `Started` population, that
admits no benign reading at all.

## Who holds a corpse

A fourth dump (23:06Z) and one full pass over all **51 562 911** objects, recording every referrer
of a `Dead` hub:

```
dead hubs = 1496
--- referrers of DEAD sync hubs ---
   19448  MessageHub+<>c__DisplayClass188_0          ⎫
   13464  MessageHub+<>c__DisplayClass59_0           ⎪ the hub's OWN internals —
    1496  MessageHubConfiguration                    ⎬ exactly 1496 of each,
    1496  MessageService                             ⎪ they die with it
    1496  HierarchicalRouting / RouteConfiguration   ⎭
    1485  MeshWeaver.Data.Serialization.SynchronizationStream<MeshWeaver.Mesh.MeshNode>
      11  MeshWeaver.Data.Serialization.SynchronizationStream<System.Text.Json.JsonElement>
       1  ConcurrentDictionary<Address, IMessageHub>+Node
--- GC roots pointing directly at a dead hub ---
direct-root hits = 0
```

Two readings, both decisive, and the first one corrects this page's own earlier section:

🚨 **`HostedHubsCollection.messageHubs` is not what holds the dead ones — it did its job.**
Exactly **one** of the 1 496 is still in a `ConcurrentDictionary<Address, IMessageHub>`. The
`RegisterForDisposal` removal at `:203` fired for the other 1 495. The registry is where the
**live** hubs sit (6 925 `sync` + 974 node, none of which anything ever asked to dispose); it is
not the corpse-holder.

🚨 **The corpse-holder is `SynchronizationStream<T>.Hub`.** 1 485 + 11 = **1 496 — one stream per
dead hub, exactly.** No GC root points at a dead hub directly (`direct-root hits = 0`); every one
is alive purely transitively, through the stream that created it.

## And the stream was never told

A fifth dump (23:11Z) read `isDisposed` off every stream:

```
SynchronizationStream total=8461 disposed=11
    4219  <MeshWeaver.Mesh.MeshNode>          live
    3193  <MeshWeaver.Data.EntityStore>       live
     992  <MeshWeaver.Data.InstanceCollection> live
      11  <System.Text.Json.JsonElement>      DISPOSED
```

**Only 11 of 8 461 streams are disposed.** `SynchronizationStream.Dispose()` sets `isDisposed`
*before* it disposes the hub, so a stream reading `false` never ran `Dispose()`. Put the two
measurements together:

> 🚨 **≈1 485 `SynchronizationStream<MeshNode>` instances are NOT disposed, and their `Hub` is
> `Dead`.** The hub was destroyed out from under a stream that still believes it is alive.

That is a defect with a name. A `sync/` hub is a *hosted* hub: its parent disposes it during
`DisposeHubsReactive` when the parent goes down — a Blazor circuit ending, a `DisposeRequest`, a
recycle — while the stream that created it is owned by a workspace somewhere else entirely and is
never notified. The stream keeps `isDisposed == false`, keeps a strong reference to the corpse and
everything the corpse roots (its Autofac `ILifetimeScope`, its `TypeRegistry`), and keeps being
handed out as usable.

The codebase already knows the *creation-time* half of this hazard — `SynchronizationStream.cs`
refuses to hand out a stream whose host is winding down, because *"a consumer cannot even detect
the corpse: `ISynchronizationStream` exposes no liveness/disposal member"*, and that cost a
production NRE. **The post-creation half — the hub dying under a stream already handed out — has
the same root and is not covered.**

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

## Three defects, not one

They are independent, and conflating them is how this stayed open.

1. **A hosted hub can die under a stream that is never told.** 1 496 `Dead` hubs, each held by an
   undisposed `SynchronizationStream<T>.Hub` — ≈580 MB of pure corpse, and the only one of the
   three that is unambiguous. `ISynchronizationStream` exposes no liveness member, so neither the
   stream nor its consumers can even detect the state.

2. **`sync/` hubs that are still `Started` are never released** — 6 925 of them, ~7 per node hub.
   The same objects supply the 45 s heartbeat that pins their owning grain, so this defect is
   *both* memory and the reason nothing deactivates: the lifetime decision has exactly one input,
   and it can only ever vote "keep". Whether all 6 925 are garbage is the open question below.

3. **A hub costs ~390 KB of retained heap, ~173 KB of it duplicated framework metadata.** A
   constant factor, but a large one: even a *legitimate* 962-node working set costs
   962 × 9.75 × 390 KB ≈ 3.5 GB, which does not
   fit a 12 GiB ceiling with room to serve. Each child `ILifetimeScope` wraps 262 parent
   registrations in `ExternalComponentRegistration`, each with its own ~4-stage resolve pipeline.
   Fixing (1) and (2) alone leaves a portal whose working set is set by its DI cost per actor.

🚨 **None of the three is fixed by raising the memory limit, lowering the GC hard limit, recycling pods on a
timer, or deleting pods by hand.** The pod-deletion stopgap has been applied at least four times
and is recorded as a stopgap.

## What is still open, and the next measurement

Five dumps settle *what* is retained, *which* hubs, and *who* holds the dead ones. Two things are
still inference rather than measurement, and both are named here so the next reader does not have
to re-derive the whole chain.

**1. What kills the 1 496 sync hubs.** Measured: they are `Dead` while their stream is not
disposed, so the kill did not come through `SynchronizationStream.Dispose()`. Inferred: it came
from the parent, via `HostedHubsCollection.DisposeHubsReactive` — a circuit ending, a
`DisposeRequest`, a recycle. **The confirming measurement is a log correlation, not a dump**: count
`[DISPOSE-CONTAINER] {Address}: lifetime scope closed` (`HostedHubsCollection.cs:338`, Debug) for
`sync/` addresses over a window and compare it with the growth in the `Dead` population. If they
match, the parent-teardown path is confirmed and the fix belongs where a hosted hub's death is
announced to whoever created it.

**2. Whether the 6 925 `Started` sync hubs are garbage at all.** They are in the registry, nothing
asked them to dispose, and their streams are live. That is *consistent with correct behaviour* —
8 450 undisposed streams for subscribers that really are attached. It is also consistent with
streams abandoned without `Dispose()`. **The discriminator is a referrer walk on the `Started`
population's streams** (the same one-pass ClrMD technique used above, retargeted): if they are
held by `Workspace._localStreamCache` / `_remoteStreamCache` entries whose subscribers are gone,
they are garbage; if they are held by live circuits, the portal's working set is simply larger
than its ceiling and the per-hub cost below is the whole story.

🚨 **Do not skip to a fix on the strength of the corpse count alone.** 1 496 dead hubs is ≈580 MB
— real, unambiguous, and worth fixing — but it is **one sixth** of the 9 398. Fixing only that
leaves ~2.7 GB in the `Started` population untouched, and (2) is what decides whether that
remainder is a leak or a sizing problem.

A third reading worth having: the growth *rate* of `MessageHub` count against replica age, on three
replicas of different ages. #3321 observed that working set tracks uptime rather than load — the
near-idle `fxbhd` holds 11 443 MiB on 512 log lines per 30 min while the busiest replica holds
4 134 MiB. The +7-in-22-minutes reading above refines that rather than contradicting it: hubs
arrive with load and never leave, so an idle pod *holds* its history without adding to it.

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
