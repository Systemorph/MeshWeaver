---
Name: Orleans Test Routing Pattern
Description: "How Orleans tests must use dedicated registered hubs (portal/client addresses + RegisterStream) to avoid response-routing deadlocks caused by unresolvable mesh-type guid addresses."
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
  <text x="112" y="169" font-family="sans-serif" font-size="11" fill="#fff" text-anchor="middle" font-weight="bold">Mesh-Type Hub</text>
  <text x="112" y="185" font-family="sans-serif" font-size="10" fill="#ffcdd2" text-anchor="middle" font-style="italic">mesh/{guid} — NOT registered</text>
  <rect x="36" y="246" width="152" height="50" rx="8" fill="#43a047" stroke="#2e7d32" stroke-width="1"/>
  <text x="112" y="267" font-family="sans-serif" font-size="11" fill="#fff" text-anchor="middle" font-weight="bold">Registered Hub</text>
  <text x="112" y="283" font-family="sans-serif" font-size="10" fill="#c8e6c9" text-anchor="middle" font-style="italic">client/{id} + RegisterStream</text>
  <rect x="424" y="56" width="152" height="50" rx="8" fill="#1e3a5f" stroke="#37474f" stroke-width="1"/>
  <text x="500" y="77" font-family="sans-serif" font-size="11" fill="#cfd8dc" text-anchor="middle">Target Grain</text>
  <text x="500" y="93" font-family="sans-serif" font-size="10" fill="#78909c" text-anchor="middle" font-style="italic">e.g. MeshNodeGrain</text>
  <rect x="424" y="148" width="152" height="50" rx="8" fill="#1e3a5f" stroke="#37474f" stroke-width="1"/>
  <text x="500" y="169" font-family="sans-serif" font-size="11" fill="#cfd8dc" text-anchor="middle">RoutingGrain</text>
  <text x="500" y="185" font-family="sans-serif" font-size="10" fill="#78909c" text-anchor="middle" font-style="italic">pathResolver.ResolvePath(…)</text>
  <rect x="424" y="246" width="152" height="50" rx="8" fill="#1e3a5f" stroke="#37474f" stroke-width="1"/>
  <text x="500" y="267" font-family="sans-serif" font-size="11" fill="#cfd8dc" text-anchor="middle">Memory Stream</text>
  <text x="500" y="283" font-family="sans-serif" font-size="10" fill="#78909c" text-anchor="middle" font-style="italic">keyed by client/{id}</text>
  <line x1="188" y1="81" x2="424" y2="81" stroke="#90a4ae" stroke-width="1.4" stroke-dasharray="5,3" marker-end="url(#arr)"/>
  <text x="306" y="74" font-family="sans-serif" font-size="10" fill="#90a4ae" text-anchor="middle">request</text>
  <line x1="424" y1="173" x2="188" y2="173" stroke="#e53935" stroke-width="1.4" marker-end="url(#arr-err)"/>
  <text x="306" y="165" font-family="sans-serif" font-size="10" fill="#e53935" text-anchor="middle">response → mesh/{guid}</text>
  <line x1="500" y1="106" x2="500" y2="148" stroke="#90a4ae" stroke-width="1.4" marker-end="url(#arr)"/>
  <line x1="500" y1="198" x2="500" y2="198" stroke="none"/>
  <text x="370" y="207" font-family="sans-serif" font-size="11" fill="#ef9a9a" text-anchor="middle" font-weight="bold">✗  NotFound — null</text>
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

*Response routing: unregistered `mesh/{guid}` hubs produce a silent `NotFound`; `client/{id}` hubs registered with `RegisterStream` receive responses via the Orleans memory stream.*

---

## The Core Rule

> **Never use `mesh/{guid}` addresses as routable targets in tests.**

A mesh-type hub in the test process is a *hosted hub* — not a grain on the silo. When the silo's `RoutingGrain` calls `pathResolver.ResolvePath("mesh/{guid}")`, it returns `null`, so any response targeted at that address produces a `NotFound` delivery and the test hangs.

Every test-side hub that participates in cross-silo routing must be a **dedicated registered hub** following the production Portal pattern:

| Requirement | Why |
|---|---|
| Address is a portal-style discriminator (`portal/{userId}`, `client/{clientId}`) — not a mesh-type GUID | Makes the address resolvable by the silo's `RoutingGrain` |
| Hub is created with `hub.GetHostedHub(address, …)` | Ties its lifetime to the parent mesh hub |
| Hub is registered with the routing service in `WithInitialization` | Subscribes it to the Orleans memory stream so the silo can publish responses back |

The registration step looks like this:

