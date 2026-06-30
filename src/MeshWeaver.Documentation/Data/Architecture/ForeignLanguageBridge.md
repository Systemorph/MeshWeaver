# Foreign-Language Bridge (Python, Bun/Node) over gRPC

How a non-.NET process joins the mesh and uses mesh features natively. The model is the **MAUI / Blazor-WASM participant**: a remote process attaches at its own address, speaks the mesh's `IMessageDelivery` envelopes, and becomes a first-class participant — `Post`/`Observe` requests, subscribe to live node streams, read and write nodes under its own identity. The only thing we swap is the **transport skin**: gRPC instead of SignalR.

## Core idea

A single gRPC **bidirectional stream IS one mesh participant connection** — the exact role `SignalRConnectionHub` plays for MAUI/WASM. The foreign-language package is the in-language equivalent of `IMessageHub` + `MeshOperations` speaking that stream.

```
 Python / Bun process                      Portal (.NET)
 ┌─────────────────────┐   gRPC  Open()    ┌───────────────────────────┐
 │ meshweaver SDK      │◄═════ bidi ══════►│ MeshGrpcService           │
 │  connection (xport) │   ClientFrame      │  GrpcConnectionRegistry   │
 │  mesh (operations)  │   ServerFrame      │   → IRoutingService       │
 └─────────────────────┘                    │   → the mesh (hubs)       │
        py/<uuid>                            └───────────────────────────┘
```

## The decisive choice: protobuf frames, JSON carries

We do **not** re-model the mesh's hundreds of polymorphic `$type` messages in protobuf — that schema would drift instantly. Instead:

- **Protobuf** frames + streams the connection (`mesh.proto`): a `connect` handshake and `deliver` / `receive` frames.
- The **message body stays the existing System.Text.Json `IMessageDelivery` JSON** (`RawJson`, `$type`-discriminated). This reuses 100% of `SerializationExtensions`, the `TypeRegistry`, `$type`/`$id` discriminators, and `AccessContext` serialization unchanged. The SignalR transport already ships the whole delivery as one JSON string (`DeliverMessage(string deliveryJson)`); we keep that payload verbatim and only give it typed, streamed gRPC framing.

`mesh.proto` therefore carries JSON strings, mirroring SignalR's `Connect(addressJson)` / `DeliverMessage(deliveryJson)` / `ReceiveMessage(json)` one-to-one.

## Server transport = the SignalR host, re-skinned

`MeshWeaver.Hosting.Grpc` is a near-mechanical mirror of `MeshWeaver.Hosting.SignalR`:

| SignalR | gRPC |
|---|---|
| `SignalRConnectionHub.Connect(addressJson)` | first `ClientFrame.connect` |
| `SignalRConnectionHub.DeliverMessage(json)` | each `ClientFrame.deliver` |
| `IHubContext…SendAsync("ReceiveMessage", json)` | `ServerFrame.receive` written by the per-connection pump |
| `SignalRConnectionRegistry.Authenticate(token)` | bearer token in gRPC call metadata, validated at stream open |
| `registry.Connect` → `routingService.RegisterStream(address, push)` | identical |
| `registry.Deliver` re-stamps `AccessContext` | identical |

Two differences fall out of gRPC's shape:

1. **No out-of-band push.** SignalR's `IHubContext` can push to any connection id; a gRPC bidi response stream is only writable inside the `Open` call. So each connection owns an outbound `Channel<ServerFrame>` that the `Open` call drains to the wire on **one** writer (gRPC forbids concurrent writes); the registry's push handler enqueues onto it.
2. **async/await at the boundary.** `MeshGrpcService.Open` is `async Task` exactly as `SignalRConnectionHub`'s methods are — the transport edge. Once a frame enters `GrpcConnectionRegistry` everything is reactive and runs off the boundary.

## Security invariant

