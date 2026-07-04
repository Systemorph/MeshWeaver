import { describe, expect, it } from "vitest";
import { Mesh, ownerOfNamespace } from "./mesh";
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

  it("create targets the node's OWNER partition address (never 'mesh/main' — unroutable on the distributed portal)", async () => {
    const deliveries: { messageType: string; target: string }[] = [];
    const mesh = await Mesh.connect("memory://", {
      transport: fakeMeshTransport({ onDeliver: (d) => deliveries.push({ messageType: d.messageType, target: d.target }) }),
    });
    // A satellite namespace strips its trailing _Xxx segment: the OWNER's hub creates satellites
    // (the threads.ts precedent — this is how the kernel Activity under {user}/_Activity routes).
    await mesh.create({
      id: "markdown-k1",
      namespace: "roland/_Activity",
      path: "roland/_Activity/markdown-k1",
      nodeType: "Activity",
    });
    const create = deliveries.find((d) => d.messageType === "CreateNodeRequest");
    expect(create?.target).toBe("roland");
    mesh.close();
  });

  it("create THROWS on a refused create instead of resolving silently", async () => {
    const mesh = await Mesh.connect("memory://", { transport: fakeMeshTransport() });
    await expect(
      mesh.create({ id: "will-fail", namespace: "ACME", path: "ACME/will-fail", nodeType: "Story" }),
    ).rejects.toThrow("creation refused");
    mesh.close();
  });

  it("ownerOfNamespace strips trailing satellite segments only", () => {
    expect(ownerOfNamespace("roland/_Activity")).toBe("roland");
    expect(ownerOfNamespace("acme/Story/x/_Thread")).toBe("acme/Story/x");
    expect(ownerOfNamespace("acme/Project")).toBe("acme/Project");
    expect(ownerOfNamespace("")).toBe("");
  });

  it("delete / move / copy resolve (acked by the target node hub)", async () => {
    const mesh = await Mesh.connect("memory://", { transport: fakeMeshTransport() });
    await expect(mesh.delete("ACME/Old")).resolves.toBeUndefined();
    await expect(mesh.move("ACME/A", "ACME/B")).resolves.toBeUndefined();
    await expect(mesh.copy("ACME/A", "ACME/C")).resolves.toBeUndefined();
    mesh.close();
  });

  it("delete/move/copy target the NODE's own hub (never the unroutable 'mesh/main')", async () => {
    const deliveries: { messageType: string; target: string }[] = [];
    const mesh = await Mesh.connect("memory://", {
      transport: fakeMeshTransport({ onDeliver: (d) => deliveries.push({ messageType: d.messageType, target: d.target }) }),
    });
    await mesh.delete("ACME/Old");
    await mesh.move("ACME/A", "ACME/B");
    await mesh.copy("ACME/A", "ACME/C");
    const targetOf = (type: string) => deliveries.find((d) => d.messageType === type)?.target;
    expect(targetOf("DeleteNodeRequest")).toBe("ACME/Old"); // the node's own path
    expect(targetOf("MoveNodeRequest")).toBe("ACME/A"); // the SOURCE (MeshOperations.Move parity)
    expect(targetOf("CopyNodeRequest")).toBe("ACME/A"); // the SOURCE
    expect(deliveries.some((d) => d.target === "mesh/main")).toBe(false);
    mesh.close();
  });
});
