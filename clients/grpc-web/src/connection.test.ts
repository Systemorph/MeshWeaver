import { describe, expect, it } from "vitest";
import { create } from "@bufbuild/protobuf";
import { createRouterTransport } from "@connectrpc/connect";
import { Mesh, ServerFrameSchema, DeliverAckSchema } from "./gen/mesh_pb";
import { connect } from "./connection";
import { newId, parseDelivery } from "./envelope";

// A minimal async push-queue: the fake Connect server-stream drains it; the fake Deliver unary feeds it.
// This is exactly the Connect+Deliver coordination the real server does, in-memory.
class Pushable<T> {
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

// An in-memory Mesh that mirrors the real server's split: Connect acks with a connection id then streams
// receives; Deliver injects a delivery and pushes the echoed response/change back onto that stream.
function fakeMeshTransport() {
  const conns = new Map<string, Pushable<ReturnType<typeof create<typeof ServerFrameSchema>>>>();
  return createRouterTransport((router) => {
    router.service(Mesh, {
      async *connect(req, ctx) {
        const connectionId = "test-conn-" + newId();
        const out = new Pushable<ReturnType<typeof create<typeof ServerFrameSchema>>>();
        conns.set(connectionId, out);
        ctx.signal.addEventListener("abort", () => out.finish());
        yield create(ServerFrameSchema, { kind: { case: "ack", value: { address: req.address, connectionId } } });
        yield* out;
      },
      async deliver(req) {
        const out = conns.get(req.connectionId);
        const d = parseDelivery(req.delivery);
        if (out) {
          if (d.messageType === "EchoRequest") {
            // Respond, correlated by RequestId == the request delivery's id (addressed back to the sender).
            const resp = JSON.stringify({
              id: newId(),
              sender: d.target,
              target: d.sender,
              message: { $type: "EchoResponse", text: d.message.text },
              properties: { RequestId: d.id },
            });
            out.push(create(ServerFrameSchema, { kind: { case: "receive", value: resp } }));
          } else if (d.messageType === "SubscribeRequest") {
            // A live change carrying the streamId the client demuxes on.
            const change = JSON.stringify({
              id: newId(),
              sender: d.target,
              target: d.sender,
              message: {
                $type: "DataChangedEvent",
                streamId: d.message.streamId,
                changeType: "Full",
                change: JSON.stringify({ areas: { main: { $type: "LayoutStackControl" } } }),
              },
              properties: {},
            });
            out.push(create(ServerFrameSchema, { kind: { case: "receive", value: change } }));
          }
        }
        return create(DeliverAckSchema, {});
      },
    });
  });
}

describe("MeshWebConnection (gRPC-web split, in-memory)", () => {
  it("connects (Connect ack) and exposes the participant address", async () => {
    const mesh = await connect("memory://", { transport: fakeMeshTransport(), address: "node/test1" });
    expect(mesh.address).toBe("node/test1");
    mesh.close();
  });

  it("observe round-trips a request and correlates the response by RequestId", async () => {
    const mesh = await connect("memory://", { transport: fakeMeshTransport() });
    const resp = await mesh.observe("mesh/main", "EchoRequest", { text: "hello web split" });
    expect(resp.messageType).toBe("EchoResponse");
    expect(resp.message.text).toBe("hello web split");
    mesh.close();
  });

  it("watch demuxes a live change by streamId", async () => {
    const mesh = await connect("memory://", { transport: fakeMeshTransport() });
    const iterator = mesh.watch("@app/Home", "stream-1", "SubscribeRequest", { reference: { area: "main" } });
    const first = await iterator.next();
    expect(first.done).toBe(false);
    expect(first.value.messageType).toBe("DataChangedEvent");
    expect(first.value.message.streamId).toBe("stream-1");
    expect(String(first.value.message.change)).toContain("LayoutStackControl");
    mesh.close();
  });
});