```csharp
.WithInitialization(hub =>
    hub.RegisterForDisposal(routingService.RegisterStream(hub)))
```

The canonical production reference is `PortalApplication.DefaultPortalConfig` (Blazor/SSR). It auto-registers every portal hub on initialization, so the silo can route layout-stream deltas, command responses, and synced-query notifications back to the right circuit.

---

## Why Mesh-Type Addresses Fail

The silo's `RoutingGrain.RouteMessage` (see `RoutingGrain.cs:71`) has a dedicated memory-stream dispatch path for `portal` and `client` address types:

```csharp
if (address.Type == AddressExtensions.PortalType || address.Type == "client")
{
    var s = streamProvider.GetStream<IMessageDelivery>(addressPath);
    return s.OnNextAsync(delivery)…;  // → subscriber receives via cluster-wide memory stream
}
```

For `mesh/{guid}` targets the grain falls through to path-resolver lookup → grain dispatch → no such grain → silent fallback. The fallback also publishes to a memory stream, but only the silo's own mesh hub typically subscribes there. The test's client-side mesh hub is a separate hosted hub in the test process, and unless it was explicitly `RegisterStream`'d — which mesh-type addresses are not, by convention — the message is dropped.

---

## Cache Partition Keyed by GUID

When a test fixture needs an isolated, GUID-scoped data partition shared between silo and Orleans client (single-process test cluster), register an `IPartitionStorageProvider` whose backing store both sides resolve to the **same instance**:

```csharp
public static class TestCacheFixture
{
    // Shared backing dict — one per test fixture, identifiable by GUID.
    public static readonly ConcurrentDictionary<string, MeshNode> Nodes
        = new(StringComparer.OrdinalIgnoreCase);
    public static readonly string PartitionId = Guid.NewGuid().ToString("N");
}

// In BOTH silo and client DI:
services.Replace(ServiceDescriptor.Singleton<InMemoryStorageAdapter>(sp =>
    new InMemoryStorageAdapter(
        TestCacheFixture.Nodes,                  // shared backing
        TestCacheFixture.PartitionObjects,
        sp.GetService<ILoggerFactory>()?.CreateLogger<InMemoryStorageAdapter>())));
```

