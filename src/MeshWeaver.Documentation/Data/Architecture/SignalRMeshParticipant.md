---
NodeType: Markdown
Name: "SignalR Mesh Participant — joining the mesh over a WebSocket"
Abstract: "How an external process (a .NET MAUI app, a native client, an edge service) joins the MeshWeaver mesh as a first-class node over a persistent SignalR/WebSocket connection: create an address (a portal address), register its hub in the circuit, and route messages over the socket. This is the reactive-push transport behind a native iOS portal — the counterpart to the in-process portal hub and the request/response MCP path."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#1565c0'/><circle cx='6' cy='12' r='2.5' fill='white'/><circle cx='18' cy='6' r='2.5' fill='white'/><circle cx='18' cy='18' r='2.5' fill='white'/><path d='M8 11 L16 7 M8 13 L16 17' stroke='white' stroke-width='1.5' fill='none'/></svg>"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Messaging"
  - "SignalR"
  - "Connectivity"
---

> **Read first:** [Message Based Communication](/Doc/Architecture/MessageBasedCommunication) and [Controlled I/O Pooling](/Doc/Architecture/ControlledIoPooling). This page is the network-transport counterpart: how a process that is *not* in the silo joins the same mesh.

## What a SignalR participant is

The mesh is an actor model of address-routed hubs. A process inside the silo participates in-process; the Blazor portal participates as a per-circuit hub. A **SignalR participant** is the third shape: an **external** process — a .NET MAUI app on a phone, a native client, an edge service — that opens a **persistent WebSocket** to a portal and joins the *same* mesh as a first-class node.

Unlike the [MCP/HTTP path](/Doc/Architecture/DataAccessPatterns) (request/response, the caller polls for changes), a SignalR participant gets the mesh's native semantics over the wire: it **posts and observes** like any hub, and node-stream updates are **pushed** to it. A `GetMeshNodeStream(path)` subscription on the device reacts the instant the owning hub writes the node — the same way the Blazor UI databinds. That push is what makes a **native iOS portal** possible: the device hosts the real Layout Areas and binds them to live mesh streams.

| | In-process hub | Blazor circuit | **SignalR participant** | MCP / HTTP |
|---|---|---|---|---|
| Location | silo | server (per circuit) | **external (device/edge)** | external |
| Transport | direct | circuit | **WebSocket** | HTTPS request/response |
| Updates | native | native | **pushed (reactive)** | polled |

## The shape: address → hub in the circuit → route over SignalR

It is the **same** pattern the portal uses for a circuit (`PortalApplication`) and the MCP plugin uses per caller (`SessionHubResolver`):

1. **Create an address.** Give the participant a **portal address** — `AddressExtensions.CreatePortalAddress(id)`. Using the portal address makes the participant a *portal in the mesh*, addressable like any other (`@{address}/{area}/{id}`), which is exactly what a native portal needs.
2. **Register the hub in the circuit.** The participant *is* an `IMessageHub`; register it with routing so the mesh can deliver to it: `routingService.RegisterStream(hub)` (returns an `IDisposable`, coupled to the hub via `RegisterForDisposal`).
3. **Route over SignalR.** A route handler forwards any non-local delivery down the socket; an inbound handler injects messages the socket receives back into the hub.

```
 device hub  ──post──▶  WithRoutes handler ──▶  SignalR socket  ──▶  /signalr hub  ──▶  mesh
 (portal addr) ◀─push──  ReceiveMessage     ◀──  (WebSocket)     ◀──  RegisterStream ◀──  routing
```

## Enabling it (feature flag)

The transport is gated by **`Features:SignalR`** (default `true`, like the other capability flags). When the flag is off the `/signalr` endpoint is never mapped, so no participant can connect. In the portal the mesh-build registration is unconditional (the registry singleton is inert without the endpoint); only the endpoint map is gated:

```csharp
if (features.SignalR)
    app.MapMeshWeaverSignalRHubs();
```

## The portal hub is the gateway

A participant never talks to arbitrary mesh hubs directly — it talks to the **portal**, and the portal routes onward: **`signalr client ⇒ portal hub ⇒ anyone else`**. The client's outbound route forwards every non-local delivery to `/signalr`; the server injects it via the **portal mesh hub** (`hub.DeliverMessage`, the portal's `JsonSerializerOptions`), which routes it to its target. Inbound runs the same path in reverse. That single gateway is what gives the participant the portal's identity, type registry, and serialization.

## Server side — `MeshWeaver.Hosting.SignalR`

