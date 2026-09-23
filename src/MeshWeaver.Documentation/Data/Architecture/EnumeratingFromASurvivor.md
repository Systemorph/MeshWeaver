---
Name: Enumerating from a Survivor
Category: Architecture
Description: A recycle derives its work from the hub it is tearing down. The synchronous prologue runs while that hub is whole; every asynchronous leg after it resolves services from a closed DI scope — and the failure reads as the recycle's own success.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12a9 9 0 1 0 9-9"/><path d="M3 4v5h5"/><path d="M12 8v4l3 2"/></svg>
---

# Enumerating from a Survivor

A recycle tears a hub down. Anything the recycle needs to *work out* — which other addresses to
reach, what the network of dependents is — has to be derived from somewhere, and the obvious
somewhere is the hub being recycled: it is the one holding the request, it knows its own address,
and its services are right there.

That works for as long as the derivation is synchronous, and **the derivation is not synchronous.**

## The window

`MessageHub.HandleDispose` runs the `RecycleCascade` seam and then calls `Dispose()`:

```text
HandleDispose
 ├─ CascadeRecycle(request)     ← the seam. Returns as soon as the pipeline is SUBSCRIBED.
 └─ Dispose()                   ← RunLevel walks down to Dead
       └─ DisposalCompleted
             └─ HostedHubsCollection.CloseScopeWhenDisposed → the hub's Autofac scope is CLOSED
```

The comment at the seam says the network is derived "while this hub is still whole enough to compute
it". True — of the *prologue*. The seam's pipeline is a chain of cross-hub queries composed as
`Concat`, so only the first leg's `Subscribe` happens on the recycle's turn. Every leg after it
subscribes on a pool thread, milliseconds later, after `Dispose()` has completed and the scope has
been closed. There is no race to lose: by construction the later legs run against a dead scope.

What they reach for is ordinary and invisible:

| Resolved from the dying hub | Reached through |
|---|---|
| `AccessService` | `MeshService.StampViewer` → `CaptureContext()`, on **every** `Query<T>` call |
| `IoPoolRegistry` | `MeshQuery.QueryPool`, which the query's `Subscribe` runs on |
| `JsonSerializerOptions` | `hub.JsonSerializerOptions`, used to type the payloads |

Each of those is an `AutofacServiceProvider.GetService` on a closed `LifetimeScope`, which throws
`ObjectDisposedException: Instances cannot be resolved and nested lifetimes cannot be created from
this LifetimeScope as it (or one of its parent scopes) has already been disposed.`

## Why it is worse than an error

The cascade is careful: a leg that cannot be read is carried to the end as *incomplete* rather than
collapsing into an empty answer, and reported at `Error`. That is exactly right, and it is still not
enough, because the recycle **succeeds** everywhere a caller can see it:

- the operator's `recycle` returns;
- the definition hub really does go down;
- the fan-out really does reach the addresses that were derived;
- and the instance hubs — the whole reason the cascade exists — keep serving the assembly they were
  born with, with no error of their own and nothing to grep.

Measured on `memex.systemorph.com`: a recycle of `Hosting/TriageItem` recycled **0** addresses with
one leg incomplete. The type's dependents happened to be empty, so the one leg that failed was the
type's own instances, and the entire point of the operation was silently skipped.

## The rule

