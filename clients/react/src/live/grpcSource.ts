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
import { mergePatch, setPointer } from "../area/pointer.js";

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
  private state: AreaTree = {};
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
    const raw = (message.change ?? message.Change) as Json;
    const wrapper = typeof raw === "string" ? JSON.parse(raw) : raw;
    if (wrapper == null) return;
    // Verified against Memex.LocalMesh: DataChangedEvent.Change wraps the EntityStore JSON as
    // { Content: "<entitystore json>" }. The EntityStore IS { areas, data } — exactly the tree the
    // renderer consumes (its $type is ignored). ChangeType 0 = Full (replace); anything else = RFC 7396 patch.
    const w = wrapper as Record<string, unknown>;
    const content = w.Content ?? w.content;
    const store = (typeof content === "string" ? JSON.parse(content) : content ?? wrapper) as Json;
    if (store == null) return;
    const changeType = String(message.changeType ?? message.ChangeType ?? "1");
    this.state = changeType === "Full" || changeType === "0" ? (store as AreaTree) : mergePatch(this.state, store);
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
