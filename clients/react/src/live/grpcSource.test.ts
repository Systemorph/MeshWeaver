import { describe, expect, it } from "vitest";
import { GrpcAreaSource, type MeshConnectionLike } from "./grpcSource.js";
import { getPointer } from "../area/pointer.js";

// Wire-faithful fold tests — the frame shapes below are pinned against the SERVER source
// (src/MeshWeaver.Data/Serialization): InstanceCollectionConverter JSON-encodes instance keys and
// prepends a `$type` collection marker; JsonSynchronizationStream.ToJsonPatch emits RFC 6902 patch
// ARRAYS whose pointer segments carry the same JSON-encoded keys. See grpcSource.ts "WIRE".

interface Sent {
  target: string;
  messageType: string;
  message: Record<string, unknown>;
}

/** In-memory MeshConnectionLike: scripts the watch frames, records every post. */
function fakeConnection(frames: Record<string, unknown>[]) {
  const sent: Sent[] = [];
  let release: (() => void) | undefined;
  const drained = new Promise<void>((r) => (release = r));
  const connection: MeshConnectionLike = {
    post: (target, messageType, message) => {
      sent.push({ target, messageType, message });
    },
    // Mirrors MeshWebConnection.watch: it POSTS the subscribe message (streamId folded in),
    // then yields the scripted mesh→client frames.
    watch: async function* (target, streamId, subscribeType, subscribeMsg) {
      sent.push({ target, messageType: subscribeType, message: { ...subscribeMsg, streamId } });
      for (const message of frames) yield { message };
      release!();
      await new Promise<void>(() => {}); // stay open like the real stream
    },
  };
  return { connection, sent, drained };
}

const fullFrame = {
  streamId: "s1",
  version: 1,
  changeType: "Full",
  change: {
    areas: {
      $type: "areas",
      '""': { $type: "NamedAreaControl", area: "Content" },
      '"Content"': { $type: "MarkdownControl", data: "# hello" },
    },
    data: {
      $type: "data",
      '"model"': { name: "Ada" },
    },
  },
};

describe("GrpcAreaSource wire folding", () => {
  it("subscribes with the polymorphic $type on the LayoutAreaReference", async () => {
    const { connection, sent, drained } = fakeConnection([]);
    const source = new GrpcAreaSource(connection, "Doc/GUI", { area: "" }, { streamId: "s1" });
    void source.start();
    await drained;
    expect(sent[0].messageType).toBe("SubscribeRequest");
    expect(sent[0].message.reference).toEqual({ $type: "LayoutAreaReference", area: "" });
    expect(sent[0].message.streamId).toBe("s1");
  });

  it("folds a Full snapshot: instance keys decoded, $type markers dropped", async () => {
    const { connection, drained } = fakeConnection([fullFrame]);
    const source = new GrpcAreaSource(connection, "Doc/GUI", { area: "" }, { streamId: "s1" });
    void source.start();
    await drained;
    const state = source.getState();
    expect(Object.keys(state.areas!).sort()).toEqual(["", "Content"]);
    expect(state.areas![""].area).toBe("Content"); // default-area indirection renders by plain key
    expect(state.areas!["Content"].$type).toBe("MarkdownControl");
    expect((state.data as Record<string, unknown>)["model"]).toEqual({ name: "Ada" });
  });

  it("applies an RFC 6902 Patch with JSON-encoded key segments", async () => {
    const patchFrame = {
      streamId: "s1",
      version: 2,
      changeType: "Patch",
      change: [
        { op: "replace", path: '/areas/"Content"/data', value: "# updated" },
        { op: "add", path: '/data/"model"/role', value: "Engineer" },
        { op: "remove", path: '/data/"model"/name' },
      ],
    };
    const { connection, drained } = fakeConnection([fullFrame, patchFrame]);
    const source = new GrpcAreaSource(connection, "Doc/GUI", { area: "" }, { streamId: "s1" });
    void source.start();
    await drained;
    const state = source.getState();
    expect(state.areas!["Content"].data).toBe("# updated");
    expect((state.data as Record<string, Record<string, unknown>>)["model"]).toEqual({ role: "Engineer" });
  });

  it("resolves a wire-encoded binding pointer against the folded tree", async () => {
    const { connection, drained } = fakeConnection([fullFrame]);
    const source = new GrpcAreaSource(connection, "Doc/GUI", { area: "" }, { streamId: "s1" });
    void source.start();
    await drained;
    // Controls arrive with wire-encoded pointers (/data/"model"/name) — resolution must decode.
    expect(getPointer(source.getState(), '/data/"model"/name')).toBe("Ada");
  });

  it("sends edits as an RFC 6902 PatchDataChangeRequest (raw array, wire pointer)", async () => {
    const { connection, sent, drained } = fakeConnection([fullFrame]);
    const source = new GrpcAreaSource(connection, "Doc/GUI", { area: "" }, { streamId: "s1" });
    void source.start();
    await drained;
    source.emit({ kind: "update", area: "Content", pointer: '/data/"model"/name', value: "Grace" });
    const patch = sent.find((s) => s.messageType === "PatchDataChangeRequest")!;
    expect(patch.message.change).toEqual([{ op: "replace", path: '/data/"model"/name', value: "Grace" }]);
    // ... and the optimistic local apply landed on the DECODED key.
    expect((source.getState().data as Record<string, Record<string, unknown>>)["model"].name).toBe("Grace");
  });
});
