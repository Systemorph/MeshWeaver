// Live AreaSource — subscribes to a MeshWeaver layout area over the gRPC transport and folds the
// stream into the {areas,data} tree the renderer consumes. Decoupled from @meshweaver/client via the
// small MeshConnectionLike interface (the consumer passes a real MeshConnection), so the renderer
// core has no transport dependency.
//
// WIRE (pinned against the server source, src/MeshWeaver.Data*/):
//   - SubscribeRequest(StreamId, WorkspaceReference Reference) — Reference is POLYMORPHIC (abstract
//     WorkspaceReference), so the subscribe JSON must carry the `$type` discriminator
//     ("LayoutAreaReference", the TypeRegistry short name); without it the server cannot
//     instantiate the abstract base and the subscribe is dropped.
//   - An EMPTY reference.area subscribes the DEFAULT area: the server resolves it and the very
//     first Full frame carries areas[""] = NamedAreaControl(resolvedArea) (LayoutAreaHost), so
//     rendering root key "" follows the indirection.
//   - DataChangedEvent.Change: ChangeType "Full" is a whole EntityStore snapshot; "Patch" is an
//     RFC 6902 JSON-PATCH ARRAY (JsonSynchronizationStream.ToJsonPatch) — NOT an RFC 7396 merge.
//   - EntityStore collections serialize as InstanceCollections: instance keys are JSON-ENCODED
//     property names (`"Content"` arrives as the property `"\"Content\""`) plus a leading `$type`
//     collection marker (InstanceCollectionConverter). Area lookups use PLAIN names
//     (NamedArea.area → areas["Content"]), so keys are decoded at fold time; binding pointers keep
//     their wire encoding and decode at resolution time (pointer.ts decodePointerSegment).
//   - Client edits ride PatchDataChangeRequest(StreamId, …, RawJson Change, ChangeType) where the
//     change is again an RFC 6902 patch array applied by the owner
//     (JsonSynchronizationStream.ApplyPatchWithCorrectUnescaping); RawJson is raw inline JSON on
//     the wire — never a stringified string.

import type { AreaSource, AreaTree, Json, MeshEvent } from "../area/types.js";
import { decodePointerSegment, mergePatch, setPointer } from "../area/pointer.js";
import { normalizeCollection, normalizeEntityStore } from "./wire.js";

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

/** A MeshWeaver LayoutAreaReference: which area (and instance) to subscribe to on the target hub.
 *  An empty/absent `area` subscribes the target's DEFAULT area (resolved server-side). */
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
      // The polymorphic $type is REQUIRED — Reference deserializes as abstract WorkspaceReference.
      reference: { $type: "LayoutAreaReference", ...this.reference },
    })) {
      this.applyChange(delivery.message);
    }
  }

  private applyChange(message: Record<string, unknown>): void {
    const raw = (message.change ?? message.Change) as Json;
    const change = typeof raw === "string" ? JSON.parse(raw) : raw;
    const changeType = String(message.changeType ?? message.ChangeType ?? "Full");
    if (change == null || changeType === "NoUpdate") return;
    this.state =
      changeType === "Patch" || changeType === "1"
        ? applyJsonPatch(this.state, change)
        : // Full (or the enum's 0) / Instance replace the snapshot.
          normalizeEntityStore(change);
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
      case "closeDialog":
        // Mirrors Blazor's DialogView.HandleClose: CloseDialogEvent(Area, StreamId, DialogCloseState).
        this.connection.post(this.address, "CloseDialogEvent", {
          area: event.area,
          streamId: this.streamId,
          state: event.value ?? "OK",
        });
        break;
      case "update":
        if (!event.pointer) break;
        // Optimistic local apply so the field reflects immediately (setPointer decodes the wire
        // pointer's JSON-encoded segments, matching the fold); the server echoes the real patch.
        this.state = setPointer(this.state, event.pointer, event.value);
        this.notify();
        this.connection.post(this.address, "PatchDataChangeRequest", {
          streamId: this.streamId,
          changeType: "Patch",
          // RFC 6902 — one replace op at the (wire-encoded) pointer, as raw JSON (RawJson inlines).
          change: [{ op: "replace", path: event.pointer, value: event.value }],
        });
        break;
    }
  };

  private notify(): void {
    this.listeners.forEach((l) => l());
  }
}

