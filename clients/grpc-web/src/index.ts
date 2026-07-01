// @meshweaver/client-web — connect a browser or React-Native app to the mesh over gRPC-web and feed
// a live MeshWeaver layout area, on platforms that can't do the bidi `Open` (no HTTP/2 duplex / no
// Node http2). The structural twin of @meshweaver/client (Node/Bun), built on the server's
// Connect+Deliver split. It implements the renderer's `MeshConnectionLike`, so it drops straight into
// `@meshweaver/react`'s GrpcAreaSource:
//
//   import { connect } from "@meshweaver/client-web";
//   import { GrpcAreaSource } from "@meshweaver/react/core";
//
//   const mesh = await connect("https://atioz.meshweaver.cloud", { token: "mw_..." });
//   const source = new GrpcAreaSource(mesh, "@app/MyAddress", { area: "main" });
//   await source.start();   // folds the live area stream into the renderer
//   // ... render <RenderArea> against `source` ...
//   mesh.close();
//
// Or the ergonomic operations surface (the in-language port of MeshWeaver.AI.MeshOperations):
//
//   import { Mesh } from "@meshweaver/client-web";
//   const mesh = await Mesh.connect(url, { token });
//   const stories = await mesh.search("nodeType:Story namespace:ACME");
//   mesh.patch("ACME/Stories/42", { content: { done: true } });

export { Mesh, type MeshOptions } from "./mesh";
export { MeshWebConnection, connect, type ConnectOptions } from "./connection";
export { type Delivery } from "./envelope";
export { type MeshNode } from "./types";