**The set is derived once, from a survivor — and so is every READ that derives it.** The cascade
already applied that rule to the outbound `DisposeRequest`s ("a dying hub cannot deliver its own
last frame") and posts them from the mesh's node-operation issuing hub. The read half needs the same
treatment, and its seam is the mesh's **read-issuing hub**:

```csharp
// NodeTypeRecycleCascade.DependencyNetwork
var reader = hub.GetMeshHub().ReadIssuingHub();
var logger       = reader.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(…);
var meshService  = reader.ServiceProvider.GetRequiredService<IMeshService>();
var accessService = reader.ServiceProvider.GetService<AccessService>();
```

`ReadIssuingHub()` is the right survivor for three independent reasons, and picking a different one
gives up one of them:

- it is **hosted by the mesh hub**, so the recycled hub's teardown cannot touch it;
- it is **off the router**, so the router is never an end of a delivery;
- it is **not** the node-CRUD execution hub, so these reads do not queue behind every write in
  flight — the `/api/content` 503 shape documented in
  [CQRS and Content Access](../CqrsAndContentAccess) and its neighbours.

`hub.GetMeshHub()` is safe on an already-dead hub: it walks `Configuration.ParentHub`, which resolves
from the *parent's* provider (the mesh's, still alive) and is cached.

## Testing it

The property is *"the enumeration does not depend on the passed hub's DI scope"*, and it has two
halves, because the hub can die in either gap:

1. **composition** resolves the services, and
2. **subscription** runs the legs.

Both are covered by `ARecycleCascadeEnumeratesFromASurvivorTest`, and neither is a race. The
production shape is deterministic if you compose while the hub is whole, dispose it, and only then
subscribe. The second case calls the method when the hub is already down.

Two things keep those cases honest:

- The precondition is **asserted, not assumed** — a resolve from the dead hub must throw. If the
  scope-closing order ever changes, the cases would otherwise pass having exercised nothing.
- Every case asserts the instance path is **in** the network, not merely that the answer is
  "complete". An empty network is complete too, and a cascade that answered "complete, 0 addresses"
  for every input is the silent form of the very defect being fixed.

And a third case runs the ordinary live path, so a failure that made the enumeration read *nothing*
could not hide behind the other two.

## 🚨 Hoisting the resolve does NOT fix this, and that is the part worth remembering

[Disposed Scopes and Dying Hubs](../DisposedScopeAndDyingHubs) catalogues this family and gives R3 —
*a continuation resolving from a scope its own pipeline closed* — the correction **hoist, never
guard**: capture the service at the top of the method, while the scope is alive, and the continuation
uses the captured instance.

That correction is **already applied here and is not sufficient.** `DependencyNetwork` resolved its
three services eagerly, in its own synchronous prologue, exactly as R3 prescribes. It still threw.

The reason is a property of the service, not of the call site:

| Lifetime | What a hoist captures | Does the hoist survive the scope? |
|---|---|---|
| **Mesh-lifetime singleton** (`IoPoolRegistry`, `IMeshChangeFeed`, `IMeshNodeStreamCache`) | the one process-wide instance | **Yes** — only the lookup scope was short-lived |
| **Scoped per hub** (`IMeshService`) | an instance **built for, and holding, that hub** | **No** — it re-resolves from that hub on every call |

`MeshService` takes `IMessageHub` in its constructor and reaches back through it on every single
`Query<T>`: `StampViewer` → `CaptureContext()` → `hub.ServiceProvider.GetService<AccessService>()`.
So hoisting a per-hub scoped service **relocates** the throw from the continuation into the service's
own method body, where no amount of hoisting at the call site can reach it.

So R3's rule needs its companion clause:

> **Hoist when the service outlives the scope. When the service IS the scope — anything registered
> per hub — hoisting is not the fix; resolve from a survivor instead.**

The two rules agree on the underlying question, which R3 states as *"what does this resolve stand in
front of"*. Here it stands in front of the only work the operation exists to do, and the service that
would perform it is bound to the thing being destroyed.

## Where else this shape lives

The generalisation is not "never resolve in a continuation" — that rule is false, and hoisting has
its own failure modes (R3's own counter-example: hoisting a logger put a possible throw *ahead of* a
collectible-ALC lease release, turning a lost log line into a guaranteed leak). The question is
narrower and answerable:

> **Does this pipeline outlive the hub whose provider it resolves from — directly, or through a
> service that hub owns?**