// ---- wire folding ---------------------------------------------------------------------------

interface PatchOp {
  op: string;
  path: string;
  value?: Json;
  from?: string;
}

// normalizeEntityStore / normalizeCollection / decodeWireKey live in wire.ts — shared with the
// SSR seeding path (portal-next fetches one Full frame over REST and folds it the same way).

/**
 * Apply an RFC 6902 patch (the wire's ChangeType.Patch shape) immutably. Pointer segments carry
 * the same JSON-encoded instance keys as snapshots — decodePointerSegment folds them onto the
 * normalized tree. A non-array delta is tolerated as an RFC 7396 merge (in-memory test sources).
 */
function applyJsonPatch(state: AreaTree, ops: Json): AreaTree {
  if (!Array.isArray(ops)) return mergePatch(state, ops);
  let next: Json = state;
  for (const op of ops as PatchOp[]) {
    const parts = splitWirePointer(op.path);
    switch (op.op) {
      case "add":
      case "replace":
        next = setAtParts(next, parts, normalizeValueAtDepth(op.value, parts.length), op.op === "add");
        break;
      case "remove":
        next = removeAtParts(next, parts);
        break;
      case "move": {
        const from = splitWirePointer(op.from ?? "");
        const moved = getAtParts(next, from);
        next = removeAtParts(next, from);
        next = setAtParts(next, parts, moved, true);
        break;
      }
      case "copy":
        next = setAtParts(next, parts, getAtParts(next, splitWirePointer(op.from ?? "")), true);
        break;
      default:
        break; // "test" (and unknown ops) are no-ops for folding
    }
  }
  return next as AreaTree;
}

/** A value landing at the root is a whole store; at depth 1 a whole collection — normalize keys. */
function normalizeValueAtDepth(value: Json, depth: number): Json {
  if (depth === 0) return normalizeEntityStore(value);
  if (depth === 1) return normalizeCollection(value);
  return value;
}

function splitWirePointer(pointer: string): string[] {
  if (!pointer || pointer === "/") return pointer === "/" ? [""] : [];
  return pointer.split("/").slice(1).map(decodePointerSegment);
}

function getAtParts(root: Json, parts: string[]): Json {
  let cur: Json = root;
  for (const part of parts) {
    if (cur == null) return undefined;
    cur = Array.isArray(cur) ? cur[Number(part)] : cur[part];
  }
  return cur;
}

/** Immutable set — RFC 6902 semantics: on arrays, `add` INSERTS at the index ("-" appends). */
function setAtParts(root: Json, parts: string[], value: Json, insert: boolean): Json {
  if (parts.length === 0) return value;
  const [head, ...rest] = parts;
  if (Array.isArray(root)) {
    const clone = [...root];
    const idx = head === "-" ? clone.length : Number(head);
    if (rest.length === 0) {
      if (insert) clone.splice(idx, 0, value);
      else clone[idx] = value;
    } else {
      clone[idx] = setAtParts(clone[idx], rest, value, insert);
    }
    return clone;
  }
  const clone: Record<string, Json> = { ...((root ?? {}) as Record<string, Json>) };
  clone[head] = rest.length === 0 ? value : setAtParts(clone[head], rest, value, insert);
  return clone;
}

/** Immutable remove — splices arrays, deletes object properties; missing paths are tolerated. */
function removeAtParts(root: Json, parts: string[]): Json {
  if (parts.length === 0) return {};
  const [head, ...rest] = parts;
  if (root == null || typeof root !== "object") return root;
  if (Array.isArray(root)) {
    const clone = [...root];
    const idx = Number(head);
    if (rest.length === 0) clone.splice(idx, 1);
    else clone[idx] = removeAtParts(clone[idx], rest);
    return clone;
  }
  const clone: Record<string, Json> = { ...(root as Record<string, Json>) };
  if (rest.length === 0) delete clone[head];
  else clone[head] = removeAtParts(clone[head], rest);
  return clone;
}

function newId(): string {
  const g = globalThis as { crypto?: { randomUUID?: () => string } };
  return g.crypto?.randomUUID?.().replace(/-/g, "") ?? Math.random().toString(36).slice(2) + Date.now().toString(36);
}
