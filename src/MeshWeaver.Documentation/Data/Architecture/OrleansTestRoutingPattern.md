# Orleans Test Routing — Dedicated Registered Hubs

When a test sends a message through Orleans, the silo's `RoutingGrain`
needs a way to deliver the **response** back to the originating hub.
Production uses a uniform pattern for this; Orleans tests must follow
the same shape, or response messages deadlock at the routing layer.

## The rule

**Never use mesh-type addresses (`mesh/{guid}`) as routable targets in
tests.** A mesh-type hub at the test process is a hosted hub — it is
not a grain on the silo, so `pathResolver.ResolvePath(mesh/{guid})`
returns `null` and the silo's `RoutingGrain` emits `NotFound` for any
delivery targeted at it (including responses to silo-handled requests).

Every test-side hub that participates in cross-silo routing MUST be a
**dedicated registered hub** following the production Portal pattern:

1. The hub's address is a portal-style discriminator (e.g.
   `portal/{userId}`, `client/{clientId}`) — NOT a mesh-type guid.
2. The hub is created with `hub.GetHostedHub(address, …)` so the
   parent mesh hub owns its lifetime (per-user-identity sharing).
3. The hub is registered with the routing service in
   `WithInitialization`:
   ```csharp
   .WithInitialization(hub =>
       hub.RegisterForDisposal(routingService.RegisterStream(hub)))
   ```
   The registration subscribes the hub to the Orleans memory stream
   keyed by its address, so the silo can publish responses there.

The canonical production reference is
`PortalApplication.DefaultPortalConfig` (Blazor/SSR). It auto-registers
every portal hub on initialization, so the silo can route layout-stream
deltas, command responses, and synced-query notifications back to the
right circuit.

## Why mesh-type addresses fail

The silo's `RoutingGrain.RouteMessage` (see `RoutingGrain.cs:71`) has a
special memory-stream dispatch path for `portal`/`client` types:

```csharp
if (address.Type == AddressExtensions.PortalType || address.Type == "client")
{
    var s = streamProvider.GetStream<IMessageDelivery>(addressPath);
    return s.OnNextAsync(delivery)…;  // → subscriber receives via cluster-wide memory stream
}
```

For `mesh/{guid}` targets, the grain falls through to path-resolver
lookup → grain dispatch → no-such-grain → silent fallback. The fallback
also publishes to a memory stream, but only the silo's own mesh hub
typically subscribes there — the test's client-side mesh hub is a
separate hosted hub in the test process, and unless it was explicitly
`RegisterStream`'d (which mesh-type isn't, by convention), the message
is dropped.

## Cache partition keyed by GUID

When a test fixture needs an isolated, GUID-scoped data partition
shared between silo and Orleans client (single-process test cluster),
register an `IPartitionStorageProvider` with a backing store both
sides resolve to the **same instance**:

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

This mirrors production: multiple `IStorageAdapter` instances (one per
host's DI container) all pointing at the same backing store (PG in
prod, the shared dict in tests). A node created on one side is
immediately visible to the other — same single logical store.

The GUID-as-id pattern lets multiple fixtures coexist without state
bleed: each fixture's `PartitionId` keys its own backing dict.

## Putting it together: cross-silo test pattern

For a test that creates a node via the client and then operates on it
across the silo boundary:

1. **Fixture setup**: declare the shared backing dict + partition GUID
   (above). Configure both silo and client to use it.
2. **Test hub setup**: in `GetClientAsync` (or equivalent), create
   the hub at `portal/{userId}`-style address with `WithInitialization
   (hub => hub.RegisterForDisposal(routingService.RegisterStream(hub)))`.
   Do NOT use `Fixture.ClientMesh.Address` (mesh-type) as the test
   target.
3. **Test message flow**: target every request at the registered hub's
   address. Responses route back through the memory-stream subscription
   the registration created.
4. **Routing-level cross-silo operations** (e.g.
   `workspace.GetMeshNodeStream(remotePath).Update(…)`) work because
   the silo's `RoutingGrain` can resolve the remote path (shared
   backing dict) and dispatch the response back via the registered
   memory stream.

## Failure mode reference

| Symptom | Likely cause |
|---|---|
| `[ROUTE] NotFound: No node found at 'mesh/{guid}'` | Test targeted a mesh-type address instead of a registered hub. |
| `[ROUTE] NotFound: No node found at '{userPath}/_Provider/Anthropic'` immediately after `CreateNodeRequest` succeeds | Silo and client are using **different** `InMemoryStorageAdapter` instances. Apply the shared-backing-dict fix. |
| Test hangs at `GetMeshNodeStream(remotePath).Update(…)` for 30 s, then `TimeoutException` | Response target is mesh-type; rotation needs the dedicated-registered-hub pattern. |

## Production analogue

| Production | Test mirror |
|---|---|
| Portal.Distributed hosts Blazor; Portal mesh hub address is `mesh/{portalGuid}`, NEVER addressed cross-silo. | Don't use `Fixture.ClientMesh.Address` as a routing target. |
| Each user circuit's `PortalApplication` creates `portal/{userId}` via `GetHostedHub` + auto-`RegisterStream`. | Test's `GetClientAsync` creates `client/{clientId}` and auto-`RegisterStream`s it. |
| All adapter instances (silo PG adapter + portal PG adapter) point at the same PG DB via shared connection string. | All `InMemoryStorageAdapter` instances (silo + client) share the same backing `ConcurrentDictionary` via fixture-level singleton. |
| Silo dispatches portal-bound messages via Orleans memory stream keyed by `portal/{userId}`. | Silo dispatches test-bound responses via the same memory-stream mechanism, since the test hub subscribed at `client/{clientId}`. |
