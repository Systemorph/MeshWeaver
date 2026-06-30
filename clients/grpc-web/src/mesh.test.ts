import { describe, expect, it } from "vitest";
import { Mesh } from "./mesh";
import { fakeMeshTransport } from "./testTransport";

// Drives the ergonomic Mesh ops surface against the same in-memory Connect+Deliver fake the connection
// tests use — proving search/get/watch/patch/create/delete/move/copy compose correctly over the transport.
describe("Mesh ops (gRPC-web, in-memory)", () => {
  it("search returns the query results", async () => {
    const mesh = await Mesh.connect("memory://", { transport: fakeMeshTransport() });
    const results = await mesh.search("nodeType:Story namespace:ACME");
    expect(results).toHaveLength(1);
    expect(results[0].path).toBe("ACME/Stories/1");
    mesh.close();
  });

  it("get reads the first node state off the live stream", async () => {
    const mesh = await Mesh.connect("memory://", { transport: fakeMeshTransport() });
    const node = await mesh.get("ACME/Stories/42");
    expect(node.path).toBe("ACME/Stories/42");
    expect(node.content.done).toBe(false);
    mesh.close();
  });

  it("watch yields node states demuxed by streamId", async () => {
    const mesh = await Mesh.connect("memory://", { transport: fakeMeshTransport() });
    const it = mesh.watch("ACME/Backlog");
    const first = await it.next();
    expect(first.done).toBe(false);
    expect(first.value.path).toBe("ACME/Backlog");
    mesh.close();
  });

  it("create round-trips a node-lifecycle request and returns the response", async () => {
    const mesh = await Mesh.connect("memory://", { transport: fakeMeshTransport() });
    const resp = await mesh.create({ path: "ACME/New", nodeType: "Story" });
    expect(resp.path).toBe("ACME/New");
    mesh.close();
  });

  it("delete / move / copy resolve (acked by the mesh hub)", async () => {
    const mesh = await Mesh.connect("memory://", { transport: fakeMeshTransport() });
    await expect(mesh.delete("ACME/Old")).resolves.toBeUndefined();
    await expect(mesh.move("ACME/A", "ACME/B")).resolves.toBeUndefined();
    await expect(mesh.copy("ACME/A", "ACME/C")).resolves.toBeUndefined();
    mesh.close();
  });
});
