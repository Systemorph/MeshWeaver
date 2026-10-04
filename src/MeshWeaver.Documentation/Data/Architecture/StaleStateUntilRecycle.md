---
Name: Stale State Until a Recycle
Category: Architecture
Description: A per-node hub binds its configuration once and is then pinned by address, so merged, sealed, rolled and restarted do not make a fix live at an address that is already up. The update contract — after a DisposeRequest the next activation binds the newest build the record names, and the old one keeps working until then — the suite that proves it, the four automatic recyclers, and the one sanctioned surface.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12a9 9 0 0 1 9-9 9 9 0 0 1 6.7 3"/><path d="M21 3v6h-6"/><path d="M21 12a9 9 0 0 1-9 9 9 9 0 0 1-6.7-3"/><path d="M3 21v-6h6"/></svg>
---

# Stale State Until a Recycle

> **"The grain keeps serving old state until we send a dispose request."** — maintainer, 2026-09-17

A per-node hub — on Orleans, a grain activation — resolves its `HubConfiguration` **once**, while it
is activating, and is then pinned by address. `MeshNodeHubFactory` says so at the seam that does it:

> *"The binding above is made ONCE and then PINNED by address: routing short-circuits on
> `GetHostedHub` for an already-hosted address and never resolves the path again, so **nothing
> re-reads the NodeType for the hub's whole lifetime**."*
> — `src/MeshWeaver.Graph/Configuration/MeshNodeHubFactory.cs`

Every change a portal absorbs **while it keeps running** — a merge that syncs in, a package installed
or updated, a NodeType recompiled in place, a new build published — changes what the **next**
activation would load, and reaches a live one through nothing at all. The failure has no signal of
its own: no error, no warning, no log line, nothing to grep. The address answers promptly and
answers the old thing. That is the missing half of three recurring stories — *"merged and rolled is
not loaded"*, *"the NodeType recompiled but the hub still serves the previous assembly"*, and *"the
fix is in the image and invisible in the running mesh"*.

🚨 **A pod restart or a roll is NOT in that list, and saying otherwise is the easy overstatement.**
Restarting a pod tears down every activation it hosts, so those addresses re-activate and re-read on
their next access; a roll replaces the pods and does the same. What a roll leaves open is the
*second* half of this page — what the fresh activation then **chooses** — plus anything that was
pinned at process start. The gap this page is about is the running portal, not the restarted one.

## What an activation pins, and what stays live

**Live, and needing no recycle:** content. `GetMeshNodeStream(path).Update(...)` and node CRUD route
to the owning activation, and `MeshDataSource` holds a long-standing own-node subscription that
picks up every emission; activation only *seeds* the workspace from the node routing already read.
Cross-hub updates travel the change feed. This is why editing, chat and every data-bound view work
without anyone recycling anything. See
[CQRS — Queries vs. Content Access](/Doc/Architecture/CqrsAndContentAccess).

**Pinned for the activation's lifetime:**

