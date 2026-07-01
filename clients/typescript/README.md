# @meshweaver/client (Node / Bun)

Connect a Node or Bun process to the MeshWeaver mesh over gRPC and use mesh features natively — the
foreign-language counterpart of the MAUI / Blazor-WASM participant. The process attaches at its own
`node/<uuid>` address and speaks the same `IMessageDelivery` envelopes the SignalR transport carries,
framed over a gRPC bidirectional stream.

The split mirrors the C# and Python sides:

| Layer | C# | Python | Node/Bun |
|---|---|---|---|
| Transport | `MeshWeaver.Connection.SignalR` | `meshweaver.connection` | `MeshConnection` |
| Operations | `MeshWeaver.AI.MeshOperations` | `meshweaver.mesh.Mesh` | `Mesh` |

## Setup

```bash
npm install        # or: bun install
npm run build      # tsc -> dist/ (Bun can run the TS directly without building)
```

The `.proto` is loaded at runtime from the C# server's canonical copy
(`../../src/MeshWeaver.Hosting.Grpc/Protos/mesh.proto`) via `@grpc/proto-loader` — one contract, no
codegen step. Override the path with the `MESHWEAVER_PROTO` env var.

## Use

```ts
import { Mesh } from "@meshweaver/client";

const mesh = await Mesh.connect("https://atioz.meshweaver.cloud", { token: "mw_..." });
const stories = await mesh.search("nodeType:Story namespace:ACME");  // mesh -> JS
mesh.patch("ACME/Stories/42", { content: { processed: true } });     // JS -> mesh
for await (const node of mesh.watch("ACME/Backlog")) handle(node);   // live stream (AsyncIterable)
mesh.close();
```

## Status

- **Transport / correlation / stream-demux** — complete and protocol-faithful (mirrors
  `MeshGrpcService` + the SignalR client).
- **Envelope wire-shape** (`envelope.ts`) and **operation request types** (`mesh.ts`, marked `WIRE:`)
  — pinned to the mesh's `IMessageDelivery` JSON. Confirm the exact `$type` + property casing against
  a sample from the C# round-trip test; then each `WIRE:` spot is a one-line fix.

Security: the API token travels in gRPC call metadata; the server validates it and stamps every write
with your identity. A forged client-side identity is never trusted.
