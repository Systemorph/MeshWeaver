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
Admin/Build/_Claim                ← the root's claim LOCK (durable; only atomic writes)
Admin/Build/{chunkName}           ← one node per CHUNK (durable, nodeType Build)
Admin/Build/{chunkName}/_Claim    ← the chunk's claim LOCK
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

Nobody is elected. **Mastership is a claim written into the root node.** Candidates register
under their own holder id in `RequestedClaims` (per-candidate keys — RFC 7396 merge-safe, so
concurrent registrations compose instead of overwriting each other), and the node's own hub
arbitrates. The claimant then observes its own claim on the stream before doing any work — a
claim you cannot read back is a claim you do not hold. In-memory single-flight flags are
permitted only as coalescers; **correctness comes from node state** (the
[ActivityControlPlane](../ActivityControlPlane) rule).

The same claim shape applies per chunk, which is what makes parallel builders safe later:
each would-be builder claims `Admin/Build/{chunkName}`; exactly one gets each chunk.

### 🚨 The grant is taken on the DURABLE ROW — a hub lambda is only exclusive within ONE cluster

The obvious implementation is to decide inside the owning hub's serialised `Update` lambda:
the action block orders every writer, so the first candidate's lambda sees an unclaimed build
and takes it, and every later lambda re-reads the claimed state and bails. That is correct —
**and it is exclusive only within one Orleans cluster**, which is not the topology this
protocol runs in.

