// In-memory test transport — a fake Mesh mirroring the real server's gRPC-web split (Connect acks with a
// connection id then streams receives; Deliver injects a delivery and pushes the echoed response/change
// back). Shared by connection.test.ts and mesh.test.ts. Not a *.test.ts file (so its import doesn't
// re-register tests) and excluded from the published build (tsconfig.build.json) — test-only.

import { create } from "@bufbuild/protobuf";
import { createRouterTransport } from "@connectrpc/connect";
import { Mesh, ServerFrameSchema, DeliverAckSchema } from "./gen/mesh_pb";
import { newId, parseDelivery } from "./envelope";

type Frame = ReturnType<typeof create<typeof ServerFrameSchema>>;

// A minimal async push-queue: the fake Connect server-stream drains it; the fake Deliver unary feeds it.
// This is exactly the Connect+Deliver coordination the real server does, in-memory.
export class Pushable<T> {
  private readonly queue: T[] = [];
  private wake: (() => void) | null = null;
  private done = false;
  push(v: T): void {
    this.queue.push(v);
    this.wake?.();
    this.wake = null;
  }
  finish(): void {
    this.done = true;
    this.wake?.();
    this.wake = null;
  }
  async *[Symbol.asyncIterator](): AsyncIterator<T> {
    while (true) {
      while (this.queue.length) yield this.queue.shift()!;
      if (this.done) return;
      await new Promise<void>((r) => (this.wake = r));
    }
  }
}

export interface FakeMeshOptions {
  /** Observe every delivery the client sends — assert on fire-and-forget posts (e.g. PatchDataRequest). */
  onDeliver?: (d: ReturnType<typeof parseDelivery>) => void;
  /** Content the fake node-watch answers with (default `{ done: false }`). */
  nodeContent?: Record<string, unknown>;
}

// Responses correlate by RequestId == the request delivery's id; live changes carry the streamId the
// client demuxes on (both addressed back to the sender).
export function fakeMeshTransport(opts: FakeMeshOptions = {}) {
  const conns = new Map<string, Pushable<Frame>>();
  const respond = (out: Pushable<Frame>, d: ReturnType<typeof parseDelivery>, message: Record<string, unknown>) =>
    out.push(create(ServerFrameSchema, {
      kind: { case: "receive", value: JSON.stringify({ id: newId(), sender: d.target, target: d.sender, message, properties: { RequestId: d.id } }) },
    }));
  const push = (out: Pushable<Frame>, d: ReturnType<typeof parseDelivery>, message: Record<string, unknown>) =>
    out.push(create(ServerFrameSchema, {
      kind: { case: "receive", value: JSON.stringify({ id: newId(), sender: d.target, target: d.sender, message, properties: {} }) },
    }));
  return createRouterTransport((router) => {
    router.service(Mesh, {
      async *connect(req, ctx) {
        const connectionId = "test-conn-" + newId();
        const out = new Pushable<Frame>();
        conns.set(connectionId, out);
        ctx.signal.addEventListener("abort", () => out.finish());
        yield create(ServerFrameSchema, { kind: { case: "ack", value: { address: req.address, connectionId } } });
        yield* out;
      },
      async deliver(req) {
        const out = conns.get(req.connectionId);
        const d = parseDelivery(req.delivery);
        opts.onDeliver?.(d);
        if (out) {
          const streamId = d.message.streamId as string | undefined;
          const reference = d.message.reference as { area?: string; path?: string } | undefined;
          switch (d.messageType) {
            case "EchoRequest":
              respond(out, d, { $type: "EchoResponse", text: d.message.text });
              break;
            case "QueryRequest":
              respond(out, d, { $type: "QueryResponse", results: [{ path: "ACME/Stories/1", name: "S1" }] });
              break;
            case "CreateNodeRequest": {
              const node = d.message.node as { path?: string; id?: string } | undefined;
              // An id containing "fail" answers a refused create — pins the surfaced-failure path.
              if (node?.id?.includes("fail"))
                respond(out, d, { $type: "CreateNodeResponse", success: false, message: "creation refused" });
              else
                respond(out, d, { $type: "CreateNodeResponse", path: node?.path });
              break;
            }
            case "DeleteNodeRequest":
            case "MoveNodeRequest":
            case "CopyNodeRequest":
              respond(out, d, { $type: d.messageType.replace("Request", "Response"), status: "ok" });
              break;
            case "SubscribeRequest":
              if (reference?.path) {
                // A node watch — wire-faithful DataChangedEvent: a Full change carrying the whole
                // node JSON as raw inline JSON (RawJson serializes verbatim, not stringified).
                push(out, d, { $type: "DataChangedEvent", streamId, changeType: "Full", change: { path: reference.path, name: "Node", content: opts.nodeContent ?? { done: false } } });
              } else {
                // A layout-area watch — a Full snapshot in the `change` string.
                push(out, d, { $type: "DataChangedEvent", streamId, changeType: "Full", change: JSON.stringify({ areas: { main: { $type: "LayoutStackControl" } } }) });
              }
              break;
          }
        }
        return create(DeliverAckSchema, {});
      },
    });
  });
}
