# @meshweaver/client-web — the browser + React-Native mesh client (gRPC-web)

The transport client for platforms that **can't do the bidi `Open`** — browsers and React Native have no
HTTP/2 duplex and no Node `http2`, so `@meshweaver/client` (Node/Bun, `@grpc/grpc-js`) won't run there.
This package speaks the server's **gRPC-web split** instead, behind the *same* surface — `observe` /
`post` / `watch` — so it drops straight into the renderer's `GrpcAreaSource` as a `MeshConnectionLike`.

```
  browser / React Native                          .NET mesh (portal)
  ┌─────────────────────────┐   server-stream     ┌────────────────────────────┐
  │ @meshweaver/client-web  │◄═══ Connect ════════│ MeshGrpcService.Connect     │  mesh → client (receives)
  │  (Connect-ES, gRPC-web) │──── Deliver ════════▶│ MeshGrpcService.Deliver     │  client → mesh (sends)
  └─────────────────────────┘   unary             └────────────────────────────┘
        one connection_id ties the two halves into one duplex participant connection
```

## Why a split (and not the bidi `Open`)

A single gRPC **bidi stream IS one mesh participant connection** — that's what `@meshweaver/client` uses on
Node. But gRPC-web (the only gRPC a browser/RN fetch can speak) **cannot do client-streaming or bidi**. So
the server exposes the duplex as two gRPC-web-safe halves (see `mesh.proto`, `MeshGrpcService`):

- **`Connect`** — *server-streaming* `mesh → client`. Its first frame is an **ack carrying a `connection_id`**;
  every frame after is a `receive` (an `IMessageDelivery` as JSON, exactly the SignalR `ReceiveMessage`).
- **`Deliver`** — *unary* `client → mesh`. Each call quotes the `connection_id` so the server injects the
  delivery onto the right participant connection.

This client hides that split: one `connect()` opens the `Connect` stream, caches the `connection_id`, and
routes every send through `Deliver`. Callers see the same three primitives as the Node SDK.

## Use

```ts
import { connect } from "@meshweaver/client-web";

const mesh = await connect("https://atioz.meshweaver.cloud", { token: "mw_…" });

// request / response — correlated by RequestId
const resp = await mesh.observe("mesh/main", "QueryRequest", { query: "nodeType:Story" });

// fire-and-forget
mesh.post("ACME/Stories/42", "PatchDataRequest", { path: "ACME/Stories/42", change: { content: { done: true } } });

// live stream — demuxed by streamId
for await (const change of mesh.watch("@app/Home", "s1", "SubscribeRequest", { reference: { area: "main" } })) {
  handle(change.message);
}

mesh.close();
```

### Rendering a live layout area

The whole point: feed `@meshweaver/react`'s renderer. `MeshWebConnection` *is* a `MeshConnectionLike`, so:

```ts
import { connect } from "@meshweaver/client-web";
import { GrpcAreaSource } from "@meshweaver/react/core";

const mesh = await connect(url, { token });
const source = new GrpcAreaSource(mesh, "@app/Home", { area: "main" });
await source.start();            // folds the live stream into {areas,data}
// <RenderArea> against `source` — see clients/react-native/src/live.ts
```

## Security

The bearer token rides in gRPC-web call metadata (an interceptor sets `Authorization: Bearer …`). The
**server** validates it and stamps every write with the caller's identity — a client-claimed identity is
never trusted. Same contract as every other participant.

## Codegen

The client is generated from the **one canonical** `mesh.proto` that lives with the C# server — no copy:

```bash
npm run gen      # buf generate ../../src/MeshWeaver.Hosting.Grpc/Protos  →  src/gen/mesh_pb.ts
npm run build    # gen + tsc
```

`src/gen/` is git-ignored (a build artifact); `gen` runs automatically before `build` / `typecheck` / `prepack`.
Uses Connect-ES (`@connectrpc/connect`, `@connectrpc/connect-web`) + protobuf-es (`@bufbuild/protoc-gen-es`).

## Runtime requirement on React Native

gRPC-web server-streaming needs a `fetch` whose **response body is a readable stream**. Browsers have this
out of the box. On **React Native / Hermes**, install a streaming-fetch polyfill (e.g. `react-native-fetch-api`
with `react-native-polyfill-globals`, plus `TextEncoder`/`TextDecoder`) before `connect()`. The unary
`Deliver` works on stock RN fetch; only the `Connect` receive-stream needs the polyfill. This client is
transport-correct as written — only the platform `fetch` capability differs.

## vs the Node SDK

| | `@meshweaver/client` (Node/Bun) | `@meshweaver/client-web` (browser/RN) |
|---|---|---|
| RPC | bidi `Open` | `Connect` (server-stream) + `Deliver` (unary) |
| Transport | `@grpc/grpc-js` (HTTP/2) | Connect-ES gRPC-web (fetch) |
| Surface | `observe` / `post` / `watch` | **same** |
| `MeshConnectionLike` | yes | yes |
| Codegen | none (proto loaded at runtime) | buf + protobuf-es |
