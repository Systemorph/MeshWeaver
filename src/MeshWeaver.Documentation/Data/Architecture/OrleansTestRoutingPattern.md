---
Name: Orleans Test Routing Pattern
Description: "How Orleans tests must use dedicated registered hubs — a stream-routed address type PLUS a RegisterStream subscription — so cross-silo responses have somewhere to land instead of being published into a stream nobody reads."
---

# Orleans Test Routing — Dedicated Registered Hubs

When a test sends a message through Orleans, the silo's `RoutingGrain` must be able to deliver the **response** back to the originating hub. Production follows a uniform pattern for this; Orleans tests must mirror it exactly — or responses deadlock silently at the routing layer.

---

<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 760 340" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto">
      <polygon points="0 0,8 3,0 6" fill="#90a4ae"/>
    </marker>
    <marker id="arr-ok" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto">
      <polygon points="0 0,8 3,0 6" fill="#43a047"/>
    </marker>
    <marker id="arr-err" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto">
      <polygon points="0 0,8 3,0 6" fill="#e53935"/>
    </marker>
  </defs>
  <rect x="0" y="0" width="760" height="340" rx="14" fill="#1a1a2e" stroke="none"/>
  <rect x="16" y="16" width="340" height="308" rx="10" fill="#0d2137" stroke="#37474f" stroke-width="1.2"/>
  <text x="186" y="40" font-family="sans-serif" font-size="13" font-weight="bold" fill="#90caf9" text-anchor="middle">Test Process</text>
  <rect x="404" y="16" width="340" height="308" rx="10" fill="#0d2137" stroke="#37474f" stroke-width="1.2"/>
  <text x="574" y="40" font-family="sans-serif" font-size="13" font-weight="bold" fill="#90caf9" text-anchor="middle">Orleans Silo</text>
  <rect x="36" y="56" width="152" height="50" rx="8" fill="#37474f" stroke="#546e7a" stroke-width="1"/>
  <text x="112" y="77" font-family="sans-serif" font-size="11" fill="#cfd8dc" text-anchor="middle">ClientMesh</text>
  <text x="112" y="93" font-family="sans-serif" font-size="10" fill="#78909c" text-anchor="middle" font-style="italic">mesh/{guid}</text>
  <rect x="36" y="148" width="152" height="50" rx="8" fill="#e53935" stroke="#c62828" stroke-width="1"/>
  <text x="112" y="169" font-family="sans-serif" font-size="11" fill="#fff" text-anchor="middle" font-weight="bold">Unregistered Hub</text>
  <text x="112" y="185" font-family="sans-serif" font-size="10" fill="#ffcdd2" text-anchor="middle" font-style="italic">never called RegisterStream</text>
  <rect x="36" y="246" width="152" height="50" rx="8" fill="#43a047" stroke="#2e7d32" stroke-width="1"/>
  <text x="112" y="267" font-family="sans-serif" font-size="11" fill="#fff" text-anchor="middle" font-weight="bold">Registered Hub</text>
  <text x="112" y="283" font-family="sans-serif" font-size="10" fill="#c8e6c9" text-anchor="middle" font-style="italic">client/{id} + RegisterStream</text>
  <rect x="424" y="56" width="152" height="50" rx="8" fill="#1e3a5f" stroke="#37474f" stroke-width="1"/>
  <text x="500" y="77" font-family="sans-serif" font-size="11" fill="#cfd8dc" text-anchor="middle">Target Grain</text>
  <text x="500" y="93" font-family="sans-serif" font-size="10" fill="#78909c" text-anchor="middle" font-style="italic">e.g. MeshNodeGrain</text>
  <rect x="424" y="148" width="152" height="50" rx="8" fill="#1e3a5f" stroke="#37474f" stroke-width="1"/>
  <text x="500" y="169" font-family="sans-serif" font-size="11" fill="#cfd8dc" text-anchor="middle">RoutingGrain</text>
  <text x="500" y="185" font-family="sans-serif" font-size="10" fill="#78909c" text-anchor="middle" font-style="italic">StreamRoutedAddressTypes?</text>
  <rect x="424" y="246" width="152" height="50" rx="8" fill="#1e3a5f" stroke="#37474f" stroke-width="1"/>
  <text x="500" y="267" font-family="sans-serif" font-size="11" fill="#cfd8dc" text-anchor="middle">Memory Stream</text>
  <text x="500" y="283" font-family="sans-serif" font-size="10" fill="#78909c" text-anchor="middle" font-style="italic">keyed by client/{id}</text>
  <line x1="188" y1="81" x2="424" y2="81" stroke="#90a4ae" stroke-width="1.4" stroke-dasharray="5,3" marker-end="url(#arr)"/>
  <text x="306" y="74" font-family="sans-serif" font-size="10" fill="#90a4ae" text-anchor="middle">request</text>
  <line x1="424" y1="173" x2="188" y2="173" stroke="#e53935" stroke-width="1.4" marker-end="url(#arr-err)"/>
  <text x="306" y="165" font-family="sans-serif" font-size="10" fill="#e53935" text-anchor="middle">response → unsubscribed key</text>
  <line x1="500" y1="106" x2="500" y2="148" stroke="#90a4ae" stroke-width="1.4" marker-end="url(#arr)"/>
  <line x1="500" y1="198" x2="500" y2="198" stroke="none"/>
  <text x="370" y="207" font-family="sans-serif" font-size="11" fill="#ef9a9a" text-anchor="middle" font-weight="bold">✗  no subscriber</text>
  <line x1="424" y1="173" x2="368" y2="173" stroke="#e53935" stroke-width="1.4"/>
  <line x1="368" y1="173" x2="368" y2="198" stroke="#e53935" stroke-width="1.4" stroke-dasharray="4,3"/>
  <text x="352" y="216" font-family="sans-serif" font-size="10" fill="#ef9a9a" text-anchor="middle">silent drop</text>
  <line x1="188" y1="271" x2="424" y2="271" stroke="#90a4ae" stroke-width="1.4" stroke-dasharray="5,3" marker-end="url(#arr)"/>
  <text x="306" y="264" font-family="sans-serif" font-size="10" fill="#90a4ae" text-anchor="middle">request</text>
  <line x1="500" y1="246" x2="500" y2="221" stroke="none"/>
  <line x1="576" y1="271" x2="576" y2="271" stroke="none"/>
  <line x1="500" y1="198" x2="500" y2="246" stroke="#43a047" stroke-width="1.4" marker-end="url(#arr-ok)"/>
  <text x="530" y="228" font-family="sans-serif" font-size="10" fill="#43a047" text-anchor="start">resolves</text>
  <line x1="424" y1="271" x2="500" y2="173" stroke="none"/>
  <line x1="424" y1="296" x2="188" y2="296" stroke="#43a047" stroke-width="1.4" marker-end="url(#arr-ok)"/>
  <line x1="500" y1="296" x2="424" y2="296" stroke="#43a047" stroke-width="1.4"/>
  <line x1="500" y1="271" x2="500" y2="296" stroke="#43a047" stroke-width="1.4" stroke-dasharray="4,3"/>
  <text x="306" y="313" font-family="sans-serif" font-size="10" fill="#a5d6a7" text-anchor="middle">✓  response via memory stream → registered hub</text>
