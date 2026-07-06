---
Name: A standalone hub in Node
Category: Documentation
Description: Program a MeshWeaver hub in Node/TypeScript — its own address, message handlers, owned state — connect it to the mesh, and (as the node kernel) execute javascript/typescript Code nodes.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><circle cx="4" cy="6" r="2"/><circle cx="20" cy="6" r="2"/><circle cx="4" cy="18" r="2"/><circle cx="20" cy="18" r="2"/><path d="M6 7l4 3M18 7l-4 3M6 17l4-3M18 17l-4-3"/></svg>
---

# A standalone hub in Node

A hub on the mesh is three things: an **address** deliveries route to, **message handlers** keyed by
message type (the C# `WithHandler<T>`), and **state** the handlers own. None of that is .NET-specific.
This page programs a hub in Node/TypeScript, connects it to the mesh over gRPC, and serves it — the
Node counterpart of [A standalone hub in Python](../PythonStandaloneHub).

The working code is `clients/typescript/src/examples/hub.ts`; the SDK is `@meshweaver/client`
(`clients/typescript`).

## Define the hub

`Hub` is the model — an address, handlers by message type, owned state. A raising handler answers with
an `ErrorResponse` instead of wedging; the reply is correlated back to the sender automatically.

```typescript
import { connect, Hub } from "@meshweaver/client";

const conn = await connect("https://memex.meshweaver.cloud", { token: "mw_…", address: "node/counter" });

let count = 0;                                              // the state the handlers own
new Hub(conn)
  .register("Increment", (d) => { count += Number(d.message.by ?? 1); return ["Count", { value: count }]; })
  .register("GetCount", () => ["Count", { value: count }]);
```

Any participant now drives it over the mesh: `observe("node/counter", "Increment", { by: 5 })` returns
`{ value: 5 }`. The hub reads the mesh (`mesh.get` / `mesh.search` / `mesh.watch`) and writes back
(`mesh.patch` / `mesh.create`) exactly like a C# hub — see
[Foreign Language Integration](../../Architecture/ForeignLanguageIntegration).

## Run it as the node kernel

The same SDK ships the **node kernel** — a specialised hub at `node/node-kernel` that executes
`javascript`/`typescript` Code nodes routed to it (`CodeNodeType.ResolveKernelAddress`). It runs the
snippet in a `vm` sandbox with REPL semantics (`console.log` captured, a trailing expression is the
return value, `Inputs` exposes caller parameters; TypeScript is transpiled first) and patches the run's
Activity node — so js/ts output surfaces identically to python and C#.

```bash
cd clients/typescript
npm install && npm run build
node dist/worker.js --url https://memex.meshweaver.cloud --token mw_… --address node/node-kernel
node dist/worker.js --demo     # self-contained smoke test: execute a JS + a TS snippet
```

In a deployment it ships as a **trusted sidecar** in the portal pod (the Node gate,
`deploy/node-gate`), connecting to the trusted loopback endpoint with **no token** — reachability is
the authentication. It is feature-flagged alongside the python gate under `grpc.gates` (every language
included by default). Full model: [Python Code Nodes](../../Architecture/PythonCodeNodes).

## Related

- [A standalone hub in Python](../PythonStandaloneHub) — the same hub model in Python.
- [Python Code Nodes](../../Architecture/PythonCodeNodes) — how a Code node routes to a language worker and back.
- [Foreign Language Integration](../../Architecture/ForeignLanguageIntegration) — the gRPC bridge and every client SDK.