This mirrors production: multiple `IStorageAdapter` instances (one per host's DI container) all point at the same backing store — Postgres in production, a shared dictionary in tests. A node created on one side is immediately visible to the other because there is one logical store.

The GUID-as-id pattern lets multiple fixtures coexist without state bleed: each fixture's `PartitionId` keys its own backing dictionary.

---

## Putting It Together: Cross-Silo Test Pattern

For a test that creates a node via the client and then operates on it across the silo boundary:

1. **Fixture setup** — declare the shared backing dict and partition GUID (above). Configure both silo and client to use it.
2. **Test hub setup** — in `GetClientAsync` (or equivalent), create the hub at a `portal/{userId}`-style address with `WithInitialization(hub => hub.RegisterForDisposal(routingService.RegisterStream(hub)))`. Do NOT use `Fixture.ClientMesh.Address` (mesh-type) as the test target.
3. **Test message flow** — target every request at the registered hub's address. Responses route back through the memory-stream subscription that the registration created.
4. **Cross-silo operations** such as `workspace.GetMeshNodeStream(remotePath).Update(…)` work because the silo's `RoutingGrain` can resolve the remote path via the shared backing dict and dispatch the response back via the registered memory stream.

---

## Failure Mode Reference

| Symptom | Likely cause |
|---|---|
| `[ROUTE] NotFound: No node found at 'mesh/{guid}'` | Test targeted a mesh-type address instead of a registered hub. |
| `[ROUTE] NotFound: No node found at '{userPath}/_Provider/Anthropic'` immediately after `CreateNodeRequest` succeeds | Silo and client are using **different** `InMemoryStorageAdapter` instances. Apply the shared-backing-dict fix. |
| Test hangs at `GetMeshNodeStream(remotePath).Update(…)` for 30 s, then `TimeoutException` | Response target is mesh-type; apply the dedicated-registered-hub pattern. |

---

## Production Analogue

| Production | Test mirror |
|---|---|
| Portal.Distributed hosts Blazor; Portal mesh hub address is `mesh/{portalGuid}`, NEVER addressed cross-silo. | Don't use `Fixture.ClientMesh.Address` as a routing target. |
| Each user circuit's `PortalApplication` creates `portal/{userId}` via `GetHostedHub` + auto-`RegisterStream`. | Test's `GetClientAsync` creates `client/{clientId}` and auto-`RegisterStream`s it. |
| All adapter instances (silo PG adapter + portal PG adapter) point at the same PG DB via shared connection string. | All `InMemoryStorageAdapter` instances (silo + client) share the same backing `ConcurrentDictionary` via fixture-level singleton. |
| Silo dispatches portal-bound messages via Orleans memory stream keyed by `portal/{userId}`. | Silo dispatches test-bound responses via the same memory-stream mechanism, since the test hub subscribed at `client/{clientId}`. |

---

## The Cache Hub — Why the Rotate Test Still Fails

The shared backing dict plus `RegisterStream(ClientMesh.Address)` makes just-written nodes visible to the silo's path resolver, but **response routing** for `GetMeshNodeStream(remotePath).Update(…)` still falls into the mesh-type `NotFound` trap. Here is why.

`MeshNodeStreamCache.GetEntry` opens its upstream subscription with:

```csharp
var handle = meshHub.GetWorkspace().GetMeshNodeStreamBypassCache(p);
```

The cache uses `meshHub` — the parent mesh hub — as the workspace. This means the `SubscribeRequest` it posts carries `Sender = mesh/{guid}`, the mesh hub's own address. When the silo handles the request and posts a response back, the response targets `mesh/{guid}` → `RoutingGrain.RouteMessage` sees a mesh-type address → no grain → `NotFound`.

---

## The Fix — Cache Hub as a Proper Partition

The cache hub at `cache/mesh-node-cache` must be a **real top-level hub** (not a hosted hub — no `~` notation), discoverable by routing the same way every other mesh node is: through a registered `IPartitionStorageProvider` for the `cache` namespace. There is **no hard-coded address-type check** in `RoutingGrain`.

The implementation shape:

**Step 1 — Define a static `MeshNode` for the cache hub** that auto-registers via `RegisterStream` in `WithInitialization` (the same Portal pattern):

```csharp
public sealed class MeshNodeCacheStaticProvider : IStaticNodeProvider
{
    public IEnumerable<MeshNode> GetStaticNodes()
    {
        yield return new MeshNode("mesh-node-cache", "cache")
        {
            NodeType = "CacheHub",
            State = MeshNodeState.Active,
            HubConfiguration = config => config
                .AddData()
                .WithInitialization(hub =>
                    hub.RegisterForDisposal(
                        hub.ServiceProvider.GetRequiredService<IRoutingService>()
                            .RegisterStream(hub)))
        };
    }
}
```

**Step 2 — Register the partition in DI:**

```csharp
services.AddSingleton<IStaticNodeProvider, MeshNodeCacheStaticProvider>();
services.AddSingleton<IPartitionStorageProvider>(sp =>
    new StaticNodePartitionStorageProvider(
        "cache",
        sp.GetRequiredService<MeshNodeCacheStaticProvider>(),
        description: "Mesh-node cache hub partition"));
```

**What this achieves:** the silo's `pathResolver.ResolvePath("cache/mesh-node-cache")` now returns the static node. `RoutingGrain` dispatches to a grain at that address — exactly the path every other mesh node takes. The grain activates with the static node's `HubConfiguration`, the `WithInitialization` hook fires `RegisterStream`, and the silo's memory-stream dispatch for `portal`/`client` types is irrelevant: the cache hub IS a grain on the silo, and other processes route to it via the standard grain-dispatch path.

The `MeshNodeStreamCache` class itself stays a process-local DI singleton. It opens upstream `SubscribeRequest`s with the cache hub's address as `Target` (not as `Sender`); responses to the `SubscribeRequest` flow back via the standard request/response correlation path that `OrleansRoutingService` handles for any address — no special-casing needed.

---

## Test That Proves This Works

`OrleansUserOwnedModelTest.UserOwnedProvider_RotateKey_ResolverPicksUpNewKey` is the canonical repro for this design requirement. It is currently skipped pending the cache-hub refactor. The test exercises the full flow:

1. Creates a `ModelProvider` node via the client mesh hub (handler runs locally, writes to the shared backing dict).
2. Calls `GetMeshNodeStream(providerPath).Update(rotate)` — which opens a remote subscription via `MeshNodeStreamCache.GetEntry`.
3. The silo handles the subscribe-and-rotate, then posts back the new `MeshNode`.
4. The response routes to the cache hub at `cache/mesh-node-cache` — registered and memory-stream-addressable.
5. The test asserts the post-rotate `ApiKey == "sk-rotated"` via the synced query, which sees the rotation through the shared backing dict.

The test will pass once `MeshNodeStreamCache` is refactored to use a dedicated registered hub as outlined above. Until then it is skipped with a comment pointing to this document.