</svg>

*Response routing: a hub that never called `RegisterStream` leaves the silo publishing into a stream nobody reads; `client/{id}` hubs registered with `RegisterStream` receive responses via the Orleans memory stream.*

---

## The Core Rule

> **A hub that must receive cross-silo replies has to be at a STREAM-ROUTED address AND registered with `RegisterStream` — the address type alone is not enough, and the registration alone is not enough.**

A hub in the test process is a *hosted hub*, not a grain on the silo. Routing reaches it only over the cluster-wide Orleans memory stream, and that requires two independent things to line up:

| Requirement | Why |
|---|---|
| The address **type** is declared stream-routed | `RoutingGrain` checks `meshConfig.StreamRoutedAddressTypes.Contains(address.Type)` and dispatches to the memory stream instead of activating a grain |
| The hub **subscribed** to that stream via `IRoutingService.RegisterStream(hub)` | Otherwise the silo publishes into a stream nobody is reading and the delivery is silently lost |
| Hub is created with `hub.GetHostedHub(address, …)` | Ties its lifetime to the parent mesh hub |

`MeshConfiguration.DefaultStreamRoutedAddressTypes` is `{ "portal", "client", "cache", "mesh" }`, declared at static-init time so no configurator ordering can make a built-in type "go missing". Modules add their own with `MeshBuilder.AddStreamRoutedAddressType(...)` — Graph registers `import`, the gRPC hosting registers its Python and Node types.