Wherever the answer is yes — a teardown that derives work, a write that reports after its caller is
gone, a watcher armed on the way out — the resolve belongs to a survivor. `HostedHubsCollection`'s
own commentary names this the `ObjectDisposedException` straggler class, "whose one escape onto a
scheduler thread is the anonymous *Catastrophic failure* that reds an otherwise green shard".

## The same shape on the WRITE side: a release create issued from the hub its compile recycles

A request/response has a second dependency on the hub it is issued from, and it is not the DI
scope: **the reply is addressed to the issuer.** If the issuer tears down with the request
outstanding, its Quiescing phase waits out its budget and then cancels every pending callback with
`HubDisposedBeforeResponseException` — while the target goes on doing the work. The caller is told
"failed" about a write that lands.

That is issue #5358. After a successful compile, the NodeType hub's settle cut the release with
`IMeshService.CreateNode` resolved from **the NodeType's own hub**; `IMeshService` is scoped per hub,
so its issuing hub was that hub, and a compile's success is exactly what gets a NodeType hub recycled.
On memex-cloud `Marketing/Event` compiled (644 → 648), the settle's own create and the
post-condition's re-cut both failed one second apart with *"Hub Marketing/Event was disposed before
the response arrived (request type CreateNodeRequest, target portal/nodeops-…)"*, and the node was
left advertising a build no release names.

The survivor for a write is the mesh's **node-operation hub** (`portal/nodeops-{meshId}`, via
`NodeTypeBuildState.ReleaseIssuingHub` = `hub.GetMeshHub().NodeOperationIssuingHub()`):

- it is hosted by the mesh hub, so no per-node teardown reaches it;
- it is the documented seam for a node **lifecycle write**; the create it issues is the
  self-addressed exchange every mesh-singleton's node CRUD already is
  ([A Request a Hub Sends to Itself](../SelfAddressedRequests)), so there is no routing leg and no
  reply leg for a teardown elsewhere to cut;
- the **read-issuing** hub is NOT the right survivor here: it registers no handlers and exists for
  bounded reads. A write's reply belongs behind the writes queued ahead of it.

What moved with the create, because the settle is a detached subscription that outlives the hub:

| step | before | now |
|---|---|---|
| release create (`TryCreateReleaseNode`) | `IMeshService` + `AccessService` from the NodeType hub | from the survivor |
| re-cut (`ReleasePostCondition.Restore`) | read `AccessService` through the NodeType hub. That hub is typically already down here, since Restore runs on the first attempt's failure | from the survivor |
| activity terminal write, delivery-hold notice, the stamp's gate / fingerprint / change feed | resolved lazily out of the NodeType hub's scope | from the survivor |
| compile-state stamp | own-node write, always | own-node write **while the hub still serves**; once it is winding down, written to the node BY PATH from the survivor, so routing delivers it to the owner's next activation instead of into a workspace that can commit nothing |

`AReleaseCreateSurvivesItsNodeTypeHubTest` parks a real create at the owner with a creation
validator, disposes the issuing stand-in (and proves its scope is closed), then lets the create
through: `Landed` with the fix, `HubDisposedBeforeResponseException` without it. Its second case
runs the re-cut against an already-dead hub.

**What this does not change:** what disposed the NodeType hub in the first place. Nothing in the
incident's two log lines names the poster of that `DisposeRequest`, and the fix does not depend on
knowing it. A settle that no longer dies with its hub is correct whoever recycled it.

## See also

- [Hub Disposal Model](../HubDisposalModel) — what a `DisposeRequest` tears down, and the cascade
- [Stale State Until Recycle](../StaleStateUntilRecycle) — why an un-recycled activation is
  indistinguishable from a bad fix
- [Disposed Scopes and Dying Hubs](../DisposedScopeAndDyingHubs) — the five roots of this symptom,
  the tiered 204-site sweep, and two hoists that made things worse. This page is R3's companion
  clause
- [Access Context Propagation](../AccessContextPropagation) — why a read carries an identity across
  `Subscribe` boundaries at all
