// Live AreaSource — subscribes to a MeshWeaver layout area over the gRPC transport and folds the
// stream into the {areas,data} tree the renderer consumes. Decoupled from @meshweaver/client via the
// small MeshConnectionLike interface (the consumer passes a real MeshConnection), so the renderer
// core has no transport dependency.
//
// 🔬 WIRE: the message-type names and field shapes below (SubscribeRequest / DataChangedEvent /
// ClickedEvent / BlurEvent / the pointer-update request) are the layout-area protocol — pin them
// against a running portal (capture one DataChangedEvent + one click/edit round-trip). Everything
// else (folding, optimistic edits, demux) is correct as written.

import type { AreaSource, AreaTree, Json, MeshEvent } from "../area/types.js";
import { applyJsonPatch, setPointer, type JsonPatchOp } from "../area/pointer.js";

/** The subset of @meshweaver/client's MeshConnection this source needs. */
export interface MeshConnectionLike {
  watch(
    target: string,
    streamId: string,
    subscribeType: string,
    subscribeMsg: Record<string, unknown>,
  ): AsyncIterable<{ message: Record<string, unknown> }>;
  post(target: string, messageType: string, message: Record<string, unknown>): void;
}

/** A MeshWeaver LayoutAreaReference: which area (and instance) to subscribe to on the target hub. */
export interface LayoutAreaReference {
  area: string;
  id?: string;
  layout?: string;
}

export interface GrpcAreaOptions {
  /** Override the generated stream id (otherwise a fresh one is used). */
  streamId?: string;
}

export class GrpcAreaSource implements AreaSource {
  // `raw` is the EntityStore exactly as it arrives on the wire: RFC 6902 patches address it by its
  // wire paths (which quote the collection keys), so patches must fold into THIS shape. `state` is the
  // normalized view the renderer consumes — see normalizeStore.
  private raw: Json = {};
  private state: AreaTree = { areas: {}, data: {} };
  private readonly listeners = new Set<() => void>();
  private readonly streamId: string;

  constructor(
    private readonly connection: MeshConnectionLike,
    private readonly address: string,
    private readonly reference: LayoutAreaReference,
    options: GrpcAreaOptions = {},
  ) {
    this.streamId = options.streamId ?? newId();
  }

  getState = (): AreaTree => this.state;

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  /** Open the area subscription and fold every change into the tree until the stream ends. */
  async start(): Promise<void> {
    for await (const delivery of this.connection.watch(this.address, this.streamId, "SubscribeRequest", {
      // The sync-stream Reference is a polymorphic WorkspaceReference — it MUST carry its $type
      // discriminator or the mesh can't deserialize it. Verified against Memex.LocalMesh: a $type-less
      // reference elicits no subscription; with "LayoutAreaReference" the area streams back. Field casing
      // is server-insensitive, so the lowercase { area, id, layout } passes through.
      reference: { $type: "LayoutAreaReference", ...this.reference },
    })) {
      this.applyChange(delivery.message);
    }
  }

  private applyChange(message: Record<string, unknown>): void {
    const rawChange = (message.change ?? message.Change) as Json;
    const wrapper = typeof rawChange === "string" ? JSON.parse(rawChange) : rawChange;
    if (wrapper == null) return;
    // DataChangedEvent.Change wraps its payload as RawJson { Content: "<json>" }. For a Full change
    // (ChangeType 0) the payload is the whole EntityStore; for a Patch it is a JsonPatch (RFC 6902,
    // an array of op/path/value). Both fold into `raw` — the un-normalized wire shape the patch paths
    // (which carry JSON-quoted collection keys) address.
    const w = wrapper as Record<string, unknown>;
    const content = w.Content ?? w.content ?? wrapper;
    const payload = (typeof content === "string" ? JSON.parse(content) : content) as Json;
    if (payload == null) return;
    const changeType = String(message.changeType ?? message.ChangeType ?? "1");
    this.raw =
      changeType === "Full" || changeType === "0"
        ? payload
        : applyJsonPatch(this.raw, payload as JsonPatchOp[]);
    this.state = normalizeStore(this.raw);
    this.notify();
  }

  emit = (event: MeshEvent): void => {
    switch (event.kind) {
      case "click":
        this.connection.post(this.address, "ClickedEvent", { area: event.area, streamId: this.streamId });
        break;
      case "blur":
        this.connection.post(this.address, "BlurEvent", { area: event.area, streamId: this.streamId });
        break;
      case "update":
        if (!event.pointer) break;
        // Optimistic local apply so the field reflects immediately; the server echoes the merge-patch.
        this.state = setPointer(this.state, event.pointer, event.value);
        this.notify();
        this.connection.post(this.address, "PatchDataChangeRequest", {
          streamId: this.streamId,
          changeType: "Patch",
          // Sparse merge-patch: only the edited pointer (e.g. /data/name -> { data: { name: value } }).
          change: JSON.stringify(setPointer({}, event.pointer, event.value)),
        });
        break;
    }
  };

  private notify(): void {
    this.listeners.forEach((l) => l());
  }
}

function newId(): string {
  const g = globalThis as { crypto?: { randomUUID?: () => string } };
  return g.crypto?.randomUUID?.().replace(/-/g, "") ?? Math.random().toString(36).slice(2) + Date.now().toString(36);
}

// The wire EntityStore is { $type, areas: <InstanceCollection>, data: <InstanceCollection> }, where each
// InstanceCollection is a JSON object whose keys are JSON-ENCODED entity keys — a string area name
// "Overview" is serialized as the property name "\"Overview\"" (MeshWeaver's InstanceCollectionConverter
// does JsonSerializer.Serialize(key)) — plus a leading "$type" collection discriminator. The renderer keys
// off plain names (areas["Overview"], NamedArea.area, /data/<id> pointers), so unwrap each collection:
// drop $type and JSON-parse each key back to its plain string.
function normalizeStore(raw: Json): AreaTree {
  return { areas: unwrapCollection(raw?.areas), data: unwrapCollection(raw?.data) };
}

function unwrapCollection(collection: Json): Record<string, Json> {
  const out: Record<string, Json> = {};
  if (collection && typeof collection === "object") {
    for (const [key, value] of Object.entries(collection)) {
      if (key === "$type") continue; // collection-type discriminator, not an entry
      out[unquoteKey(key)] = value;
    }
  }
  return out;
}

// A JSON-encoded string key ("\"Overview\"") parses back to "Overview". Anything that isn't a quoted
// string (already-plain, or a numeric/tuple key) passes through unchanged.
function unquoteKey(key: string): string {
  if (key.length >= 2 && key.charCodeAt(0) === 0x22 /* " */) {
    try {
      const parsed = JSON.parse(key);
      if (typeof parsed === "string") return parsed;
    } catch {
      /* not a JSON string — fall through */
    }
  }
  return key;
}
