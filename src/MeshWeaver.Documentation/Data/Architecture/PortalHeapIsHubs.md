---
Name: The Portal Heap Is Hubs
Category: Architecture
Description: >-
  Five heap dumps from a live replica of the public instance: the retention is 9,386 MessageHub instances (89.6%
  sync/ stream hubs), 1,496 of them fully disposed corpses held by SynchronizationStream.Hub after the
  parent killed the hub under a stream that was never told; 45% of the live heap is per-hub Autofac
  and TypeRegistry metadata; the ALC, lazy-compile and GC-fragmentation candidates are all falsified.
  Also: taking such a dump is NOT free — a --type Heap dump freezes a 6.6 GiB replica for 106 s,
  longer than the 90 s liveness budget, so it restarts the container.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><circle cx="5" cy="5" r="2"/><circle cx="19" cy="5" r="2"/><circle cx="5" cy="19" r="2"/><circle cx="19" cy="19" r="2"/><path d="M10 10L6.5 6.5"/><path d="M14 10l3.5-3.5"/><path d="M10 14l-3.5 3.5"/><path d="M14 14l3.5 3.5"/></svg>
---

# The Portal Heap Is Hubs

A portal replica of the public instance grows ~215 MB/h, monotonically, through hundreds of full
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

`<namespace>/memex-portal-deployment-5b9cbf46dd-gs6j6`, 26 h old, working set 6 859 MiB,
container limit 16 Gi. A `dotnet-dump collect --type Heap` through an ephemeral
`--profile=sysadmin` container (the recipe is in
[Debugging Disposal, Storms and Leaks](/Doc/Architecture/DebuggingDisposalAndLeaks)), then
`dumpheap -stat`. The dump file was deleted from the pod afterwards; no cluster object was
mutated.

🚨 **That last sentence understates the cost, and a later measurement proved it.** On a replica
this size `--type Heap` freezes the process for ~106 s, which is longer than the liveness budget,
so taking one **restarts the container** — see *The dump is not free* below. No *cluster object*
is mutated; the replica is.

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

### Neutral type metadata is shared; hub-specific state stays separate

A private three-silo PostgreSQL diagnostic on 2026-09-27 retained 5,000 owning-node stream
connections and produced 40,911 hubs. A full native heap census found 40,906 started hubs,
five starting hubs and no disposed hubs, so this specimen does not reproduce the disposed-hub
retention above. It does reproduce metadata multiplication: 3,662,876 type definitions describe
378 CLR types, with 3,652,612 separate deferred XML-description graphs.

**Why every hub had its own graph.** Every hub builds its own `TypeRegistry`
(`MessageHubConfiguration` chains it to the parent's, up to the mesh-level registry `MeshBuilder`
registers), and every `WithType` / `GetOrAddType` constructed a fresh `TypeDefinition` that
re-derived, from the CLR type alone, its display declaration, wordified name, icon and a deferred
XML-description closure. Nothing about that part depends on the hub — yet it was rebuilt once per
hub per type. On top of that every registry re-seeded the 27 basic types (`String`, `Int32`,
`Address`, `DeliveryFailure`, …) into its own map although its parent already resolved them.

**What is shared, and where it lives.** The hub-independent part is a `TypeDisplayMetadata` (display
declaration, fallback name, icon, lazy XML summary). The store of them, `TypeDisplayMetadataCache`, is an
INSTANCE created by the ROOT registry of a chain — the mesh-level one — and handed down to every
child registry through the parent chain, so there is one description per CLR type per MESH. It is
never `static` ([No Static State](../NoStaticState)): two independent meshes do not share
descriptions, and nothing outlives the mesh. The table is a `ConditionalWeakTable<Type, …>` so a
collectible NodeType's types still leave with their load context. A registry whose parent is a
`TypeRegistry` no longer seeds the basic types; it resolves them through the parent.

**What stays per hub.** Each hub still owns its `TypeDefinition` for every type it registers:
collection name, key-function builder, owning address and explicit description overrides are
per-registry registration state and are untouched. Resource-backed display names and groups are
resolved per definition, never cached as translated values. XML lookup stays on first description
access; constructing a definition must not parse XML docs.

