import { describe, expect, it } from "vitest";
import { Mesh } from "./mesh";
import { buildThreadNode, generateSpeakingId, isOwnerlessThreadPath } from "./threads";
import { fakeMeshTransport } from "./testTransport";
import type { Delivery } from "./envelope";

// patch/post are fire-and-forget (the Deliver promise isn't surfaced) — wait on the actual
// condition (the delivery arriving at the fake router), not a fixed sleep.
async function until<T>(get: () => T | undefined, tries = 100): Promise<T> {
  for (let i = 0; i < tries; i++) {
    const v = get();
    if (v !== undefined) return v;
    await new Promise((r) => setTimeout(r, 1));
  }
  throw new Error("condition not met within the deadline");
}

// The chat-thread submission surface — the client twin of MeshWeaver.AI.HubThreadExtensions.
// startThread must send ONE CreateNodeRequest with the seeded thread node (pendingUserMessages +
// composer), targeted at the namespace hub; submitMessage must read the thread and send a JSON-merge
// PatchDataRequest appending to userMessageIds + pendingUserMessages.
describe("threads (pure helpers)", () => {
  it("generateSpeakingId slugs the text and appends a unique suffix", () => {
    const id = generateSpeakingId("Hello, can you help me with this?");
    expect(id).toMatch(/^hello-can-you-help-me-with-this-[a-z0-9]{4}$/);
  });

  it("buildThreadNode anchors under {namespace}/_Thread and seeds the first message", () => {
    const { node, path, userMessageId } = buildThreadNode("rbuergi/Notes", "Summarize my inbox", {
      agentName: "Coder",
      createdBy: "rbuergi",
    });
    expect(path).toMatch(/^rbuergi\/Notes\/_Thread\/summarize-my-inbox-[a-z0-9]{4}$/);
    expect(node.nodeType).toBe("Thread");
    expect(node.mainNode).toBe("rbuergi/Notes");
    const content = node.content as Record<string, unknown>;
    expect(content.userMessageIds).toEqual([userMessageId]);
    expect(content.messages).toEqual([userMessageId]);
    const pending = content.pendingUserMessages as Record<string, Record<string, unknown>>;
    expect(pending[userMessageId].role).toBe("user");
    expect(pending[userMessageId].text).toBe("Summarize my inbox");
    expect(pending[userMessageId].status).toBe("Submitted");
    // The composer is the single source of the round's selection.
    expect((content.composer as Record<string, unknown>).agentName).toBe("Coder");
  });

  it("buildThreadNode does not nest _Thread for delegation sub-thread namespaces", () => {
    const { path } = buildThreadNode("acme/_Thread/parent-1234/resp-1", "sub task");
    expect(path).toMatch(/^acme\/_Thread\/parent-1234\/resp-1\/sub-task-[a-z0-9]{4}$/);
  });

  it("buildThreadNode fails fast on an ownerless namespace (no NotFound storm)", () => {
    expect(() => buildThreadNode("", "hi")).toThrow(/namespacePath/);
  });

  it("buildThreadNode with whitespace-only text creates the thread but queues NO round", () => {
    const { node } = buildThreadNode("rbuergi", "   ");
    const content = node.content as Record<string, unknown>;
    expect(content.pendingUserMessages).toBeUndefined();
    expect(content.userMessageIds).toBeUndefined();
  });

  it("isOwnerlessThreadPath flags bare _Thread paths and accepts owned ones", () => {
    expect(isOwnerlessThreadPath("_Thread/abcd-1234")).toBe(true);
    expect(isOwnerlessThreadPath("")).toBe(true);
    expect(isOwnerlessThreadPath("rbuergi/_Thread/abcd-1234")).toBe(false);
  });
});

describe("Mesh thread ops (gRPC-web, in-memory)", () => {
  it("startThread sends CreateNodeRequest to the namespace hub and resolves the thread path", async () => {
    const sent: Delivery[] = [];
    const mesh = await Mesh.connect("memory://", { transport: fakeMeshTransport({ onDeliver: (d) => sent.push(d) }) });
    const { path, userMessageId } = await mesh.startThread("rbuergi", "Hello from RN", { agentName: "Coder" });

    expect(path).toMatch(/^rbuergi\/_Thread\/hello-from-rn-[a-z0-9]{4}$/);
    const create = sent.find((d) => d.messageType === "CreateNodeRequest")!;
    expect(create.target).toBe("rbuergi"); // targeted at the namespace address, like the .NET StartThread
    const node = create.message.node as Record<string, unknown>;
    const content = node.content as Record<string, unknown>;
    expect((content.pendingUserMessages as Record<string, unknown>)[userMessageId]).toBeDefined();
    mesh.close();
  });

  it("submitMessage patches userMessageIds (append) + pendingUserMessages (merge) on the thread node", async () => {
    const sent: Delivery[] = [];
    const mesh = await Mesh.connect("memory://", {
      transport: fakeMeshTransport({
        onDeliver: (d) => sent.push(d),
        nodeContent: { userMessageIds: ["aaaa1111"], pendingUserMessages: {} },
      }),
    });
    const msgId = await mesh.submitMessage("rbuergi/_Thread/t-1", "and another thing", { modelName: "gpt" });

    expect(msgId).toMatch(/^[a-z0-9]{8}$/);
    const patch = await until(() => sent.find((d) => d.messageType === "PatchDataRequest"));
    expect(patch.target).toBe("rbuergi/_Thread/t-1");
    // PatchDataRequest(Reference, Patch): the merge document rides in `patch`, the node in `reference`.
    expect(patch.message.reference).toEqual({ $type: "MeshNodeReference", path: "rbuergi/_Thread/t-1" });
    const change = patch.message.patch as Record<string, unknown>;
    const content = change.content as Record<string, unknown>;
    expect(content.userMessageIds).toEqual(["aaaa1111", msgId]); // full array — merge-patch replaces arrays
    const pending = content.pendingUserMessages as Record<string, Record<string, unknown>>;
    expect(pending[msgId!].text).toBe("and another thing");
    expect((content.composer as Record<string, unknown>).modelName).toBe("gpt"); // selection folds into the composer
    mesh.close();
  });

  it("submitMessage is a no-op for whitespace-only text (never enqueue an empty round)", async () => {
    const sent: Delivery[] = [];
    const mesh = await Mesh.connect("memory://", { transport: fakeMeshTransport({ onDeliver: (d) => sent.push(d) }) });
    const msgId = await mesh.submitMessage("rbuergi/_Thread/t-1", "   ");
    expect(msgId).toBeNull();
    // Negative assertion — let any (wrongly) issued fire-and-forget delivery settle first.
    await new Promise((r) => setTimeout(r, 5));
    expect(sent.find((d) => d.messageType === "PatchDataRequest")).toBeUndefined();
    mesh.close();
  });

  it("submitMessage rejects an ownerless threadPath", async () => {
    const mesh = await Mesh.connect("memory://", { transport: fakeMeshTransport() });
    await expect(mesh.submitMessage("_Thread/t-1", "hi")).rejects.toThrow(/ownerless/);
    mesh.close();
  });
});
