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

// The wire delivers frames DUPLICATED (RegisterStream + proxy-hub forward both fire) with no
// cross-frame ordering guarantee. Folding by the monotonic per-stream `version` is what makes the
// rendered tree DETERMINISTIC — the "same page renders differently on every refresh" bug was a fold
// that replayed duplicates and applied patches against the wrong base.
describe("GrpcAreaSource version-ordered fold (deterministic refresh)", () => {
  const patch = (version: number, name: string) => ({
    streamId: "s1",
    version,
    changeType: "Patch",
    change: [{ op: "replace", path: '/data/"model"/name', value: name }],
  });
  const modelName = (source: GrpcAreaSource) =>
    (source.getState().data as Record<string, Record<string, unknown>>)["model"].name;

  async function fold(frames: Record<string, unknown>[]): Promise<GrpcAreaSource> {
    const { connection, drained } = fakeConnection(frames);
    const source = new GrpcAreaSource(connection, "Doc/GUI", { area: "" }, { streamId: "s1" });
    void source.start();
    await drained;
    return source;
  }

  it("a duplicate Full frame folds to the same tree (idempotent, no corruption)", async () => {
    const source = await fold([fullFrame, fullFrame]);
    expect(modelName(source)).toBe("Ada");
    expect(source.getState().areas!["Content"].data).toBe("# hello");
  });

  it("ignores a duplicate Patch (same version) — no double fold", async () => {
    // A second version-2 frame (here with a different payload to make the drop observable) is dropped.
    const source = await fold([fullFrame, patch(2, "Grace"), patch(2, "WRONG")]);
    expect(modelName(source)).toBe("Grace");
  });

  it("drops a Patch that arrives before its base snapshot", async () => {
    const source = await fold([patch(2, "Grace"), fullFrame]);
    expect(modelName(source)).toBe("Ada"); // the un-baseable patch was ignored; only the Full applied
  });

  it("never regresses to a stale Full (older version) after patching", async () => {
    const source = await fold([fullFrame, patch(2, "Grace"), { ...fullFrame, version: 1 }]);
    expect(modelName(source)).toBe("Grace"); // the re-sent v1 Full did not clobber the v2 state
  });

  it("applies newer patches even when versions are NON-contiguous (no exact-succession requirement)", async () => {
    // A subscriber's versions can skip (server-side coalescing); demanding current+1 would drop these
    // and strand content on its loading frame — the "threads never load" regression. Newer = applied.
    const source = await fold([fullFrame, patch(2, "Grace"), patch(5, "Hopper")]);
    expect(modelName(source)).toBe("Hopper");
  });

  it("applies a versionless patch best-effort (never drops it for lack of a version)", async () => {
    const versionlessPatch = { streamId: "s1", changeType: "Patch", change: [{ op: "replace", path: '/data/"model"/name', value: "Lovelace" }] };
    const source = await fold([fullFrame, versionlessPatch]);
    expect(modelName(source)).toBe("Lovelace");
  });
});
