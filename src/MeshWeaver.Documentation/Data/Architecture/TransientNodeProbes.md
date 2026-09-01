---
Name: Transient Node Probes
Category: Architecture
Description: A probe hub applies a NodeType's instance configuration purely to be read, and is disposed in the same breath. Its address is synthetic, so it has no mesh node — and every read seam that can be handed that address has to answer it directly instead of gating and routing it.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/><path d="M11 8v6"/><path d="M8 11h6"/></svg>
---

The portal repeatedly needs to know what a NodeType's **instances** look like — the content type, the
type registry, the JSON schema — without there being an instance to look at. The NodeType Overview's
*Data model* section, the `$Model` area, schema lookup and content validation all ask that question.

The answer is a **transient node probe**: a hosted hub created at a synthetic address, configured
with the NodeType's instance `HubConfiguration`, read once, and disposed. It is marked
`AsTransientNodeProbe()` (`MeshWeaver.Graph`), which stores a `TransientNodeProbe` marker
(`MeshWeaver.Data`) on the configuration, and it deliberately gets the data context but **not** the
per-node control plane — the own-node subscription, the persistence sampler, the compile /
release-request / sources watchers, the compile-state mirror. Those exist to serve a node for
months; on a hub that lives for microseconds they only opened a `sync/` sub-hub apiece and then
faulted as the hub was torn down out from under them (~22 unactionable error lines per probe on the
AKS portals).

## The one contract: a probe has no mesh node

A probe's address is minted with a fresh GUID for a hub nobody will ever address again:

| Probe | Address | Producer |
|---|---|---|
| Model probe | `$model-probe/{guid}` | `NodeTypeDataModelAreas.ProbeInstanceModel` |
| Schema probe | `$schema-probe/{guid}` | `MeshDataSourceExtensions` (SchemaReference handling) |
| Validation probe | `_schema_validation/{guid}` | `MeshOperations.ValidateContentWithSchema` |
| Schema-lookup probe | `_schema_lookup/{guid}` | `MeshOperations.GetContentSchema` |
| Registration probe | `content-type-registration/{guid}` | `ContentTypeRegistration.ProbeRegister` |

So *"is there a node at this path?"* has one answer, for every reader, forever: **no**. That is not a
timing statement — nothing is in flight, nothing is about to be created. It is a fact about the
address.

**The producers mint these addresses from `TransientProbeAddresses`** (`MeshWeaver.Data`), which is
also where the predicate that recognises them lives:

```csharp
var probeAddress = new Address($"{TransientProbeAddresses.ModelProbePrefix}{Guid.NewGuid():N}");
...
if (TransientProbeAddresses.IsProbeAddress(path)) { /* answered directly */ }
```

That coupling is the point. A guard whose prefix drifts from its producer's is a guard that silently
stops firing, and nothing about a probe minted under an unknown prefix looks wrong until a read on it
fails in production. Adding a new probe kind means adding its constant here, which is what keeps
`IsProbeAddress` exhaustive.

