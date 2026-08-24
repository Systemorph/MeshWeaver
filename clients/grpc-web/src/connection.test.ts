import { describe, expect, it } from "vitest";
import { connect } from "./connection";
import { fakeMeshTransport } from "./testTransport";

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

describe("unsolicited typed messages (onMessage)", () => {
  it("dispatches a server-initiated NavigationRequest to its handler, after pending/stream demux", async () => {
    const { fakeMeshTransport } = await import("./testTransport");
    const { MeshWebConnection } = await import("./connection");
    const conn = await MeshWebConnection.connect("https://portal.example", { transport: fakeMeshTransport() });
    const seen: string[] = [];
    const off = conn.onMessage("NavigationRequest", (d) => seen.push(String(d.message["uri"])));
    // Round-trip an Echo so the fake's stream is live, then hand-feed an unsolicited delivery the
    // way the server sends one: no RequestId, no streamId — just a typed message to this node.
    await conn.observe("any", "EchoRequest", { text: "warm" });
    (conn as unknown as { onFrame(r: string): void }).onFrame(
      JSON.stringify({ id: "x", sender: "Store", target: conn.address,
        message: { $type: "NavigationRequest", uri: "Store/Catalog/Catalog?category=Games" }, properties: {} }),
    );
    expect(seen).toEqual(["Store/Catalog/Catalog?category=Games"]);
    off();
    (conn as unknown as { onFrame(r: string): void }).onFrame(
      JSON.stringify({ id: "y", sender: "Store", target: conn.address,
        message: { $type: "NavigationRequest", uri: "Elsewhere" }, properties: {} }),
    );
    expect(seen).toEqual(["Store/Catalog/Catalog?category=Games"]); // unsubscribed — not delivered
    conn.close();
  });
});