The registration step looks like this:

```csharp
.WithInitialization(hub =>
    hub.RegisterForDisposal(routingService.RegisterStream(hub)))
```

The canonical production reference is `PortalApplication.DefaultPortalConfig` (Blazor/SSR). It auto-registers every portal hub on initialization, so the silo can route layout-stream deltas, command responses, and synced-query notifications back to the right circuit.

> **🚨 Historical note — `mesh/{guid}` is no longer intrinsically unroutable.** This page previously said "never use `mesh/{guid}` addresses as routable targets in tests", on the basis of a hard-coded `address.Type == PortalType || address.Type == "client"` check in `RoutingGrain`. That check is gone: the type list is configuration, and `mesh` is in the default set. What *has not* changed is the second half — an address whose hub never called `RegisterStream` still gets a silently-dropped delivery. Diagnose on the registration, not on the address type.

---

## Why an Unregistered Hub Still Fails

The silo's `RoutingGrain.RouteMessage` decides between two dispatch paths:

```csharp
if (meshConfig.StreamRoutedAddressTypes.Contains(address.Type))
{
    var s = streamProvider.GetStream<IMessageDelivery>(addressPath);
    return PostToStream(() => s.OnNextAsync(delivery), …);  // → cluster-wide memory stream
}
// otherwise: path-resolver lookup → grain dispatch
```

For a stream-routed type the silo publishes onto the memory stream keyed by the address path — and that is all it can do. If no hub in any process subscribed at that key, nothing receives it. For a *non*-stream-routed type the grain falls through to `pathResolver.ResolvePath(...)`; when that returns `null` the delivery NACKs as `NotFound`.

**These two failures look different in the log and need different fixes**: a missing `RegisterStream` is silent, a missing node is a `[ROUTE] NotFound`.

---

## Sharing One Backing Store Between Silo and Client

A single-process test cluster runs the silo and the Orleans client in **separate DI containers**, so
each resolves its own `InMemoryStorageAdapter` — and a node created on one side is then invisible to
the other. The fix is to give both containers adapters that point at the **same backing store**,
exactly as production points every adapter at the same Postgres database.

> **🚨 The backing store must be an INSTANCE on the fixture, never a `static` field.**
> `test/MeshWeaver.PathResolution.Test/NoStaticCollectionsTest.cs` reflects over every `MeshWeaver.*`
> assembly in the test output — **test assemblies included** — and fails the build on any `static`
> mutable collection (`ConcurrentDictionary`, `Dictionary`, `HashSet`, `MemoryCache`, …). A
> `public static readonly ConcurrentDictionary` "shared backing dict" on a fixture class is a
> guard violation *and* leaks state across every test in the process. Hold the dictionary as an
> instance field on the `ICollectionFixture<>` and close over **that instance** in both containers'
> registrations; its lifetime is then the fixture's, and it dies with the cluster.