The registration probe is the worked example of the drift: it was added as a literal, so it was a
transient probe by every other measure — same marker, same lifetime, same impossibility of ever
carrying a node — while `IsProbeAddress` did not know it existed and the cache seam below did not
fire for it (Systemorph/MeshWeaver#2990). A read of one of its addresses died on
`No node found at 'content-type-registration/…'`: the #2894 failure, re-created in full by one
string literal.

## Why "read my own node" reaches a probe at all

A probe applies content written for a **real per-node hub**, where the hub's address *is* its mesh
path. Deriving a path from `Hub.Address` and reading it is therefore ordinary, correct code — a
loader reading its own node's configuration, a virtual data source reading the list it is configured
from. On the probe that same derivation collapses onto the probe's synthetic address.

This is not a content bug to be fixed in each NodeType. It is inherent in what a probe *is*: applying
instance configuration to a hub that is not an instance. The framework, not the content, has to
absorb it.

## The three own-address read seams — all must answer directly

There are exactly three ways such a read leaves content, and **all three are guarded**:

| Seam | Answer for a probe's own address |
|---|---|
| `MeshNodeStreamExtensions.GetMeshNodeOutcome` (and `GetMeshNode`) | `NodeReadStatus.Absent`, immediately |
| `IMeshNodeStreamCache.GetStream(path, options)` | an **empty stream** — no emission, immediate completion |
| `MeshNodeStreamHandle.Subscribe` — the own-node reduce behind `workspace.GetMeshNodeStream()` | an **empty stream** |

The empty stream is the stream-shaped twin of `Absent`: no emission, because there is no node;
immediate completion, because there never will be one. None of the three suppresses a fault — there
is genuinely nothing there.

The third seam is the one every own-node **watcher** subscribes to, and it was the last to say so.
It threw `InvalidOperationException("Failed to create stream")` instead: a probe is built
`startDataSources: false`, so the own-`MeshNode` reduce has no started data source to reduce from and
`ReduceManager.ReduceStream` returns null. That diagnostic named no reference, no owner and no cause,
which is precisely why three deliberate fault classifiers all missed it — see below.

The cache seam matters more than it looks, because **it is the seam content reaches for**. Reading
the hub's own node through the process-wide cache is the only own-node read that answers *before* the
hub's init gates open, so anything feeding data-source initialization is pushed to it. From
`StoreManifestSource` (the Store catalog's `store-packages` provider), in-mesh:

```csharp
// The node is read through the process-wide IMeshNodeStreamCache — NOT the hub's own-node
// stream — because this feeds initialization, and the own-node stream only emits once the
// hub's init gates open (a deadlock if initialization itself waits on it).
var nodes = cache.GetStream(hub.Address.ToString(), options);
```

## What an unguarded seam produced

Until the cache seam was guarded, that one line produced three different failures depending on where
the read landed — all of them from correct content:

1. **A permission denial.** The cache's per-user read gate evaluated the *triggering user's*
   effective permissions on `$model-probe/{guid}`. A path that is not a node has no access
   assignments, so the answer was `Permission.None` and the read threw
   `User 'x' lacks Read permission on '$model-probe/…'` — an actionable-looking sentence that was
   false in both halves: nothing was denied to that user, and there was nothing to deny.
2. **A routing NotFound.** Past the gate, the upstream `SubscribeRequest` routed to a node that does
   not exist and the read died on `DeliveryFailure: No node found at '$model-probe/…'`. Worse, a
   point read of an absent path opens the storm-breaker on it — see
   [CQRS and Content Access](/Doc/Architecture/CqrsAndContentAccess).
3. **A read that never ends.** In-process, where routing *does* find the probe's hosted hub, the
   subscribe was delivered to the probe itself and parked behind the `DataContextInit` gate that the
   probe's own initialization opens — a cycle whose only exit was the read's full budget.

Each of those reaches `VirtualDataSource`'s error arm, which reports
*"the provider for collection 'X' faulted … frozen at its last emission"* and leaves the probe
serving a data model with that collection missing. The user sees a Data model section rendered
without one of its collections; the log sees one error per affected viewer. Both of the first two
shapes were observed on the same production pod in the same second
(Systemorph/MeshWeaver#2894 and its sister log incident).

**Impersonation is not the fix here.** Stamping the read as System converts shape 1 into shape 2 — it
moves the failure, does not remove it, and would collapse a per-user read to a privileged one. The
address is what is wrong with the read, not the identity on it.

## The own-node watchers, and the error a clean boot used to write

"A probe gets the data context but not the per-node control plane" is stated above, and
`MeshDataSource` honours it for the watchers *it* installs (`SubscribeToOwnDeletionInit` returns
early on a probe). But the **Activity Control Plane is installed by the adopter, not by
`MeshDataSource`** — `KernelContainer` and every Activity-shaped NodeType call
`hub.WatchControlPlane(...)` from their own `WithInitialization`. A guard placed per-adopter is a
guard the next adopter will not have, so the ACP watcher went on being installed on every probe.

On a probe it has nothing to watch, and it said so at the wrong volume. Every mesh start wrote one
line per swept Activity-shaped NodeType:

```text
Error MeshWeaver.Kernel.Hub.KernelContainer: ActivityControlPlane subscription faulted on
content-type-registration/65936dcbcfa348bd921eb49885dea848 — re-establishing
```

`SubscribeWithReEstablish` classifies a watcher fault into four buckets, three of which are terminal:
own hub disposing (`HubDisposingException` naming this address), poisoned own content (a
deserialization `MeshNodeStreamException`), and own node gone (a routing `NotFound`). A bare
`InvalidOperationException` matches none, so it fell through to the fourth — *genuinely transient* —
which means `LogError` plus a one-second re-establish timer. `Information` and above ships to Loki
and `Error` is what operators alert on, so a routine boot-time step reported itself as a production
fault, once per type, forever. The timer was armed against a hub already being disposed: the #991
`TimerQueue`-root shape, kept harmless only by the ordering accident that `RegisterForDisposal`
disposes a late registrant immediately.

An Activity NodeType that *also* declares a content type showed the other face of the same root: its
own-node reduce succeeded far enough to construct a `SynchronizationStream`, whose constructor always
calls `GetHostedHub(sync/{id}, Always)` — into the probe's own disposal, producing exactly the
`Rejecting hosted hub creation … during disposal` warning that `startDataSources: false` was
introduced to remove.

Both are cured at the seams, not at the adopters:

- `WatchControlPlane` and `WatchSubmission` **do not install** on a hub carrying the
  `TransientNodeProbe` marker. This is the shared seam every ACP adopter goes through, so it also
  covers the ones not yet written.
- The own-node stream **completes empty** (the third seam above), which covers watchers that build
  their own source rather than going through those two — `BuildNodeType`'s claim arbiter is the
  live example.

🚨 **This is not a classification change.** *Transient* keeps meaning what it meant — the node is
alive and will come back. The cure is that the watcher is never installed on a hub for which that can
never be true, not that its fault is reported more quietly. Downgrading the line would have left the
re-establish timer armed and the sub-hub still being created.

## Rules

- **Mint every probe address from `TransientProbeAddresses`.** Never a string literal.
- **A new read seam that can be handed an arbitrary path must consult `IsProbeAddress`** before it
  gates, caches, routes, or opens a breaker on that path.
- **A watcher of the hub's OWN node is not installed on a probe.** Put the guard at the shared seam
  the adopters call, never once per adopter — and make the own-node stream itself answer empty, so a
  watcher that composes its own source is covered too.
- **A probe hub must never be used to WRITE.** With no own-node subscription and no persistence
  sampler it has no node identity and would not persist anything; the write guards are deliberately
  *not* short-circuited, so such an attempt fails loudly instead of being swallowed.
- **Reads of any REAL path from a probe are untouched.** The guard is scoped to the probe's own
  synthetic address only.

## Related

- [Subscription Ownership](/Doc/Architecture/SubscriptionOwnership) — why the probe's sub-hubs were
  the original cost.
- [CQRS and Content Access](/Doc/Architecture/CqrsAndContentAccess) — why a point read of an absent
  path is a framework defect and not merely slow.
- [Virtual Data Sources](/Doc/DataMesh/VirtualDataSources) — the provider whose fault surfaced this.
- [Access Context Propagation](/Doc/Architecture/AccessContextPropagation) — the identity the read
  inherits, and why moving it is not the answer.
- [Activity Control Plane](/Doc/Architecture/ActivityControlPlane) — the watcher that is deliberately
  not installed here, and what its four fault classes mean.