| What | Why a change to it cannot reach a live activation |
|---|---|
| The **NodeType binding** the hub was born with | Resolved once in `MeshNodeHubFactory.ResolveHubConfiguration`, then short-circuited by `HostedHubsCollection`. *"If the node acquires — or changes — its type while the hub is alive, the hub keeps serving the configuration it was born with for the rest of its lifetime"* (`NodeTypeRebindWatcher`, #1104). |
| The **compiled assembly and its collectible load context** | Resolved during that same enrichment (`CompilationCacheService.GetOrCreateLoadContextForPath`); the configuration delegate's closure captured types from that ALC. Only idle deactivation or a recycle ever replaced the binding. |
| **Layout-area streams**, the stale-build `$Banner` subject, the armed watchers | Hub-owned, created in `WithInitialization` / `hub.Set(...)`, destroyed with the hub. |
| Anything read from **outside the mesh** — the store modules under `/data/modules` | *"Pinned at process start and never swapped"* — so this one is pinned to the PROCESS, not the activation, and a recycle does not touch it; a restart is its only lane, by construction. |
| A node written **around** the mesh — a raw `psql UPDATE` on a live portal | The write never passes the workspace, so no activation hears about it. (Which is why that is forbidden — see [Postgres Schema Architecture](/Doc/Architecture/PostgresSchemaArchitecture).) |

🚨 **Tests are not exempt.** A shared fixture — the canonical case being an Orleans `TestCluster` with
one silo per xUnit collection — keeps activations between tests, and *"the grain caches its config,
Test B activates a different node at the same path, reads stale state from Test A, and fails for
reasons entirely unrelated to its own logic"* is the same pinning seen from the test side. Disposing
the hubs a test created is one of the two halves of
[Test State Isolation](/Doc/Architecture/TestStateIsolation); the exemption is a fixture that builds
and tears down its own mesh, not "it is a test".

**"Published" therefore does not imply "every instance is running it".** The framework states the
consequence at the site that chose it:

> *"OFFER as the DEFAULT. The unconditional self-`DisposeRequest` that once lived here recycled
> every live instance on every publish … **Stated consequence: an instance whose viewer never clicks
> keeps serving the OLDER assembly indefinitely** — safe, since that build worked, but 'published'
> no longer implies 'every instance is running it'."*
> — `NodeTypeEnrichmentHelpers.ArmStaleAssemblySelfHeal`

## Tearing the activation down is the only thing that makes it re-read

A `DisposeRequest` posted to the address ends the activation; demand routing re-creates the hub from
its node on the next access (`.WithReactivationOnDemand()`, declared at the single funnel every
per-node activation passes — Monolith routing *and* `MessageHubGrain`). The fresh activation
re-resolves the NodeType, re-runs adopt-or-compile, rebuilds the data context and re-arms the
watchers. There is no partial refresh and no invalidation hook: the lifetime of an activation is the
unit.

### The one framework surface — dispose-only

**`hub.RecycleNode(path, reason: "…")`** — `src/MeshWeaver.Mesh.Contract/HubRecycleExtensions.cs`.
It posts a `DisposeRequest` and waits. It requests **no compile**; the fresh activation binds
whatever the store and the adoption lane then offer.

- **Cold.** The `DisposeRequest` is posted on *subscribe*, never at call time, so a composed-but-
  unsubscribed chain cannot silently tear a hub down.
- **The wait is a read, not a poll.** `GetMeshNode` already treats a `ShuttingDown` NACK as
  *"recycling, NOT absent"* and re-probes on its own paced loop inside the caller's budget, so for
  every caller this class documents the method emits the node as served by the **re-activated**
  address. No timer, no watchdog, no sleep; over budget it errors with `AddressRecyclingException`
  rather than emitting a stale nothing. 🚨 **One documented exception, and it is the caller this
  class already excludes:** from the ROOT hub the dispose and the read hop to *different* off-router
  hubs (`portal/nodeops-{meshId}` and `portal/reads-{meshId}`), whose action blocks are independent,
  so the read can reach the target first and be answered by the still-live activation — and the
  method then emits before a fresh one exists. The ordering holds because both deliveries leave the
  *same* hub, which is true for every non-router caller and false for that one.
- **It defers while an install holds the root.** `WhenNoInstallHoldsRoot` / `PackageRootInstallLeases`
  is the ONE implementation of that rule, never a second copy: *"While an install holds a root, a
  recycle aimed at that root waits — it is never dropped, never retried on a timer, and no bound
  anywhere is widened."* The lease is taken through `Observable.Using`, so it releases on completion,
  on error and on unsubscribe; there is deliberately no timer that force-releases one. It scopes to
  the root path **exactly**, never the subtree, because an install *waits on* the per-type rebuilds
  beneath its root and deferring those would deadlock.
- 🚨 **The caller's hub must OUTLIVE the target.** A dying hub cannot be relied on to deliver its own
  last frame, so a recycle driven from the hub being recycled loses its confirmation, its redirect,
  or both — structurally, not by a race you can re-order away (#2202). Callers pass a surviving hub:
  the portal circuit's, a session hub, an MCP hub, a test client.

### The operator / agent surface — dispose, plus a FORCED rebuild when the target is a NodeType

The **`recycle` verb** (`MeshOperations.Recycle`) backs the `recycle` MCP tool, a node's **Recycle**
menu entry and `mw recycle <path>`. It checks `Update` permission on the target (a refusal is an
*answer* rendered in the operation's envelope, not a fault), waits on the same install lease, and
broadcasts a cache invalidation on the change feed. And **only when the target is a NodeType node**
(`IsNodeTypeNode` — content is a `NodeTypeDefinition`, or `nodeType` is the NodeType path) it stamps
`RequestedReleaseAt` **and `RequestedReleaseForce = true`** before the dispose. Recycling an ordinary
page or instance stamps nothing and recompiles nothing — it is a plain teardown.

🚨 **That forced flag is what makes the operator verb a rebuild rather than a re-bind**, and it is the
opposite of an adoption. `NodeTypeCompilationHelpers` short-circuits on it:

> *"A FORCE MEANS 'BUILD THE LIVE SOURCE', NOT 'SERVE ME WHATEVER A BUNDLE STILL RESOLVES' … until
> now this branch asked the bundle sources again regardless. On any mesh whose bundle still resolved,
> a force therefore re-adopted the very bytes the operator was trying to replace … That is how the
> stale prebuilt in #2813 could not be forced off a node whose live source was already fixed."*
> — #2818

So on a NodeType the operator recycle is the *"rebuild from what is there now"* remedy. The stamp is
also *sequenced* before the dispose, never merely issued alongside it: when the dispose won that
race, the reactivated hub re-ran its source query against a half-invalidated state, matched zero
`Code` nodes and recompiled the **pre-fix** source.

🚨 **The Compile button is not a recycle.** It writes the same `RequestedReleaseAt` /
`RequestedReleaseForce` stamp through `GetMeshNodeStream(hubPath).Update` and posts **no**
`DisposeRequest`. It rebuilds the type; it does not re-bind a live instance.

### Who may recycle a system-owned type — and what an operator does instead

The `Update` check is not a formality that a global admin passes. **A global admin is a platform
admin, not a data superuser** ([Access Control](/Doc/Architecture/AccessControl) → "The Admin
partition"): the grant lives in `Admin/_Access` and reaches neither a space nor a system-owned
partition. Measured on the control instance on 2026-09-26: `Hosting/_Access` gives `system-security`
the Admin role and gives `rbuergi`, a global admin, **Viewer + Commenter**. So `recycle
@Hosting/TriageItem` is refused with `RecycleDeniedMessage`, and that is the rule working, not a
gap in it. Recycling a NodeType also stamps a FORCED release on it, which is a write to a node the
package owns, so it has to be the package owner's act.

The sanctioned ways to re-bind a type in `Hosting` (or any other package-installed, system-owned
partition), in order of preference:

1. **Let the platform do it.** A package install or update recycles the types it wrote, with the
   dependency cascade ("recycle the main bit"), as the system identity. On the control instance a
   type that releases a new build has its live activations recycled by a `recycle` job, and a
   platform admin may enqueue one (see "Recycling a type" below). A **roll** ends every
   activation on the pods it replaces. If a fix to a Hosting type is merged, the path is the
   module's publish → seal → roll, never a hand recycle.
2. **If the running activation is stuck and no install or roll is due**, the operator action is a
   governed `Hosting/InstanceAction`. The fleet's rule is that cluster and instance operations go
   through that API. Its **`Recycle`** kind (MeshWeaver.Plugins, `Hosting/RecycleAction`) recycles
   ONE address, a NodeType with its dependency network or any node, as system. It runs behind one
   approval on the action node, which binds the target, and it records the requester, the approver
   and the required `Reason` on the `DisposeRequest`. It runs in-process on the instance it
   targets. No lane reaches another instance's mesh, so a recycle there is filed on that
   instance's own mesh. A `Restart` (a pod restart, which ends every activation on that pod)
   remains the coarse governed equivalent. When the hubs keep serving a space that should not
   exist at all — a stranded partition whose compile watcher and build queue loop on every boot —
   recycling cannot help, because every re-activation re-reads the same rows: its **`DeleteSpace`**
   kind (`Hosting/DeleteSpaceAction`) disposes the space's hubs and removes the space itself, behind
   one approval of the plan it parks with.
3. **Break-glass elevation** — audited and time-boxed, a person's call — is the only way to hold a
   write on a system-owned partition. It is described, not built. Granting a standing Update on
   `Hosting/_Access` to a person is exactly the standing access the Admin model rules out.

Until the denial message was corrected it ended *"Ask someone with write access to the node (or a
platform admin)"*. That sent a caller to a role which, in a system-owned partition, is refused the
same way.

🚨 **A client's cached tool schema is not the server's.** On 2026-09-26 an agent session attached to
the control instance before its roll to `ci.9332` still listed `recycle` as `path`-only. The server
by then took `reason` (Plugins `abcf92d71`, in `1470fbf3`). An MCP client reads the tool list when it
connects, so after a roll, reconnect before concluding that a parameter is missing from the
deployed surface.

### Recycling a type — every live activation, on every process

A new build of a NodeType reaches only the activations created after it; the existing per-node hubs
stay on the previous build until something disposes them. **`ActivationRecycle`**
(`MeshWeaver.Graph`) is the framework's type-scoped recycle: *"dispose every LIVE activation whose
bound NodeType is T"* (optionally narrowed to some addresses, or to the activations under one path).

- **The request is a node, and the node is the broadcast.** `ActivationRecycle.Request(hub, request)`
  writes `Admin/_Recycle/{id}` (`nodeType: ActivationRecycle`) as System. Every process hears that
  commit on its `IMeshInvalidationFeed` — PostgreSQL LISTEN/NOTIFY in production, the in-process feed
  on a monolith — so the request reaches every silo without a cluster-singleton grain.
- **Arming the listener does not resolve mesh services.** It runs in the mesh hub's buildup, when
  resolving `IMeshService` can ask DI for that same hub and wait on the buildup action itself.
  The listener resolves `IMeshService` and `AccessService` only when a request arrives, after startup.
- **Each process disposes what IT hosts.** The agent on every mesh hub walks that process's
  `HostedHubsCollection`, keeps the per-node hubs whose `NodeTypePathHolder` names a requested type
  and that are still `Started`, and posts one `DisposeRequest` each — off the router, through
  `NodeOperationIssuingHub()`, with the request's reason (sanitised onto one line). No index of
  stored instances is read, so a type with ten thousand instances and three open pages disposes
  three hubs, and a cold address is never touched. Disposals go out in batches of 20, 500 ms apart,
  and each target goes through its normal quiesce.
- **Each process reports.** It writes `{request}/{process}` (`ActivationRecycleReport`: matched,
  disposed, the addresses) as System. The request plus its reports are the audit: who asked, when,
  why, which types, and what each silo did.
- **Once.** A process handles a request path once, and a hub already tearing down is never selected,
  so no activation is disposed twice.
- **Always type-scoped.** A request names at least one NodeType; `Paths` (exact addresses) and
  `UnderPath` (one subtree) only narrow it, and the filters combine conjunctively, so an address
  recycle never reaches a same-type sibling.
- **Refused, with nothing written:** no reason, no NodeType, a blank entry, an unknown NodeType, the
  definition type `NodeType` (it would recycle every definition hub) or the feature's own
  `ActivationRecycle` / `ActivationRecycleReport` types, and a request id that is not letters, digits
  and `-` (at most 80). An id is a dedupe key: an existing request at that id is answered, not
  rewritten.
- **Only System's requests are acted on.** Each process re-validates what it reads and refuses a
  request node not created by System — a platform admin, who holds `Admin`, cannot bypass the checks
  by writing `Admin/_Recycle` directly.

The request is a platform write. On the control instance it is issued by the build queue as System:
the `recycle` job kind (MeshWeaver.Plugins, `Hosting/Queue`) runs it, either when a NodeType of the
estate releases a new build or when a platform admin enqueues one over MCP (`enqueue_recycle`).
Enqueueing is a platform action; it grants the admin no read or write on the partition, and the job,
not the admin, holds the rights. The direct `recycle` verb above is unchanged and remains the
`Update` holder's tool, including its forced rebuild.

Pinned by `ActivationRecycleTest` (monolith: two live activations of T and one of U, exactly the two
disposed, re-activation on a fresh hub, redelivery, refusals) and `TwoSiloActivationRecycleTest` (both silos' activations,
one report per silo whose counts match what each hosted).

### Three rules for posting one, each paid for

- **Off the router.** Issue through `hub.NodeOperationIssuingHub()`. A `DisposeRequest` posted off the
  DI-injected `IMessageHub` — which in the mesh's root container *is* the router — leaves stamped
  `Sender = mesh/{id}` and puts node-hub lifecycle on the one action block that must never queue.
  Measured in production 2026-09-16 01:10:13Z as `ROUTER_TRAFFIC ORIGIN: DisposeRequest was POSTED
  with the mesh hub as sender` (#4463, merged as #4477). The seam is the identity function for every
  hub whose address type is not the mesh type, so an ordinary caller is byte-for-byte unaffected. A
  **self-directed** post (a hub recycling itself, `o.WithTarget(thatHub.Address)`) is structurally
  excluded from that rule — issuing it from a different hub would misdeliver it — which is what makes
  the three self-healing watchers below legitimate; the installer's root recycle is not self-directed
  and goes through the seam like any other routed teardown.
- **The root mesh hub refuses.** `MessageHub.HandleDispose` returns `Ignored()` for
  `Address.Type == "mesh"`: that hub is a process-lifetime DI singleton, and disposing it over the
  bus timed every node operation out at 60 s until the process restarted (the mesh-wide outage of
  2026-06-10). Host teardown disposes it directly instead, so refusing the message path blocks no
  legitimate shutdown.
- **Carry a `Reason`.** It is free text written by the poster and printed by the target's
  `[QUIESCE-START]`. Omitted, it renders as `reason not stated by the caller` — a named answer rather
  than a blank, but still no answer. #3510 spent six occurrences and four bake seals establishing
  which of three self-posting recyclers took a package root down, because the log could say
  *"requested by itself — a rebind or self-heal recycle"* and nothing more: one sentence covering
  three states. The poster always knows why; only the log did not.

  🚨 **And for two releases the operator surfaces could not honour that rule** (#4782): `Recycle` took
  a path and nothing else, so an absolute in `AGENTS.md` was unsatisfiable from the places an operator
  actually reaches for. An instruction the named surface cannot obey trains readers to skip the rule
  rather than to follow it, which is a worse outcome than not having stated it. Closed in two steps —
  `MeshOperations.Recycle(path, reason)` (#4952, an **overload**, because a defaulted parameter
  replaces the signature an already-built in-mesh module compiled against), then the surfaces:
  `POST /api/mesh/recycle` takes `reason` on its own `RecycleBody`, and `mw recycle <path> --reason "…"`
  carries it.

  Two properties of that plumbing are deliberate and easy to undo by accident:

  - **A new record, not a widened `PathBody`.** `/compile` and `/diagnostics` bind the same shape and
    have no use for a reason, and a field that means nothing on two of three routes is a field callers
    guess about.
  - **The framework's sentence is KEPT and the operator's APPENDED**, never substituted — they answer
    different questions, and a reader working backwards from a `[QUIESCE-START]` wants both. A blank
    reason is refused at exactly one place (`MeshOperations.RecycleReason`, parameterised over `null`,
    `""` and `"   "`), so the wire and the CLI deliver what they were given rather than normalising it:
    two deciders would mean the wrong one is the one nobody reads. A `Reason` that is present and empty
    prints as nothing and is **worse** than the fallback, because it looks like a caller who answered.

A recycle is announced to the subscribers it is about to orphan. `Dispose()` makes that announcement
as its FIRST statement, **while the hub is whole**, because the teardown itself is silent by
construction — a dying owner reaching up the hub tree for a last word resurrects the activation it is
retiring (#2533 / #2551; the mechanism is in
[Hub Disposal Model](/Doc/Architecture/HubDisposalModel)). 🚨 It hung on `HandleDispose` until
2026-09-20, which keyed it on "was there a routed request" rather than on "is an ancestor taking me
with it" — so an **Orleans deactivation**, a direct `Dispose()` of an address that IS coming back,
told its live subscribers nothing and every click they sent afterwards was discarded (#3986,
[Refusing a Lost User Action](/Doc/Architecture/RefusingALostUserAction)).

## ✅ The next activation binds the NEWEST build — the update contract

> *"After disposerequest, new version must be loaded"* and *"old version must continue working"*
> — maintainer, 2026-10-03

Since 2026-10-03 this is a **guarantee**, proven by a dedicated suite rather than hoped for:

1. **A live activation keeps serving the build it activated on** until a `DisposeRequest` reaches
   it. Nothing re-binds it mid-flight — a publication, an unrelated write, a pin edit all leave it
   exactly as it was (unless `Modules:AutoRecycleOnStaleBuild` posts that dispose for it — see the
   recyclers below).
2. **The activation after the dispose binds the newest build the type's record names** — the
   newest that is published, installed and loadable for THIS framework identity — for a node-native
   compile and for an adopted prebuilt bundle alike. It is resolved by **identity** (the record's
   content-hashed `LatestAssemblyPath` and its `LatestAssemblyMvid`), never by whichever file
   happens to answer for the store key: `IAssemblyStore.TryGetBuildPath`.
3. **A same-key byte change counts as new.** The store is content-addressed: different bytes for
   the same `(nodeTypePath, version)` get their own content-hashed name, so the record's path moves
   with the bytes and every path-keyed reader — the stale-build watcher included — sees a new build.
   The old file is never overwritten, so a hub still holding it keeps serving.
4. **When nothing newer can be loaded yet, the old build keeps serving and the record says why** —
   never a park, never an overlay over a working build: a source edit not yet released reads
   `IsDirty`; a compile that failed reads `CompilationStatus = Error` + `CompilationError` with the
   last good build still named; a build for another framework identity is refused with
   `NodeTypeBuildIdentity.RefusalReason` naming both identities, and the type rebuilds for this
   one.

**What was wrong before, and why "a recycle re-binds the same local copy" was true.** The
filesystem assembly store — the one every AKS replica shares at `/data/assembly-cache` — was
**first-write-wins per `(nodeTypePath, version)`**: a second publication of DIFFERENT bytes at the
same key (a prebuilt bundle adopted at the node version a compile already used, a second platform
generation compiling the same version, any recompile that did not move the version) wrote nothing
and handed back the FIRST build's path, while the publisher stamped the NEW build's MVID from the
bytes it held. Every later activation resolved the old file, the bind-time identity check
(#2471) refused it and rebuilt from the mesh's source, the rebuild's own `Put` was handed the old
file again, and once the retry budget was spent the activation bound the old bytes for its
lifetime. On a `Modules:RequirePrebuilt` mesh, or for a bundle-only update whose source the mesh
does not hold, no rebuild could ever produce the new build — so no number of `DisposeRequest`s
loaded it. That is the state measured on memex on 2026-08-26 over 30+ minutes and six recycles
with every surface reporting success, and it is why a same-path build mismatch
(`StaleBuildKind.ServedBuildIsNotPublished`) still carries no recycle link: on a LEGACY record —
one stamped before the fix, whose path names the old bytes and whose MVID names the new — the
named bytes are not in the store at all, and the remedy is a rebuild or a republish, not a re-bind.

> *"A PATH is not an identity … so the path can match perfectly while the bytes behind it differ per
> replica."* — `ServedBuildIdentity`. The fix makes the path an identity again (content hash) and
> resolves by the MVID besides.

### Where it is proven — `test/MeshWeaver.Updates.Test`

One suite whose whole job is updates, booting a real mesh (Monolith, and a two-silo Orleans cluster
for the mixed-replica case). Each scenario is one test, falsified against the defect it guards; the
suite references the loading, compilation, adoption and disposal projects directly, so
`affected-tests.py` owes it on every change to any of them.

| # | Scenario | Test |
|---|---|---|
| 1 | node-native N → N+1: live hub keeps N, dispose binds N+1 | `NodeTypeUpdateTest.ANewBuild_IsBound_AfterTheDispose_AndNotBefore` |
| 2 | prebuilt bundle N → N+1 at the SAME store key | `BundleUpdateTest.ABundleAdoptedAtTheSameKey_IsBound_AfterTheDispose` — **red on main before the fix** |
| 3 | a same-key byte change moves the path; the old bytes stay in place | `BundleUpdateTest.ASameKeyByteChange_MovesThePath_AndLeavesTheOldBytesInPlace` — **red before the fix** |
| 4 | N+1 fails to compile: N keeps serving, the record says why | `NodeTypeUpdateTest.AFailedNewBuild_KeepsTheOldOneServing_AndSaysWhy` |
| 5 | N+1 unpublished, or built for another framework identity | `NodeTypeUpdateTest.AnUnpublishedNewBuild_IsNamed_AndTheOldOneKeepsServing`, `ForeignIdentityUpdateTest` |
| 6 | N reads N+1-shaped content, N+1 reads N-shaped content | `ShapeCompatibilityTest.EachBuild_ReadsTheOtherBuildsShape` |
| 7 | two builds serve side by side on a two-silo cluster | `MixedReplicaUpdateTest` |
| 8 | rollback N+1 → N, including to a build sharing N+1's key | `NodeTypeUpdateTest.ARollback_…`, `BundleUpdateTest.ARollbackToABuildSharingItsKey_…` — the latter **red with a version-keyed lookup** |
| 9 | content written under N survives N+1 and the rollback | `NodeTypeUpdateTest.ARollback_RebindsTheOldBuild_AndTheDataSurvivesBothWays` |
| 10 | no dispose, no change | `NodeTypeUpdateTest.WithoutADispose_ALiveHubNeverSwitchesBuilds` |

The store-level half is pinned in `FileSystemAssemblyStoreTest`
(`Put_same_version_different_bytes_publishes_the_new_bytes_under_their_own_name`,
`TryGetBuildPath_*`).

### A dispose is still not a fix by itself

The contract says what the re-read binds; it cannot make a build exist. **A recycle is never
itself a fix, and a second one proves nothing the first did not**: if the address still answers
the old way after one dispose, the newest build the record names IS the old one — read the
NodeType's record (`IsDirty`, `CompilationStatus`, `CompilationError`, the adoption refusal,
`LatestAssemblyPath`) instead of recycling again.

🚨 **Two things the contract does not cover.** A compiled MODULE assembly (`content.module`,
loaded from `/data/modules`) is pinned to the PROCESS, not to an activation — a dispose cannot
swap an assembly the default load context holds; the module lane's own refresh (which restarts the
instance) is its only lane, as the table at the top of this page says. And the contract decides
which build an activation BINDS, not which build gets PUBLISHED:

**an UNFORCED trigger can re-choose the same bundle.** A release request that is *not* forced
runs *adopt-before-compile*: the deployment's prebuilt bundle sources get one bounded chance to
supply the assembly first, and an adopted type then satisfies its release request without Roslyn
ever running. `Modules:VersionStrictness` decides what counts as a candidate, and its default is
**`Family`** (`Minimum` on a Development host) — a bundle sealed for another identity of the same
major line is adopted when its declared floor is satisfied and its type links resolve. So a bundle
baked against a different platform surface is a legal candidate, the question is asked again, the
same bundle is still the only answer, and the identical MVID lands.

🚨 **This is exactly what `RequestedReleaseForce` exists to defeat**, so it is not what the operator
verb or the Compile button do — both force. Where it bites is the unforced lanes (install, boot, a
watcher's own trigger), and on a `Modules:RequirePrebuilt` mesh, where there is no local compile to
fall back to at all.

🚨 **And in that unforced shape NO INSTRUMENT REPORTS IT, which is what makes it expensive.**
Measured on memex, 2026-09-17: `Approvals/Desk` was recompiled at 17:35 (v844 → v846) by a pod of the
*other* generation while the deployment was split across two images. It came out with the **same
MVID**, still stamped the foreign framework identity, and recorded
`buildProvenance: AdoptedVerified`. Nobody ran the `recycle` verb, so nothing was forced — a
boot-time compile re-adopted a prebuilt bundle, exactly the clause above. Neither of the two
instruments a reader would reach for said so: `/health`'s `content-types` records a degradation only
when a content read **degrades**, and `bake-report` reads the **shared record**, which said `Ok`. All
the reader had was a blank area and `No renderer is registered for area …` — and two hours. The
question none of them answers is *"is there a NodeType this replica cannot **serve**?"*, and the
census built to answer it — per-type outcome, with its denominator stated, and with "no sweep has
reported here" printed as its own sentence rather than as a clean one — is
[#4647](https://github.com/Systemorph/MeshWeaver/pull/4647)'s addition to `bake-report`. **Read the
outcome, not the plan, when a page is blank and the record says `Ok`.** And since the outcome census
is itself a sweep-time reading, read `bake-report`'s **`LIVE RECORD CENSUS`** sentence for the
cross-stamp shape above ([#4632](https://github.com/Systemorph/MeshWeaver/issues/4632)): it is
refolded from the NodeType catalog on every emission and names, by partition and framework
identity, every record keyed to a framework this replica does not run — with the ones stamped
**after this replica booted** counted apart, which is exactly what the 14:33 adoption by the other
generation was. See [Compiled Against Another Platform](../CompiledAgainstAnotherPlatform) → "The
live record census".

The remedies there, none of which is another dispose-only recycle:

- **force the rebuild** — the `recycle` verb or the Compile button on the NodeType, which skips
  adoption and compiles the live source;
- **rebake and republish** the package for *this* framework identity — the only remedy on a
  `Modules:RequirePrebuilt` mesh (default off — *"a PRODUCTION portal opts in because its invariant
  is 'the runtime artifact of a module is a baked DLL': a silent compile there is a distribution
  failure being papered over"*);
- tighten `Modules:VersionStrictness` to `Exact`, at the stated cost that every platform roll then
  adopts nothing until every satellite has re-sealed.

See [Execute-Time Interlock](/Doc/Architecture/ExecuteTimeInterlock) for the provenance verdicts
(`AdoptedVerified`, `AdoptedUnverified`, `AdoptionRefused`, `StaleAdopted`) — **a dispose moves none
of them** — and [Install-Time Prebuilt Adoption](/Doc/Architecture/InstallTimePrebuiltAdoption) for
the adoption lane itself.

**Three more things a recycle does not do:**

- **It clears no data.** Reactivation re-loads from the store and the own-node source re-saves what
  it loaded, so a recycle is not a delete: a node removed underneath the mesh comes back on the next
  activation, and clearing in-memory-only state takes a process restart.
- **It is one address.** Sibling hubs, other partitions, the other replicas and every process-wide
  cache keep what they have — the stream cache is one singleton *per silo*, the ALC dictionary is
  process-lifetime, and store modules are pinned at process start.
- **It cannot fix a delivery gap.** If the pod loaded old bytes because the package never reached
  that portal, no number of recycles changes what is on disk. Read the
  `[ModuleLoad] <Assembly> ← … (written=…)` line first.

One symptom has stopped needing it: a cache **failure era** on a path is now cleared by the change
feed resetting the storm breaker and evicting faulted entries (#3954), so *"recycle is no longer
load-bearing for this symptom"* — it is a redundant second cure rather than the only escape. See
[The MeshNode Stream Cache](/Doc/Architecture/MeshNodeStreamCache).

## The four automatic recyclers — real, and not a guarantee

| Recycler | Fires when | Default | Install-lease gated |
|---|---|---|---|
| `NodeTypeRebindWatcher` | a post-commit change event retypes this path away from the bound type (#1104) — `Take(1)` | always armed | yes |
| `WithOverlaySelfHeal` | a DEGRADED activation (error overlay, slow-path timeout, unresolved pin, missing bytes, compiling) reaches a usable build, by version advance, grace timer or the re-evaluation ladder — `Take(1)` | always, on degraded branches | no — by design: it recycles per-TYPE hubs an install is often waiting for |
| `ArmStaleAssemblySelfHeal` — convergence branch | a NodeType publishes a usable build whose assembly **path** advanced past the bound one, throttled by a 10 s settle window — `Take(1)` | **config:** `Modules:AutoRecycleOnStaleBuild`; the code default is OFF (absent or unparseable means OFF), the AKS chart sets it **true** | no |
| `PackageInstaller.SettleRetypedRoot` | an install retyped its own package root and the in-package type has a loadable build | always, for such an install | no — it *is* the lease holder, and deferring a holder against its own lease deadlocks |

The first three post to **their own instance hub**, which is the sanctioned shape and the reason
`DisposeRequest` carries a `Reason` at all — `[QUIESCE-START]` could otherwise only say *"requested
by itself — a rebind or self-heal recycle"*, one sentence covering three states. `SettleRetypedRoot`
is the exception: it issues off-router through `hub.NodeOperationIssuingHub()` at the **package
root** and then waits for that root to answer, so it is a routed teardown, not a self-post.

🚨 **Read the EFFECTIVE `Modules:AutoRecycleOnStaleBuild`, not the code default.** They disagree, and
each reading is wrong about the other's portals:

- **Off** (the code default, and any portal that does not set it) — the watcher only publishes a
  **banner offering** a recycle, so **a portal is a mixture of old and new assemblies for as long as
  viewers do not click**. That default was chosen because the unconditional self-recycle it replaced
  made publication frequency equal restart frequency and tore hubs out from under users mid-edit; the
  2026-08-25 Store outage is what the cost looks like.
- **True** — which is what `deploy/aks/values.aks.yaml` sets **fleet-wide**, so the portals this
  repository deploys converge by themselves and no viewer has to click.

A same-key byte change now MOVES the path (the store is content-addressed), so it converges like
any other new build. A path that did not move while the MVIDs disagree is a LEGACY record (stamped
before 2026-10-03) — a build *mismatch* to report, not one to converge on (#2471).

## Where this bites, and what to do about it

**Finishing a change set.** A pull request that changes something a per-node hub serves — a
NodeType's source, node content shipped in an image or a bake, a layout area compiled in the mesh —
is not proven live by the merge, the seal, the image tag or the roll. **Exercise the feature against
the running address.** If it still answers the old way, recycle that address and exercise it again;
if it *still* answers the old way, the activation was never the problem and a third recycle will not
find it. Name the addresses a deploy has to recycle in the PR body — a recycle nobody knew to run is
indistinguishable from a fix that did not work.

**Debugging.** Before tracing a message flow for a fix that "did not take", ask whether the
activation predates the change. [Debugging Message Flow](/Doc/Architecture/DebuggingMessageFlow)
separates a hang from a wrong answer; this page separates a wrong answer from a *stale* one, which
needs no debugging at all.

**Operating a portal.** Operations go through the control instance's Hosting API, never the cluster —
see [Operating from the Portal](/Doc/Architecture/OperatingFromThePortal). A `Roll` moves the image;
it does not follow that every address is serving it.

## Related

- [Hub Disposal Model](/Doc/Architecture/HubDisposalModel) — the teardown state machine, and the
  recycle announcement that owes orphaned subscribers a goodbye.
- [Retiring an Activation](/Doc/Architecture/RetiringAnActivation) — a hub that retires itself after
  a transient init fault must fail its gate *before* it disposes.
- [Teardown Layers](/Doc/Architecture/TeardownLayers) — work finishes, nothing is forced.
- [The MeshNode Stream Cache](/Doc/Architecture/MeshNodeStreamCache) — one handle per path per silo,
  and the failure era a recycle no longer has to clear.
- [Node Type Compilation](/Doc/Architecture/NodeTypeCompilation) — what a fresh compile chooses, the
  banner, and the ALC generations a recycle releases.
- [Compile Cache Input Freshness](/Doc/Architecture/CompileCacheInputFreshness) — identical inputs
  reuse their existing DLL, which is the other half of "the same bytes come back".
- [Router Traffic Detection](/Doc/Architecture/RouterTrafficDetection) — why the teardown is issued
  off the router.