This mirrors production: multiple `IStorageAdapter` instances (one per host's DI container) all point
at the same backing store — Postgres in production, one fixture-owned dictionary in tests. Multiple
fixtures coexist without bleed because each owns its own instance; a GUID partition id per fixture is
a useful extra label, not the isolation mechanism.

---

## Putting It Together: Cross-Silo Test Pattern

For a test that creates a node via the client and then operates on it across the silo boundary:

1. **Fixture setup** — hold the shared backing store as a fixture instance field (above) and configure both silo and client containers to close over it.
2. **Test hub setup** — use `OrleansTestBase.GetClient(clientId?, userId)`. It is **synchronous** (there is no `GetClientAsync`) and it does the registration for you: `routingService.RegisterStream(client.Address, client.DeliverMessage)` at `client/{clientId}`. Don't target `Fixture.ClientMesh.Address`, whose hub is not the one registered for your test.
3. **Test message flow** — target every request at the registered hub's address. Responses route back through the memory-stream subscription that the registration created.
4. **Cross-silo operations** such as `workspace.GetMeshNodeStream(remotePath).Update(…)` work because the silo can resolve the remote path via the shared backing store, and the reply reaches the process-unique cache hub described above.

---

## Failure Mode Reference

| Symptom | Likely cause |
|---|---|
| A reply never arrives, with **no** `[ROUTE] NotFound` anywhere | The target address type is stream-routed but no hub subscribed at that key — a missing `RegisterStream`. Silence is the fingerprint. |
| `[ROUTE] NotFound: No node found at '{userPath}/_Provider/Anthropic'` immediately after `CreateNodeRequest` succeeds | Silo and client are using **different** `InMemoryStorageAdapter` instances. Apply the shared-backing-store fix. |
| Test hangs at `GetMeshNodeStream(remotePath).Update(…)`, then `TimeoutException` | Two cache hubs sharing one memory-stream key (a non-process-unique `cache/…` address), or a hub that never registered. |

---

## Production Analogue

| Production | Test mirror |
|---|---|
| Each user circuit's `PortalApplication` creates `portal/{userId}` via `GetHostedHub` + auto-`RegisterStream`. | `OrleansTestBase.GetClient()` creates `client/{clientId}` and `RegisterStream`s it. |
| All adapter instances (silo PG adapter + portal PG adapter) point at the same PG DB via shared connection string. | All `InMemoryStorageAdapter` instances (silo + client) close over one fixture-owned dictionary. |
| Silo dispatches portal-bound messages via Orleans memory stream keyed by `portal/{userId}`. | Silo dispatches test-bound responses via the same memory-stream mechanism, since the test hub subscribed at `client/{clientId}`. |
| Each process's `MeshNodeStreamCache` owns `cache/{meshHubId}`. | Same — silo and client each get their own, so replies cannot cross. |

---

## The Cache Hub — the Shipped Design

`MeshNodeStreamCache` used to open its upstream subscription off the *parent mesh hub*, so the
`SubscribeRequest` it posted carried the mesh hub's own address as `Sender` and replies had nowhere
to land. That is fixed. The cache now owns a **dedicated stream-routed hub**, and the shape is worth
knowing because it is the pattern any process-local cache with cross-silo reads should copy:

```csharp
var routingService = meshHub.ServiceProvider.GetRequiredService<IRoutingService>();
var cacheAddress = new Address("cache", meshHub.Address.Id);   // ← process-unique, NOT a fixed id
cacheHub = meshHub.GetHostedHub(cacheAddress, config => config /* … + RegisterStream in WithInitialization */);
…
var handle = cacheHub.GetWorkspace().GetMeshNodeStreamBypassCache(p);
```

Two decisions carry the whole design:

- **`cache` is a declared stream-routed address type**, in `MeshConfiguration.DefaultStreamRoutedAddressTypes`
  — not a partition with a static node and an `IPartitionStorageProvider`. The silo dispatches to the
  memory stream; there is no grain to activate.
- **🚨 The address is keyed by the parent mesh hub's `Id`, so it is unique per PROCESS.** A fixed id
  such as `cache/mesh-node-cache` would make the silo's and the client's cache hubs subscribe to the
  *same* cluster-wide memory stream, so a silo-side reply to a client-initiated `SubscribeRequest`
  can be delivered to the silo's cache hub. That hub has no sync sub-hub for the incoming
  `DataChangedEvent`'s `StreamId`, `RouteStreamMessage` returns `request.Ignored()`, and the client
  times out. Do not "simplify" this to a constant.

> **Superseded design.** An earlier revision of this page prescribed making the cache hub a real
> top-level node at `cache/mesh-node-cache` via a `MeshNodeCacheStaticProvider` +
> `StaticNodePartitionStorageProvider`, on the premise that `RoutingGrain` had no address-type check.
> Neither the provider type nor that address exists; following it would reintroduce the shared-stream
> cross-delivery bug above. It has been replaced by the description here. Likewise, this page used to
> cite `OrleansUserOwnedModelTest.UserOwnedProvider_RotateKey_ResolverPicksUpNewKey` as a skipped
> repro that would go green after the refactor — **that test does not exist**; the surviving tests in
> `OrleansUserOwnedModelTest` are `UserCreatesProvider_ThenResolverFindsKey` and
> `UserModelAndProvider_VisibleInSyncedQuery`.