`TypeDisplayMetadataSharedAcrossHubsTest` pins it: 32 hosted hubs registering the same type own 32
definitions over ONE description (a per-registry store measured 32); two independent meshes must NOT
share one (a process-wide static store fails that negative control); key functions and collection
names stay per registry; a hub-level registry resolves `String` to the mesh registry's own
definition. Resource/override/key-isolation tests for standalone definitions live in
`TypeDefinitionMetadataTest`. This is a measured reduction of per-hub metadata, not a closure of
the queue-starvation/OOM investigation (#5555, #1186).

### Per-hub DI and type metadata

The out-of-memory dump of the memex portal (MeshWeaver#7082, 2026-10-23) put a number on defect 3:
of 12.3 GB of managed heap, 5.78 GiB were Autofac child-scope rows (12,060,761
`ExternalComponentRegistration`, 50.8 M `MiddlewareDeclaration`) and 1.49 GiB were per-hub
`TypeRegistry` rows (2.79 M `TypeDefinition`). The dump's own counters size the hub population:
~101,600 `ConcurrentDictionary<String,TypeDefinition>` tables and ~303,000 registration-queue segments
(three per scope) say **~100,000 hubs**, so a hub carried **~120 wrappers ≈ 58 KB of Autofac and
~15 KB of type metadata** — not one copy of the whole parent registration set, but a
per-hub constant multiplied by a very large population. (The ~4,700 per *grain activation* in the
first reading is that constant times the ~40 hubs an activation hosts.)

**Where the wrappers come from.** A hub's scope is `BeginLifetimeScope(configurationAction)`, and a
scope with registrations of its own answers a service its parents provide by wrapping each parent
registration of it in an `ExternalComponentRegistration` with a fresh resolve-pipeline builder — kept
for the life of the hub. Most wrappers are what the hub genuinely resolves (its loggers, the mesh
services, the per-hub `INodeValidator`/`IDataValidator` set), and those are what keep a parent-scoped
service resolved in a hub bound to the HUB's own `IMessageHub` and overrides. **One group was pure
waste:** Autofac runs its startable pass on every such scope (the scope's property bag falls back to
the root's build options), asks the CHILD registry for `AutoActivate`, and every `RegisterInstance` —
which is every `AddSingleton(instance)` of every module — provides `AutoActivate`. So every hub scope
wrapped every instance registration of its ancestors, and started none of them: each was already
activated in the scope that owns it. Measured in a test mesh: 26 of a node hub's 125 wrappers, 26 of a
hosted child hub's 48; with 200 instance registrations at the root, a hosted hub held 221.

**The fix.** `HubScopeStartables` sets the hub scope's own build option to *ignore startable
components* unless the hub's own service collection registers an `IStartable` — the only thing an
`IServiceCollection` can produce that the pass would really start (an `AddSingleton(instance)` is
auto-activated, but as an externally owned provided instance its activation does nothing). The decision
is read from the descriptors before the scope is built: a first version that made it in a
`Registered` handler closing over the scope's builder kept disposed hubs reachable
(`StreamReleasesItsHubTest` caught it). Under 200 root instances a hosted hub now
holds 15 wrappers, a child-plus-grandchild pair 4 (407 before) —
`HubScopeDeclaresOnlyItsOwnServicesTest`, with the negative controls that a hub's own registration still
overrides the parent's (directly, through a parent-scoped service built in the hub, and in enumeration
order) and that a hub's own `IStartable` is still started.

**Type definitions.** A hub's registry already chains to its parent's; the copies came from the hub's
own `WithType` calls for types the mesh registry already held under the same name (a node hub: 344 own
definitions of 427 resolvable). `TypeRegistry.WithType` now binds the parent's definition OBJECT in the hub's own maps, instead of
building a copy, when it is exactly the one the hub would build — same type and name, no owning address, no description or icon
override, no resource-backed display text, and a key the conventions decide (`[Key]`, `Id`,
`SystemName`), because a registry's own key-function providers are consulted only after them. A type
whose key would come from the hub's own providers keeps its own definition, as before
(`HubRegistryInheritsEquivalentDefinitionsTest`). The binding stays LOCAL — the hub keeps its name and
type entries, and only the definition is shared — because a registry the hub inherits from can change
after the hub registered: a later `WithKeyFunction` on the parent, a module replacing the name's type
while the mesh runs, or an intermediate hub binding the same name to another type must not change what
this hub registered. For the same reason a closed generic (its name is synthesised locally before the
parent is asked) is never shared, the parent chain must resolve the registered name and the full-name
alias to the very same definition, and a hub that binds a collectible type, or keys one, listens to
that context's unloading itself. Each remaining `TypeDefinition` also lost its seven
per-instance key objects (two `Lazy`, two `LazyHelper`, two delegates, a closure); the key is now
resolved from the builder on first use, and the two weak-shadow name maps are created on the first
demotion only.

**Measured on a realistic population** (before the binding was made local; the hub's two map entries
per shared type are back, the definition objects stay shared) — 99 activated Markdown node hubs with the standard data-source
setup plus their hosted hubs, 516 `MessageHub`s, in one test mesh (`dumpheap -stat -live`):

| | Before | After |
|---|---:|---:|
| Live heap | 158.4 MB | 133.9 MB |
| Autofac rows | 31.9 MB (62 KB/hub) | 28.5 MB (55 KB/hub) |
| TypeRegistry rows | 30.6 MB (59 KB/hub) | 11.7 MB (23 KB/hub) |
| Retained per node-hub activation (`GC.GetTotalMemory`) | 2.49 MB | 2.04 MB |

**What this does NOT close.** The remaining ~55 KB of Autofac per hub is the services a hub resolves,
and it is the price of an Autofac child scope per hub: the wrapper is how a parent-scoped service gets
the hub's own dependencies. Removing it means replacing the per-hub child scope with something that
still gives parent-scoped services the hub's own `IMessageHub` — a redesign, not this change. Nor does
it touch defects 1 and 2, which set how many hubs there are; the per-hub cost only multiplies them.
How much of the production 120-wrapper constant was `AutoActivate` depends on how many instance
registrations the portal's modules make at the root; the next dump answers it directly (the
`ExternalComponentRegistration` count against the hub count).

### What a hub costs once the container is gone: the next candidates

Replacing Autofac (MeshWeaver#7147) took the container from 44.5 KB to 2.7 KB per hub. This section
answers the next question: **what is the remaining per-hub cost made of, and which part of it is
duplicated in every hub when it could be shared?** It is an analysis. Nothing in it is fixed yet.

**The harness.** The same one #7147 used, on its branch (`feat/7145-replace-autofac`, head
`bc9e9f5c58`): 99 Markdown node hubs in one Monolith test mesh, activated through
`GetMeshNodeStream` with their subscriptions held. One change: a `--type Heap` dump is taken both
BEFORE and AFTER the activations, so the per-type difference is exactly what 99 activations retain.
Measured 2026-10-25 on a host with 18 logical processors.

| Reading | Value |
|---|---:|
| Retained per activation (`GC.GetTotalMemory`) | 1,777,500 B (#7147: 1.77 MB) |
| Live heap before → after (`dumpheap -stat -live`) | 33.6 MB → 119.6 MB |
| New `MessageHub`s | 594: 99 node hubs + 495 `sync/{id}` stream hubs (5 per node hub) |
| Live bytes per activation, per hub | 868,567 B per activation = **144,761 B per hub** |

The two totals disagree by 2× (176 MB against 86 MB), and why was not established. The breakdown
below uses the live walk, because only that one has owners. The hub mix matches production: there,
89.6 % of hubs were `sync/` stream hubs (*What is in it*, above); here they are 83 %. **So a cost
that every hub carries is, in practice, mostly a cost that `sync/` hubs carry.**

**How ownership was assigned.** A ClrMD walk gives every live object its breadth-first parent from
the GC roots. Each object is then charged to its nearest ancestor that is not a collection, delegate,
closure, Rx operator, array or other BCL internal. So the `Object[]` inside a `ConcurrentDictionary`
inside a `TypeRegistry` is charged to `TypeRegistry`. The figures are the after-dump minus the
before-dump, divided by the 594 new hubs:

| Owner subsystem | Bytes per hub | Share |
|---|---:|---:|
| Type registry (`TypeRegistry`, `TypeDefinition`) | 44,550 | 30.8 % |
| Messaging (hub object, handler rules, delivery pipeline, `HostedHubsCollection`, `MessageService`, storm breaker) | 37,657 | 26.0 % |
| Serialization (the hub's `JsonSerializerOptions`, System.Text.Json metadata, the polymorphic resolver and converters) | 34,220 | 23.6 % |
| Data (workspace, three synchronization streams per node, reducers, stream-cache entries) | 12,267 | 8.5 % |
| Layout (area definitions, renderers) | 5,935 | 4.1 % |
| Assembly type cache (`Type[]` per assembly, held through a `ConditionalWeakTable` keyed by the assembly) | 3,761 | 2.6 % |
| DI (`MeshServiceProvider`, after #7147) | 3,755 | 2.6 % |
| Other | 2,619 | 1.8 % |

There is no `Microsoft.Extensions.Logging` row: a hub's loggers come from the root's factory and add
nothing per hub. Rx subjects and operators come to about 3.5 KB per hub, spread over the owners above,
and are real per-stream state. Orleans activation state is not in this harness (a Monolith), and no
Orleans type is among the 30 largest rows of the #7082 production dump, the smallest of which is
64.8 MB.

#### One cost cuts across all of them: `ConcurrentDictionary` sized for the machine

A census of every live `ConcurrentDictionary` (the dictionary, its `Tables`, its buckets, its lock
array, its lock objects and its per-lock counts) found:

- **29.6 dictionaries per hub, 60 % of them EMPTY**, holding 248 elements per hub in total.
- **45,310 bytes of skeleton per hub, which is 31 % of the whole hub.**
- **20.8 locks per dictionary.** The default concurrency level is `Environment.ProcessorCount`, and
  every lock costs 36 bytes (an `object[]` slot, an `Object`, and an `int[]` slot). That is what the
  bare `System.Object` row of this dump is: 629 objects per hub (15.1 KB), and 587 of them are lock
  objects held from a dictionary's lock array.

The largest groups per hub: two `String→TypeDefinition` maps (7.7 KB), 5.4 System.Text.Json
`PolymorphicTypeResolver` caches (7.5 KB), and five empty `ConcurrentDictionary<string, byte>`
warn-once sets (6.3 KB). After them come the four maps of `HostedHubsCollection` (hosted hubs, pending
creations, joining, retiring) and the three callback maps of `MessageService`, about 1.3 KB each and
empty on almost every hub, and the storm breaker's counter map. The warn-once sets are
`MessageHub.foreignSenderReported`, `routerTrafficOriginReported`, `routerTrafficReported`,
`ObjectPolymorphicConverter.warnedUnregisteredRead` and
`PolymorphicTypeInfoResolver.warnedUnloadableAssemblies`, plus two `<Type, byte>` sets in the same
resolver. **They exist to log something at most once, and on a healthy hub they never receive an
entry.** A leaf hub hosts nothing, yet its `retiring`, `joining` and hosted-hub maps are all built.

The production dump is consistent with this. It shows 6 GC heaps, so presumably 6 processors and 6
locks per dictionary, and 9.79 M `System.Object` (235 MB), which is about 98 per hub over the
~100,000 hubs.

#### The candidates, ranked

The bytes are measured in this harness (per hub, at 18 processors). The savings are ESTIMATES:
"×600" is this harness's population, and "×100k" is the #7082 OOM population (~2,568 grain
activations × ~40 hubs each). For the lock-bound row, both an 18-processor and a 6-processor figure
are given.

| # | Candidate | Measured per hub | Duplicated? | Fix shape | Est. saving per hub · ×600 · ×100k | Effort · risk |
|---|---|---:|---|---|---|---|
| 1 | **`ConcurrentDictionary` skeletons** (cross-cutting; overlaps rows 2–4) | 45.3 KB, 29.6 dicts, 60 % empty | Not duplicated, OVER-PROVISIONED: per-processor locks on maps a single-threaded hub writes, and maps that stay empty | (a) warn-once sets and leaf-only maps created lazily on first entry, or one mesh-scoped warn-once instance; (b) the rest `concurrencyLevel: 1` with a small capacity, or an immutable map where only the hub's own turn writes | ~31 KB (18 procs) / ~20 KB (6) · 18 / 12 MB · 3.1 / 2.0 GB | low per site, ~30 sites · low |
| 2 | **Type registry** | 44.5 KB, 69 own `TypeDefinition`s | **Yes, three ways** (below) | Re-registering an equivalent type is a no-op. A keyless type shares the parent's definition when the registry adds no key provider of its own. The key cell is a field set once, not a `Lazy` with a closure | ~16 KB · 10 MB · 1.6 GB | low–medium · low–medium |
| 3 | **Serialization stack per hub** | 34.2 KB; 612 `JsonSerializerOptions` for 608 hubs; 5.4 polymorphic resolvers per hub | **Yes for `sync/` hubs**: they register a handful of stream types the host already resolves, and still build their own options, resolver and STJ metadata | A hub whose registry adds nothing the host does not resolve reuses the host's options. The general shape is options keyed by the registry's content | ~25 KB (83 % of hubs × ~30 KB) · 15 MB · 2.5 GB | high · high (wire format) |
| 4 | **Handler rules and the delivery pipeline** | ~12.5 KB (40 `AsyncDelivery` delegates, 18 `DeliveryFilter`, 13 `MessageHandlerItem` per hub, plus closures) | Yes in shape: the same configuration lambdas build the same rules on every hub of a kind, each closing over its own hub | An immutable rule table per configuration, shared, with the hub passed in rather than captured | ~8 KB · 5 MB · 0.8 GB | high · medium |
| 5 | **Loadable types per assembly** (`PolymorphicTypeInfoResolver.loadableTypesByAssembly`) | 3.7 KB (951 `RuntimeType[]` for 594 hubs) | **Yes**: an assembly's loadable types do not depend on the hub | One `ConditionalWeakTable<Assembly, Type[]>` instance per mesh, handed down from the root like `TypeDisplayMetadataCache` | ~3.7 KB · 2.2 MB · 370 MB | low · low |
| 6 | **Layout area descriptions** | 2.9 KB average (17.7 KB per node hub): 3,396 description strings, **137 distinct** | **Yes**: `CreateLayoutAreaDefinition` reads the XML summary and the display attributes again per hub per area; only `Url` is per hub | The hub-independent part (description, title, group, order) cached per mesh by the view's `MethodInfo`; the hub adds only `Url` | ~2.9 KB · 1.7 MB · ≤290 MB | low · low |
| — | Data: workspace and streams | 12.3 KB | No: genuine per-node state | Fewer hubs (defects 1 and 2 above), not sharing | — | — |
| — | DI container | 3.8 KB | Done (#7147) | — | — | — |

**The three ways the type registry still duplicates** (row 2):

- **Two definitions per type per hub.** There are 1,215 `PingRequest` definitions for 608 hubs, and
  1,217 for `Address`. Both copies are held through the registry's `String→TypeDefinition` maps. By
  code reading: a second `WithType` of a name already registered skips the inherited path
  (`typeByName.ContainsKey`), builds a new definition and overwrites `typeByName`. Meanwhile
  `IndexFullNameAlias` uses `TryAdd` for a non-collectible type, so the alias map keeps the FIRST
  definition. Which call registers the type twice was not established.
- **Keyless types are never shared.** `IsEquivalentPlainDefinition` requires `HasConventionalKey`.
  Request, response and event types such as `PingRequest`, `DisposeRequest` and `DataChangedEvent`
  have no key, so the 16 types every hub registers are copied on every hub, `sync/` hubs included.
  Over all hubs there are 42,363 definitions of 434 types. The 16 types on every hub account for
  12,459 of them, and 239 node-hub types for 29,717 (about 300 per node hub).
- **The key cell is four objects per definition**: a `Lazy<KeyFunction>`, its `LazyHelper`, a
  `Func<KeyFunction>` and the closure, about 168 bytes of the ~272 a definition costs. That is about
  11.7 KB per hub.

**Correction to the #7082 reading:** `MeshWeaver.Messaging.AsyncDelivery` is a DELEGATE type: a
handler rule (`public delegate IObservable<IMessageDelivery> AsyncDelivery(IMessageDelivery,
CancellationToken)`, held in `MessageHub.rules`). It is not a queued message. The dump's 1,823,071
instances are about 18 registered rules per hub, not 1.8 M messages waiting. They belong in row 4,
not in a backlog count.

**Recommended order.** First row 1, because it is the cheapest and touches every hub. Then row 2,
because it is local to `TypeRegistry`, and #7131's tests (`HubRegistryInheritsEquivalentDefinitionsTest`,
`TypeDisplayMetadataSharedAcrossHubsTest`) already pin the sharing semantics. Then row 3, the largest
structural saving, because it removes most of the serialization line from 5 of every 6 hubs, though
it needs a design for "this hub's registry adds nothing new". Rows 5 and 6 are small, safe and can
ride along with row 2. Together rows 1–3 come to an estimated ~70 KB of the 145 KB per hub. That is
an estimate from these readings, not a measurement; each fix re-runs this harness, before and after.

#### Allocation churn: two sources the retained heap does not show

**`Subject<MeshChangeEvent>+SubjectDisposable[]` re-copies** (0.65 GiB sampled). The cause:
`InProcessMeshChangeFeed` holds ONE Rx `Subject<MeshChangeEvent>` per channel for the whole process,
and every subscriber filters inside its own handler. An Rx `Subject` keeps its observers in a
copy-on-write array, so every `Subscribe` and every `Dispose` allocates a new array of all N current
observers. The dump counts the observers: 311 after the 99 activations against 14 before, so **three
subscribers per node activation**. Those are one `Workspace` (104), one `JsonSynchronizationStream`
(99), one auto-detaching operator subscription (100), plus 6 synced queries and the two mesh-level
caches. At production's ~2,568 activations, that is ~7,700 observers and about 62 KB copied on every
hub start and every hub stop. Every commit also invokes all N handlers. The fix shape: fan out by
path inside the feed, an immutable map from path to its handlers, so that a subscription copies only
its own path's handlers and an event reaches only its path's subscribers. The kind filter stays.

**`CoffHeader`** (1.94 GiB sampled). `System.Reflection.PortableExecutable.CoffHeader` is allocated
whenever a `PEReader` parses PE headers. The [allocation sampler](../WhichKindOfSilence) saw it in
the first ticks after boot, while assembly loading and compilation run. By code reading, the
repeated parses in the compile path are:

- `CompileReferences.Choose` calls `AssemblyName.GetAssemblyName` on the host and the package file,
  for every package assembly of every compile.
- `CompileReferences.WithPackageAssemblies` creates a fresh `MetadataReference.CreateFromFile` per
  package assembly per compile by default. Roslyn then re-reads that reference's metadata and cannot
  reuse symbols across compilations.
- The identity reads (`SameIdentity`, `ServedBuildIdentity`, `GeneratedInputIdentity`,
  `NodeSetCompiler`) open their own `PEReader` per call.

The fix shape: a mesh-scoped INSTANCE cache, never `static`, of the assembly name or MVID and of the
`PortableExecutableReference`. It is keyed by path, length and last-write time, and passed to every
`WithPackageAssemblies` caller. The kernel already passes its `GetOrCreateFromFile`. Which caller
dominates is NOT established: the sampler records types, not stacks. The next reading is an
allocation trace with stacks (`dotnet-trace` with `gc-verbose`) over one boot.

## Why the LIVE ones are never released

`HostedHubsCollection.messageHubs` (the field in `src/MeshWeaver.Messaging.Hub/HostedHubsCollection.cs`):

```csharp
private readonly ConcurrentDictionary<Address, IMessageHub> messageHubs = new(AddressComparer.Instance);
```

It has **no idle sweep, no TTL, no size cap and no LRU**. The one removal is on the hub's own
disposal, registered in `HostedHubsCollection.Track` (shown as it read at the time of the
measurement; it now removes the entry only while it is still that hub):

```csharp
hub.RegisterForDisposal(h => messageHubs.TryRemove(h.Address, out _));
```

So the chain is: an entry leaves iff the hub disposes; a grain-hosted node hub disposes iff
`MessageHubGrain.OnDeactivateAsync` runs (it calls `hub.DisposeAbortingCurrentTurn()` — dispose
first, then abort the current turn; the order is explained in
[Hub Initialization Failure](../HubInitializationFailure)); and that method logged its own first line
(the `LogInformation` at the top of `OnDeactivateAsync`, *"Grain {GrainId} deactivating: reason={Reason}"*) **zero times across
five replicas and 514 687 log lines in 28 hours**, in a window carrying 2 791 other `info:` lines.

Deactivation never happens because the activation is re-pinned faster than it can age out.
`MessageHubGrain.CompleteActivation` installs

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
`RegisterForDisposal` removal registered in `HostedHubsCollection.Track` fired for the other 1 495. The registry is where the
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
   The share that was waste — every ancestor's instance registrations, wrapped by a startable pass
   that started nothing, and a copy of every type definition the mesh already held — is removed;
   what remains is measured in *Per-hub DI and type metadata* above.

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

🚨 **Two things block that walk today, and both are about WHICH replica you point it at**
(measured 2026-09-06, Systemorph/MeshWeaver#3432):

- **No deployment carries the `Dead`-half fix yet.** #3427 merged as `f41f8bda` at 15:49Z; CD run
  7905 was that commit and was *cancelled*, so the earliest published image carrying it is
  **`3.0.0-ci.7906`** (`71c194ca9` — confirmed an ancestor-of check plus the tag present in ACR).
  Both the control instance and the public instance run **`3.0.0-rc9.ci.7693`**, 213 CD runs older, because prod was
  rolled back by hand that evening. 🚨 Mind the two tag lines when reading those side by side: the
  running tag carries an `rc9.` segment that current CD tags no longer use, so compare the CD run
  NUMBER, not the string. A dump taken now therefore measures the *pre*-step-3 population and
  cannot answer "did the `Started` count move", which is the first row of this section's outcome
  table.
- **Dumping a replica fat enough to carry the population restarts it** — see *The dump is not
  free* below. The walk itself is cheap and correct; getting a dump to run it against is the
  expensive half.

🚨 **Do not skip to a fix on the strength of the corpse count alone.** 1 496 dead hubs is ≈580 MB
— real, unambiguous, and worth fixing — but it is **one sixth** of the 9 398. Fixing only that
leaves ~2.7 GB in the `Started` population untouched, and (2) is what decides whether that
remainder is a leak or a sizing problem.

A third reading worth having: the growth *rate* of `MessageHub` count against replica age, on three
replicas of different ages. #3321 observed that working set tracks uptime rather than load — the
near-idle `fxbhd` holds 11 443 MiB on 512 log lines per 30 min while the busiest replica holds
4 134 MiB. The +7-in-22-minutes reading above refines that rather than contradicting it: hubs
arrive with load and never leave, so an idle pod *holds* its history without adding to it.

## The dump is not free — it costs a container restart at this heap size

Measured 2026-09-06 on `<namespace>/memex-portal-deployment-58c859d4cc-gqqg7` (8 h old, working
set 6 609 MiB, image `3.0.0-rc9.ci.7693`), while attempting the referrer walk this page's *What is
still open* section asks for.

```
21:37:38Z  dotnet-dump collect -p 1 --type Heap   (process suspended)
21:39:17Z  kubelet: Container memex-portal failed liveness probe, will be restarted
21:39:26Z  Complete                               (process resumed — 106 s of freeze)
21:39:35Z  ephemeral container SIGKILLed with the restart
21:42:18Z  pod reads 1/1 Running, RESTARTS 1
```

The probes, read off the live deployment, are what turns a long freeze into a restart:

| probe | path | period | timeout | failureThreshold | a freeze longer than |
|---|---|---:|---:|---:|---|
| readiness | `/ready` | 10 s | 5 s | 3 | **≈30 s** pulls the replica out of the Service |
| liveness | `/alive` | 15 s | 10 s | 6 | **≈90 s** restarts the container |

🚨 **The freeze is not write-bound, so it does not shrink by finding a faster disk.** `dd
oflag=direct` on the same node measured **169 MB/s** (3.1 GB in 18.6 s). This dump's own file size
was never read — the container restart wiped it before it could be — but at the 5.54 GB the
2026-09-04 specimen produced, the write is only ≈33 s of the 106. The rest is `createdump` walking
the object graph — ≈50 M objects — with every thread suspended, so **the freeze scales with the
very thing that makes this heap interesting.**

**Hence the tension that governs every future measurement on this page's open questions: the
population only exists on a replica fat enough that dumping it restarts the replica.** A young
replica dumps in seconds and has nothing to show — a freshly restarted portal here reached
2 977 MiB within five minutes, so ~3 GiB is the *floor*, not the accumulation.

So do not treat "read-only, dump deleted afterwards" as meaning "no impact". Either:

- **Spend the restart on purpose.** The other four replicas stayed `1/1 Running` with `RESTARTS 0`
  throughout, so the deployment served from four of five for ≈2 min; the cost is one replica's
  accumulated state, which is also the thing being measured. That can be a fair trade — but it is a
  decision to take deliberately and in advance, not a side effect to discover afterwards. (Whether
  any in-flight request was actually dropped was not measured.)
- **Or use the counters below**, which read EventCounters over the diagnostic socket and never
  suspend the process. They cannot answer a referrer question, but they discriminate retention from
  fragmentation, which is what most of this page's falsifications turned on.

🚨 **And note WHY the cheap option is so weak: the platform publishes no metric of its own.**
`src/` contains **zero** `System.Diagnostics.Metrics` meters — `grep -rn "new Meter(" src` is
empty — so every counter this page relies on (`dotnet.gc.*`, `dotnet.assembly.count`,
`dotnet.timer.count`) is a **runtime built-in**. Nothing anywhere reports live `MessageHub` count,
the `sync/`-versus-node split, the `RunLevel` histogram, or `SynchronizationStream`
total-vs-disposed. That is exactly why `dotnet.timer.count` appears below as a *proxy* for live
hubs, and why answering any question on this page currently costs a replica restart. The three
numbers this whole investigation turns on are cheap to compute in-process and are simply not
emitted — `HostedHubsCollection.Hubs.Count()` already exists and is used only in two log lines
(`MessageHub.Dispose` and `MessageHub.HandleShutdownCore`). An observable-gauge trio over hub count by address type and run
level would make the population trackable per replica, continuously, at no risk — and would turn
"take a dump and hope" into "watch the curve and dump only when it says something changed".

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

**Four practicalities, all verified 2026-09-06 against `Microsoft.Diagnostics.Runtime` 4.0.732401:**

- **Pass the DAC explicitly.** `DataTarget.LoadDump(path)` records the runtime path *as seen by the
  target*, which does not exist in the SDK sidecar. Find it with
  `find /proc/1/root/usr/share/dotnet -name libmscordaccore.so` (it was
  `…/Microsoft.NETCore.App/10.0.11/`) and pass it to `CreateRuntime(dacPath, ignoreMismatch: true)`.
- **Compile the walk locally first.** `dotnet new console` + the same package pins the identical
  version, so a local `dotnet build -c Release` catches a typo before it costs a dump — and, given
  the section above, a dump can cost a replica.
- **Get the analyzer into the sidecar with `kubectl cp`**, not a base64 heredoc:
  `az aks command invoke --file Program.cs --command "kubectl cp Program.cs <ns>/<pod>:/hw/Program.cs -c <container>"`.
  Create the sidecar with `-- sleep 7200` and drive it with individual `kubectl exec` calls, so a
  failed step costs one command rather than the whole run.
- **The sidecar dies with the target.** It is an ephemeral container in the same pod: if the dump
  trips the liveness probe, the sidecar is SIGKILLed (exit 137) along with the restart, taking the
  dump file and any un-flushed output with it. Print results as you go; do not batch them to the end.

The fields the *open questions* above need, beyond address and `runLevel`: `SynchronizationStream`'s
`isDisposed` and `Hub`; `Address.Host` (which names the parent hub, so a `Started` sub-hub under a
`Dead` parent is separable from one under a live parent); and the subscriber count behind
`SynchronizationStream.Store`, reachable as `ReplaySubject → _implementation → _observers → _data[]`
— the discriminator for "is anyone actually listening to this stream".

## Related

- [Debugging Disposal, Storms and Leaks](/Doc/Architecture/DebuggingDisposalAndLeaks) — the profiling recipe and the ClrMD root-chasing method.
- [Hub Disposal Model](/Doc/Architecture/HubDisposalModel) — what disposal means for a hub, and the RunLevel phases.
- [Mesh Node Stream Cache](/Doc/Architecture/MeshNodeStreamCache) — the idle sweep that *is* bounded, and its "never release an entry with a live subscriber" contract.
- [Dead Circuit Fan-Out Storm](/Doc/Architecture/DeadCircuitFanOutStorm) — the sibling failure where a departed subscriber keeps costing.
- [Actor Model](/Doc/Architecture/ActorModel) — why there is one hub per addressable thing in the first place.
