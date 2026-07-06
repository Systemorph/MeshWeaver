// Define and run a HUB in Node. A hub on the mesh is three things (C#, python, and here alike): an
// ADDRESS deliveries route to, HANDLERS keyed by message type (the C# `WithHandler<T>`), and STATE
// the handlers own. `Hub` is exactly that in Node; the runnable example below is a stateful counter
// hub other participants / agents drive over the mesh.
//
//   npm run build
//   MESH_TOKEN=mw_... node dist/examples/hub.js          # → memex.meshweaver.cloud as node/counter
//
// Then from any participant: observe("node/counter", "Increment", { by: 5 }) → { value: 5 }.
import { connect, MeshConnection } from "../connection.js";
import type { Delivery } from "../envelope.js";

/** A handler returns `[responseType, payload]` to reply (correlated to the sender), or `null` to stay silent. */
export type Handler = (delivery: Delivery) =>
  | [string, Record<string, unknown>] | null
  | Promise<[string, Record<string, unknown>] | null>;

/** The hub programming model in Node: an address, handlers by message type, owned state. */
export class Hub {
  private readonly handlers = new Map<string, Handler>();

  constructor(private readonly conn: MeshConnection) {
    conn.serve((d) => this.dispatch(d));
  }

  /** Register the handler for `messageType` — this hub's `WithHandler<T>`. Chainable. */
  register(messageType: string, handler: Handler): this {
    this.handlers.set(messageType, handler);
    return this;
  }

  get address(): string {
    return this.conn.address;
  }

  private async dispatch(delivery: Delivery): Promise<void> {
    const handler = delivery.messageType ? this.handlers.get(delivery.messageType) : undefined;
    if (!handler) return; // not one of ours — ignore quietly
    try {
      const reply = await handler(delivery);
      if (reply) this.conn.respond(delivery, reply[0], reply[1]);
    } catch (e) {
      // A raising handler answers with an error instead of wedging — errors always propagate.
      this.conn.respond(delivery, "ErrorResponse", { error: e instanceof Error ? e.message : String(e) });
    }
  }
}

/** A runnable example: a stateful counter hub. `count` is the state the handlers own. */
async function main(): Promise<void> {
  const url = process.env.MESH_URL ?? "https://memex.meshweaver.cloud";
  const address = process.env.MESH_ADDRESS ?? "node/counter";
  const conn = await connect(url, { token: process.env.MESH_TOKEN, address });

  let count = 0;
  new Hub(conn)
    .register("Increment", (d) => { count += Number(d.message.by ?? 1); return ["Count", { value: count }]; })
    .register("GetCount", () => ["Count", { value: count }]);

  console.log(`counter hub running as ${conn.address} → ${url}. Drive it with Increment / GetCount.`);
  await new Promise<void>((resolve) => {
    process.once("SIGINT", resolve);
    process.once("SIGTERM", resolve);
  });
  conn.close();
}

if (import.meta.url === `file://${process.argv[1]}`) {
  main().catch((e) => { console.error(e); process.exit(1); });
}
