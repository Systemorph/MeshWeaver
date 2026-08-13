# Build coordination — the Build node protocol

Who builds, what gets built, and when a silo may serve — decided by **mesh nodes**, not by
file leases or in-process sweeps. This page is the protocol contract; the compilation
mechanics it orchestrates are unchanged and documented in
[NodeTypeCompilation](../NodeTypeCompilation) and [NodeTypeRelease](../Postmortems/NodeTypeReleaseRedesign).

## Why the current shape has to go

Today the bake is an in-process sweep (`DynamicTypePreWarmerHostedService` +
`NodeTypeBatchBake`) arbitrated by a file lease under the shared assembly cache, and
readiness is fed by that same process's private state. Three defect classes follow
directly from that shape, and each has caused a production incident:

1. **Starved cross-silo discovery.** During a rollout the baking pod and the serving pod
   share one Orleans cluster, so the baker's per-type source reads land on activations the
   outgoing pod already holds. Under churn those round-trips starve, Roslyn compiles
   against an empty source set, and a healthy type reads as a regression (CS0246 naming
   the type's *own* classes). `[PreferLocalPlacement]` cannot fix this — it only biases
   where a *new* activation lands.
2. **Mid-bake content races.** The sweep compiles against the *moving* mesh. A plugin
   auto-update landing mid-bake gave Roslyn a source set that existed for two minutes and
   never again; the regression latch stuck and the rollout hung. The retraction machinery
   in `NodeTypeBakeGateState` exists to paper over exactly this.
3. **Retention.** Every compile reads its sources through the mesh, and each touched path
   mints synchronization state in the baking process (~22 `sync/` sub-hubs, ~9 MB retained
   per trivial recompile), on top of the collectible-ALC accumulation. A long bake grows
   until the pod hits its memory ceiling.

The protocol below removes all three **by construction** rather than by compensation.

## The shape

```
Admin/Build                       ← the build ROOT (durable node, nodeType Build)
Admin/Build/{chunkName}           ← one node per CHUNK (durable, nodeType Build)
Admin/Build/{chunkName}/_Activity ← the chunk's execution activity (standard Activity)
{nodeTypePath}/Release/{version}  ← unchanged: releases minted per compiled NodeType
```

- The **root** carries the build's identity — the framework fingerprint it targets
  (`MeshWeaver.Graph`'s MVID, the same value `HasUsableBuild` compares against), the
  commit set of the synced sources, the chunk plan, and the aggregate `Status`.
- A **chunk** is a named unit of work defined by an array of mesh queries — as simple as a
  list of paths, or a module such as `namespace:MyPlugin scope:subtree nodeType:Code`.
  Chunk names address the nodes: `Admin/Build/{chunkName}`.
- Each chunk node **launches one Activity** to execute its build (standard `_Activity`
  satellite: progress, cancel, terminal status — nothing bespoke) and reports back the
  paths it wrote: the **release node paths** its compiles minted. Releases stay the
  system of record for artifacts; the chunk records which ones this build produced.
- When every chunk is terminal and none regressed, the root flips to `Ready` — the **GO
  signal**.

All coordination state is durable node content. There is no other channel: no lease
files, no request/response types, no in-memory gates. Writers go through
`GetMeshNodeStream(path).Update(...)`; the owning hub serialises them.

## Who becomes the build master

Nobody is elected. **Mastership is a claim written into the root node**, arbitrated the
same way every concurrent write in this system is arbitrated: inside the `Update` lambda.

```csharp
stream.Update(node =>
{
    var b = node.ContentAs<BuildRoot>(hub.JsonSerializerOptions);
    // The claim check and the claim write are one serialised step on the owning hub.
    if (b is null || (b.ClaimedBy is not null && b.FrameworkVersion == myFingerprint))
        return node;                    // someone else already owns this build — bail
    return node with { Content = b with
        { ClaimedBy = myIdentity, FrameworkVersion = myFingerprint, Status = BuildStatus.Planning } };
});
```

The owning hub's action block serialises every writer, so the first candidate's lambda
sees an unclaimed build and takes it; every later candidate's lambda re-reads the claimed
state and returns the node unchanged. The claimant then observes its own claim on the
stream before doing any work — a claim you cannot read back is a claim you do not hold.
In-memory single-flight flags are permitted only as coalescers; **correctness comes from
node state** (the [ActivityControlPlane](../ActivityControlPlane) rule).

The same claim shape applies per chunk, which is what makes parallel builders safe later:
each would-be builder claims `Admin/Build/{chunkName}`; the owning hub hands each chunk
to exactly one.

## The bake runs in its own, disposable cluster

The build master is **not a portal pod**. It is an ephemeral silo with its **own Orleans
ServiceId** (and ClusterId), started per new image version and disposed after GO:

- **Same image, different mode.** The bake host runs the *portal image* with a bake
  entrypoint. This is non-negotiable: the framework fingerprint is the Graph assembly's
  MVID, so only byte-identical binaries produce bakes the portal can use. (A dedicated
  bake image was tried in 2025 and retired for exactly this reason — it computed a foreign
  fingerprint; see the `memex-bake` retirement, #1347.)
- **Separate ServiceId = the grain-placement answer.** Every grain involved in baking
  activates in the bake cluster because that is the only cluster it exists in; every
  regular grain keeps serving in the live cluster. Neither can starve the other, and
  defect class 1 above is unrepresentable.
- **Commit-pinned sources.** Chunk compiles read their sources from the on-disk
  [source replica](../NodeTypeCompilation) (`GitModuleReplica`) at the chunk's recorded
  commit (`GitHubSyncConfig.LastSyncCommitSha` per synced space) — a *consistent snapshot*,
  not the moving mesh. Defect class 2 is unrepresentable: the source set cannot change
  under the compiler, and an unchanged (commit, fingerprint) pair is provably a no-op.
- **Dispose after GO.** The bake process exits once the root is `Ready`. Whatever
  synchronization state and collectible ALCs the compiles minted die with it — defect
  class 3 is bounded by process lifetime instead of by leak-chasing.

The shared surfaces between the two clusters are exactly three, all already
multi-process-safe: the Postgres store (node state, serialised per node by the owning
hub), the assembly store under `/data` (content-addressed writes), and the source replica
(commit-addressed, idempotent).

## The GO signal and readiness

Every portal silo — old and new — **subscribes** to the build root and holds its health
probe accordingly:

- On startup a silo computes its own framework fingerprint (its Graph MVID — a local,
  mesh-independent computation) and subscribes to `Admin/Build`.
- The readiness probe reports **not ready** until the observed root says
  `Status = Ready && FrameworkVersion == <my fingerprint>`.
- Old pods keep serving through a rollout untouched: the GO for *their* fingerprint was
  written when *their* image baked, and a newer build's state transitions never revoke an
  older GO — the root's `Ready` is per-fingerprint history, not a global boolean.

The broadcast rides the **durable store's change feed** (Postgres `LISTEN/NOTIFY` via
`PostgreSqlChangeListener`), which is cross-process and therefore cross-ServiceId by
construction. It deliberately does *not* ride the Orleans stream relay — the bake cluster
is not in the portal's cluster, and intra-cluster relays must not be a readiness
dependency. The subscription itself is a remote-path watch and uses the
`SubscribeWithReEstablish` fault taxonomy: transient faults re-establish; a poisoned or
deleted root is terminal and loud, never a silent 1 Hz retry loop.

The probe semantics of `NodeTypeBakeGateState` are preserved unchanged — fail **closed**
on a measured regression (the rollout stalls, the old image keeps serving), fail **open**
when nothing is measuring (a configuration mistake must never black-hole a pod), gate
`/health` and never `/alive`. Only the *feeder* changes: a subscription to the build root
instead of the in-process sweep. The elaborate retraction/cascade machinery loses its
main customers (timeout-blindness and content races are gone by construction) but keeps
guarding the one honest case: a type that really does not compile on this image.

## Chunking: plugins are the natural unit

The chunk plan is **derived, not invented**:

- one chunk per installed plugin — a plugin's footprint *is* a chunk query
  (`namespace:{Plugin} scope:subtree nodeType:Code`), and its registry commit is the
  chunk's commit key;
- residual chunks for non-plugin content (samples, user NodeTypes), partitioned along
  namespace lines;
- a plugin may *declare* its chunk (override queries, order dependencies) in its manifest
  — declarative content, shipped with the plugin.

Two consequences fall out for free:

- **A module update is a build.** "This plugin's commit moved" re-runs exactly
  `Admin/Build/{pluginName}` — same protocol, no separate mechanism. The install/update
  path compares recorded vs target commit and materialises only what differs.
- **Parallelism is claim arbitration, not state transfer.** When multiple builders run,
  the content-addressed stores (assembly cache, source replica) do the heavy
  coordination; the protocol only arbitrates who takes which chunk — the claim shape
  above, unchanged.

## What the coordinator is NOT

- **Not a dynamic NodeType.** The protocol engine makes dynamic content usable; if it
  were itself dynamic content, a compile failure in it would leave the portal permanently
  not-ready with no deterministic way back, and none of it would be CI-tested. The
  engine ships in the image. Plugins contribute *declarative* chunk manifests and
  observation UI, never coordinator code.
- **Not a static declared node.** `AddMeshNodes` entries are process-local and never
  persisted — and a static claim at the root's path would shadow the durable row
  (the #1209 class). The root is created **if absent** by the claimant, durably, and
  existence is answered by the store.
- **Not request/response.** No `StartBuildRequest`. A build is requested by writing
  `RequestedStatus = Running` on the node ([RequestViaStreamUpdate](../RequestViaStreamUpdate));
  cancel is `RequestedStatus = Cancelled` via the standard activity surface.

## What this retires

| Retired | Replaced by |
|---|---|
| `.bake-lease` file lease under the assembly cache | the claim field on `Admin/Build`, arbitrated by the owning hub |
| in-process sweep orchestration (`DynamicTypePreWarmerHostedService` driving the whole bake) | the ephemeral bake silo executing chunk activities |
| readiness fed by process-private sweep state | readiness fed by the GO subscription |
| cross-silo discovery timeouts + their leniency rules | bake-cluster-local discovery + commit-pinned sources |

The compilation mechanics themselves — Roslyn invocation, release minting
(`{nodeTypePath}/Release/{version}`), the assembly store, `HasUsableBuild` — are reused
as-is. This protocol changes who runs them, against which source snapshot, and how the
verdict reaches the probes.