A second cluster over the same database activates its *own* hub for `Admin/Build`, runs its
*own* arbiter against its *own* mirror, and grants its own candidate. Three things then line
up to make the collision invisible (#1424):

- both writes are minted at the same next `MeshNode.Version`, and the store's monotonic
  condition **applies at equal versions** — re-persisting an unchanged node is a legitimate,
  common shape — so both land, last-write-wins;
- neither writer is told it lost: the "store refused" signal is `saved.Version >
  written.Version`, and the versions are *equal*;
- nothing propagates the other cluster's write back. Orleans membership and its memory
  streams are per-cluster by construction, and there is no cross-process change feed running
  (see the GO section below), so a mirror can be arbitrarily far behind and never learn.

On 2026-08-13 that is exactly what happened on `memex-cloud`: the ephemeral bake Job and the
rolling serving pod each claimed `Admin/Build` and each ran the full 268-type bake.

#### The claim lives on a LOCK, not on the Build node

Making the *grant* exclusive is necessary and not sufficient, and the second half is the less
obvious one. Every cluster mirrors `Admin/Build` in its own workspace, and that mirror is
flushed to storage as a **whole node** by the ordinary persistence sampler — a plain,
unconditional write from a hub that may never have held the claim. `MeshNode.Version` is a
per-node counter, not a cross-cluster logical clock, so both mirrors climb it independently and
the later flush simply wins the row. A *losing* cluster therefore overwrites the winner's
`ClaimedBy` with its own `null`, its arbiter sees a free build on the next pass, and the
exclusivity a compare-and-set had just established is undone by a write that knows nothing about
it. (This is observed behaviour, not a hypothesis: the cross-cluster test caught exactly it.)

So the claim moved off the contended row onto a **lock node**, `Admin/Build/_Claim` (and
`Admin/Build/{chunk}/_Claim`). The lock has no hub, no mirror and no sampler. Exactly two things
ever write it, and neither is unconditional:

| Operation | Primitive | Exclusivity |
|---|---|---|
| grant / takeover | `IStorageAdapter.WriteIfVersion(node, expectedVersion)` | compare-and-set on the version the arbiter read (`0` = "must not exist") |
| heartbeat | `WriteIfVersion` | no-op unless this holder still owns the lock |
| release | `IStorageAdapter.DeleteIfExists` | rowcount-gated, first delete wins |

`BuildState.ClaimedBy` on the Build node stays as the **observable projection** — what the GUI
and `ObserveBuildClaim` read. A flush clobbering it now costs a stale view in one cluster, never
a second builder, because no decision is taken from it.

#### The three steps, in this order

`BuildNodeType.ArbitrateDurably`:

1. **Read the lock.** Not the mirror — the mirror is per-cluster and cannot see a rival's grant.
2. **Decide** with the unchanged `Arbitrate`, over the *lock's* holder state and *this cluster's*
   pending registrations (from its mirror, because a candidate that registered milliseconds ago
   has not reached storage yet — and a cluster can only ever grant one of its own candidates).
3. **Commit with the compare-and-set** against the version step 1 read. At most one of N
   concurrent arbiters is told `true`. Only then is the grant published on the Build node.

Step 3 has to come before the mirror write, not after, because **the mirror commits ~200 ms
before the persistence sampler reaches storage** and `ObserveBuildClaim` emits off the mirror.
Adjudicating after the fact would find the loser already baking.

Losing writes nothing and retries nothing: a refused compare-and-set means another cluster's
grant is already durable, so this cluster does not grant, its candidate runs out its
`GrantWindow` and follows the GO — the path the protocol already has for "held elsewhere".
The arbiter is level-triggered, so the next legitimate trigger re-decides against fresh
durable state. No timer, no election, no backoff loop.

**It fails OPEN.** With no storage provider owning the path (or no persistence at all — a
monolith, a test, a dev box) the grant is taken on the mirror exactly as before. Being wrong
in that direction costs one duplicated bake: bounded and non-corrupting, because every
downstream write is content-addressed (assembly store keyed by content, release versions
minted from content hashes, GO keyed by fingerprint). Being wrong the other way — refusing to
grant because exclusivity could not be proven — leaves the fingerprint with no GO at all,
which holds every silo's readiness probe down and stalls the rollout. Same asymmetry the bake
gate applies, and the opposite of `AssemblyCacheRetention`, where the wrong answer deletes
bytes a running pod still needs.

### 🚨 When may the claim be taken away — cluster MEMBERSHIP decides, not a clock

A builder that dies mid-build must not wedge the fleet, so a claim has to be reclaimable. The
obvious way is a staleness budget on the holder's heartbeat, and that is what this started as
(`ClaimStaleAfter`, 10 minutes). It is the wrong instrument, and wrong in **both** directions: a
timestamp answers "when did the holder last manage to write", which conflates *dead* with *busy*,
*starved* and *descheduled*. So the fleet waits out ten minutes for a pod that is already gone, and
it is licensed to evict a pod that is merely slow — putting two builders on one compile, the exact
storm the claim exists to prevent (#1355).

Where a cluster exists it already answers the real question authoritatively and immediately — it
runs probes, indirect probes and a membership table for precisely this. So the candidate stamps its
`IClusterMembership.LocalIdentity` into its claim request, the arbiter copies it to
`BuildState.ClaimedByIdentity` on grant, and takeover is a lookup rather than a guess:

| Membership says about the holder | Result |
|---|---|
| **Gone** | take over **immediately** — no budget to wait out once the cluster has positively recorded it as departed |
| **Alive** | **never** take over, however old the heartbeat looks |
| **Unknown** | fall back to the `ClaimStaleAfter` (10 min) heartbeat clock |

`Unknown` is the only path that still consults a clock, and it covers exactly the hosts that have no
cluster to ask — a monolith, a test, a dev box, the Orleans *client* host — plus a claim written
before identities were stamped and an identity membership cannot resolve.

🚨 **A holder in ANOTHER cluster is always `Unknown`**, because Orleans membership is per-cluster:
the bake silo's ServiceId is not in the serving cluster's table and vice versa, and absence is
`Unknown`, never `Gone`. So a cross-cluster takeover falls back to the `ClaimStaleAfter` clock —
this is the one place the clock still governs a live fleet, and it is deliberate: the alternative
is treating "I cannot see you" as "you are dead", which is precisely the eviction of a live builder
the membership rule exists to prevent. What #1424 adds is that the takeover WRITE is itself a
compare-and-set, so two clusters that time out on the same dead holder cannot both succeed it.

🚨 **Absence from the membership snapshot is `Unknown`, never `Gone`.** Orleans keeps departed silos
in the table as `Dead` until the defunct-cleanup window elapses, so a silo that really died IS in the
snapshot and resolves to `Gone`. One that is missing entirely usually means our own snapshot is not
hydrated yet — and reading that as death on a freshly-started silo would evict a live builder, which
is the one failure this rule exists to make impossible.

The heartbeat needs no defending here: it is a field on the Build node written through
`stream.Update`, so it is durable mesh state rather than a file's last-write metadata. The
predecessor lease kept its instant in an SMB timestamp, where Azure Files' metadata caching could
report a heartbeat as stale while its holder was alive — that whole failure mode left with the lease.

Code: `BuildNodeType.Arbitrate` (pure over `now` and the membership verdict, so every rule above is
tested without a cluster and without wall-clock), `IClusterMembership`
(`src/MeshWeaver.Mesh.Contract/Services/IClusterMembership.cs`), and `OrleansClusterMembership`
registered silo-side by `ConfigureMeshWeaverServer`.

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

The broadcast is *designed* to ride the **durable store's change feed** (Postgres `LISTEN/NOTIFY`
via `PostgreSqlChangeListener`), which is cross-process and therefore cross-ServiceId by
construction. It deliberately does *not* ride the Orleans stream relay — the bake cluster
is not in the portal's cluster, and intra-cluster relays must not be a readiness
dependency. The subscription itself is a remote-path watch and uses the
`SubscribeWithReEstablish` fault taxonomy: transient faults re-establish; a poisoned or
deleted root is terminal and loud, never a silent 1 Hz retry loop.

🚨 **That feed is not running.** `PostgreSqlChangeListener` is registered but never started in
either partitioned-PG overload — `AddPartitionedPostgreSqlPersistence` has the `AddHostedService`
call commented out, and the Aspire overload the portal actually uses never had one. Within a
cluster the GO still reaches every silo, because `GetMeshNodeStream` resolves to the ONE per-node
hub that owns `Admin/Build` there; **across clusters nothing propagates it**, so a follower in the
bake silo's peer cluster observes only what its own hub read at activation.

**Nothing in the protocol may therefore depend on being notified.** The arbiter never did — it
READS the durable row on every pass (above). The follower used to, and that is #1440.

### The follower has two doors, and it closes them behind it

`BuildProtocolDriver.FollowGo` used to be a single `ObserveBuildGo(fingerprint).Take(1)`. That is a
projection of *this cluster's mirror*, so a cross-cluster follower was waiting on an event that
could not be delivered to it — and it had also stopped observing its own claim, so the one event
that could still reach it in-process went unheard. It had no door at all. It now ends on either of
two **real** events:

1. **The GO becomes visible** — `ReadBuildGo` reads it off the durable build root (the same witness
   the arbiter decides on) *and* `ObserveBuildGo` watches this cluster's mirror, whichever answers
   first. The read is what lets a peer cluster's GO mean anything at all here.
2. **The arbiter hands us the claim.** Registering as a candidate outlives the grant wait, and the
   arbiter grants the moment the build falls free — whether the builder finished or died. On a grant
   the follower **re-reads the durable witness**: a GO already there means the winner finished and
   this process stands down without re-baking; no GO means the builder went away mid-build and this
   process bakes. The grant is the level-trigger; the witness is the verdict.

**Standing down is not bookkeeping.** `RequestBuildClaim`'s registration survives until a grant
consumes it, so a follower that reached its answer some other way is later handed a build it will
never run — holding the claim at `Planning`, never heartbeating, and never taken over, because its
process is alive and the takeover rule below defends a live holder by design (a stopped heartbeat on
a live process means busy, not dead). The *next* image's fingerprint could then never be claimed and never get a GO, holding
every silo's readiness down. `WithdrawBuildClaim` removes the registration and gives back a claim
that raced it; both halves are conditional, so they are safe against a concurrent grant.

There is deliberately **no timer and no bound bolted onto the wait**. A bound would end the wait by
guessing, and a follower that guesses "the build finished" certifies a share it never probed — a
silent wrong answer, strictly worse than a hang that announces itself. Both doors are level-triggered
on durable state, exactly like the arbiter (and see #1366 for why the poll clock went).

**Starting the listener is a separate decision.** It would make the GO propagate promptly across
clusters instead of at the arbiter's next pass, but it changes behaviour for every partitioned-PG
deployment and wants its own justification and measurement. The follower is correct without it.

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
