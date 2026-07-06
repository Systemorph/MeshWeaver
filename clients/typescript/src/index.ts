// @meshweaver/client — connect a Node/Bun process to the mesh over gRPC and use mesh features
// natively. The foreign-language counterpart of the MAUI/SignalR participant.
//
//   import { Mesh } from "@meshweaver/client";
//
//   const mesh = await Mesh.connect("https://atioz.meshweaver.cloud", { token: "mw_..." });
//   const stories = await mesh.search("nodeType:Story namespace:ACME"); // mesh -> JS
//   // ... native JS does the work ...
//   mesh.patch("ACME/Stories/42", { content: { processed: true } });   // JS -> mesh
//   for await (const node of mesh.watch("ACME/Backlog")) handle(node); // live stream
//   mesh.close();

export { Mesh, type MeshOptions } from "./mesh.js";
export { MeshConnection, connect, type ConnectOptions } from "./connection.js";
export { type Delivery } from "./envelope.js";
export { type MeshNode } from "./types.js";
// The node kernel: execute javascript/typescript Code nodes routed from the mesh (node/node-kernel).
export { executeCode, CodeWorker, serve as serveNodeKernel, DEFAULT_WORKER_ADDRESS, type ExecResult, type ServeOptions } from "./worker.js";
// The hub programming model in Node (address + handlers by message type + state).
export { Hub, type Handler } from "./examples/hub.js";