Two registrations on the host (the previously-removed code, restored and adapted to today's API):

```csharp
// In the mesh build (e.g. ConfigureMemexMesh):
builder.AddSignalRHub();                 // AddSignalR() + the connection registry singleton

// In the request pipeline (after routing/auth), behind Features:SignalR:
app.MapMeshWeaverSignalRHubs();          // maps SignalRConnectionHub at /signalr
```

**`SignalRConnectionHub`** is the endpoint. SignalR creates a fresh Hub instance per invocation, so it holds no state — it delegates to a mesh-singleton registry:

```csharp
public sealed class SignalRConnectionHub(IMessageHub hub, SignalRConnectionRegistry registry) : Hub
{
    public const string EndPoint = "signalr";
    public void Connect(Address address) => registry.Connect(address, Context.ConnectionId);   // inbound routing
    public void DeliverMessage(IMessageDelivery delivery) => hub.DeliverMessage(delivery);      // client → mesh
    public override Task OnDisconnectedAsync(Exception? e) { registry.Disconnect(Context.ConnectionId); return base.OnDisconnectedAsync(e); }
}
```

**`SignalRConnectionRegistry`** (mesh-scoped singleton, instance state only — never `static`, see [No Static State](/Doc/Architecture/NoStaticState)) bridges routing to the socket. `Connect` registers a route for the participant's address; every delivery the mesh routes there is **pushed** to that connection. The push is genuine socket I/O, so it runs off the routing/hub scheduler and is bounded through the **Http `IIoPool`** — never a bare `async`/`Observable.FromAsync` (see [Controlled I/O Pooling](/Doc/Architecture/ControlledIoPooling)):

```csharp
public void Connect(Address address, string connectionId) =>
    routesByConnection[connectionId] = routingService.RegisterStream(address,
        (delivery, ct) => PushToClient(connectionId, delivery, ct));

private IObservable<IMessageDelivery> PushToClient(string connectionId, IMessageDelivery delivery, CancellationToken _) =>
    ioPool.Invoke(async ct =>
    {
        await hubContext.Clients.Client(connectionId).SendAsync("ReceiveMessage", delivery, ct);
        return delivery.Forwarded();
    });
```

## Client side — `MeshWeaver.Connection.SignalR`

One extension turns any hub configuration into a participant. Give the hub a **portal address** and point it at the portal's `/signalr`:

```csharp
var participant = host.CreateMessageHub(
    AddressExtensions.CreatePortalAddress("my-device"),
    config => config.UseSignalRClient("https://memex.meshweaver.cloud/signalr",
        builder => builder.WithUrl(/* … Bearer token … */)));
```

`UseSignalRClient` does three things:

- **Builds + starts the `HubConnection`** with `WithAutomaticReconnect()` and a `JsonHubProtocol` that uses the hub's own `JsonSerializerOptions` (so the mesh `Address` and `IMessageDelivery` converters round-trip).
- **Registers the address (the handshake).** On connect *and* every reconnect it calls `Connect(hub.Address)` on the server, and wires `On<IMessageDelivery>("ReceiveMessage", hub.DeliverMessage)` so pushed messages enter the hub.
- **Routes outbound.** A `WithRoutes` handler forwards any delivery whose target isn't in the hub's own host-chain over the socket — through the Http `IIoPool`, returning `IObservable<IMessageDelivery>` (not a `Task`):

```csharp
return ioPool.Invoke(async c =>
{
    var connection = await hub.ServiceProvider.GetRequiredService<Task<HubConnection>>();
    await connection.InvokeAsync("DeliverMessage", delivery.Package(), c);   // Package() makes the envelope transport-safe
    return delivery.Forwarded();
});
```

## Two things to get right

- **Serializer symmetry.** Both ends must serialize the mesh envelope with the hub's `JsonSerializerOptions` — the client sets it on its `JsonHubProtocol`; the server's SignalR must mirror it so the `Connect(Address)` handshake and `IMessageDelivery` payloads deserialize. `delivery.Package()` converts the message to a `RawJson` envelope, which keeps deliveries transport-tolerant.
- **Identity.** The WebSocket authenticates the user (Bearer token → `AccessContext`); messages injected via `DeliverMessage` must carry that identity so RLS holds on the server side. See [Access Context Propagation](/Doc/Architecture/AccessContextPropagation). The push direction runs through `IIoPool`, which is an identity hole by design — the *mesh* read/write under the participant's address is what carries identity, not the pool leaf.

## Why this is the native-portal foundation

A SignalR participant addressed with a **portal address** *is* a portal in the mesh — it can host Layout Areas and bind them to live node streams. On a phone that means: render the real MeshWeaver Razor components in-process (MAUI Blazor Hybrid) and bind them to `GetMeshNodeStream(path)` over the socket — a **native iOS portal**, reactive end-to-end, with the same components the server renders. The SignalR transport is the connective tissue; in-process rendering (decoupling the Blazor RCLs from the ASP.NET server host) is the remaining step.

## Cross-references

- [Message Based Communication](/Doc/Architecture/MessageBasedCommunication) — addresses, routing, request/response.
- [Controlled I/O Pooling](/Doc/Architecture/ControlledIoPooling) — why the push/forward leaves go through `IIoPool`.
- [MeshNodeStreamCache](/Doc/Architecture/MeshNodeStreamCache) — the shared per-path handle a participant subscribes to.
- [Access Context Propagation](/Doc/Architecture/AccessContextPropagation) — carrying the participant's identity into mesh writes.