The bearer API token travels in gRPC call metadata (`authorization: Bearer …`), is validated once at stream open (the same `ValidateTokenRequest` the SignalR/MCP paths use), and **every inbound delivery's `AccessContext` is overwritten server-side with the resolved identity** before it touches the mesh. A client-supplied identity is never trusted. No token ⇒ anonymous (writes cleanly RLS-denied). This is what keeps "never write as hub" intact across the foreign boundary.

## Streams ride the one connection

A stream subscription is just a message exchange: the participant posts a subscribe request as a `deliver` frame; the owner's change events come back as `receive` frames addressed to the participant. The SDK demuxes by stream id and folds `Full` → RFC 7396 merge-patches into live state — exactly what `ISynchronizationStream` does in C#. No separate RPC.

## Foreign packages

Each package = "a remote participant + the `MeshOperations` surface, in-language", built on three primitives over the bidi stream:

1. **request/response** — send a delivery, await the one whose `properties.RequestId` matches.
2. **fire-and-forget** — send a delivery.
3. **stream** — send a subscribe request, demux change events by stream id.

`get`/`search`/`watch`/`patch`/… are thin compositions of these over the existing mesh request types — a port of `MeshWeaver.AI.MeshOperations`.

- **Python**: `clients/python` (`meshweaver`) — `grpc.aio` transport, `Mesh` operations, async with a notebook-friendly surface.
- **Bun/Node**: `clients/typescript` (`@meshweaver/client`) — same surface, `AsyncIterable` streams. *(planned)*

## Build note — Apple Silicon

`Grpc.Tools` ships no macOS-arm64 `protoc`/`grpc_csharp_plugin` (only `macosx_x64`, needing Rosetta). CI (Linux) is unaffected. For local arm64 builds either install Rosetta (`softwareupdate --install-rosetta`) or `brew install grpc protobuf` — the `MeshWeaver.Hosting.Grpc` project auto-points codegen at `/opt/homebrew/bin` native binaries when present (guarded by `Exists()` so other machines fall back to the bundled tools).

## Participant reachability (monolith vs Orleans)

The server registers a participant's inbound route with `routingService.RegisterStream(address, push)` — exactly as `SignalRConnectionRegistry` does. A reply addressed to that participant is delivered by `MonolithRoutingService.RouteImpl`'s `streams` lookup. But `RouteImpl` is only reached when `RouteMessage → PathResolver.ResolvePath` succeeds with an empty remainder; a bare participant address with **no backing mesh node** resolves to NotFound first, so the `streams` check is skipped. Two paths make a participant reachable:

- **Orleans** (the production / SignalR-parity target): `RoutingGrain` consults `StreamRoutedAddressTypes`, so a `RegisterStream`'d participant is reachable. `AddGrpcHub` declares `py`/`node` there.
- **Monolith**: a participant is reached via a **hosted hub** (how Blazor circuits receive — `RouteInMesh` short-circuits on `GetHostedHub`). A bare `RegisterStream` alone is not enough.

The current transport proves inbound (request → mesh → handler) and outbound framing (the ack). Completing the mesh→participant **response** path under the monolith means giving the participant a hosted hub whose `DeliverMessage` forwards to its gRPC `Open` stream — the next step.

## Phasing

1. `mesh.proto` + `MeshWeaver.Hosting.Grpc` + a network-free transport test (drives the service over in-memory duplex streams against a real mesh; pins inbound routing + outbound framing). ← **this change**
2. Python `meshweaver`: transport + correlation + `get`/`search`/`watch`. ← **this change (skeleton; wire-shape pinned against a captured sample)**
3. Close the response path: a hosted participant hub (monolith) and/or an Orleans round-trip test (see "Participant reachability").
4. `@meshweaver/client` (Bun/Node) — port the surface.
5. Widen operations (move/copy/execute/threads) + the hosted-Code-node subprocess path (the kernel spawns `python`/`bun` for an executable Code node and hands it `MESH_GRPC_URL` + a scoped token, so in-mesh scripts reach back through the same SDK over loopback).
