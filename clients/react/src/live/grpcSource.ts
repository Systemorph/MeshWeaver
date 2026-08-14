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
import type { EmbeddedAreaHandle, EmbeddedAreaReference } from "../render/embeddedArea.js";
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
  // The last stream version folded into `state`. Every wire frame (DataChangedEvent) carries a
  // MONOTONIC per-stream version; the transport can deliver frames DUPLICATED (RegisterStream +
  // proxy-hub forward both fire) and gives no cross-frame ordering guarantee. Folding by version is
  // what makes the tree deterministic: replay a duplicate or apply a patch against the wrong base and
  // the render depends on arrival timing — the "different page every refresh" bug. -1 = nothing folded.
  private version = -1;
  private hasSnapshot = false;
  private started = false;
  private disposed = false;
  /** Set when the subscription stream faults or ends before any snapshot — lets the UI stop "loading". */
  error: string | null = null;

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

  /**
   * Open the area subscription and fold every change into the tree until the stream ends. Idempotent
   * (a second call is a no-op) and self-contained: a stream fault or a clean end is caught, recorded on
   * `error`, and surfaced to listeners — a subscription must never die silently and strand an embed on
   * its loading frame (the "@@ stuck loading" symptom).
   */
  async start(): Promise<void> {
    if (this.started || this.disposed) return;
    this.started = true;
    try {
      for await (const delivery of this.connection.watch(this.address, this.streamId, "SubscribeRequest", {
        // The polymorphic $type is REQUIRED — Reference deserializes as abstract WorkspaceReference.
        reference: { $type: "LayoutAreaReference", ...this.reference },
      })) {
        if (this.disposed) return;
        this.applyChange(delivery.message);
      }
      // A clean end before any snapshot means the stream produced nothing — surface it rather than
      // leaving consumers on the loading frame forever.
      if (!this.disposed && this.version < 0) {
        this.error = "the area stream closed before delivering a snapshot";
        this.notify();
      }
    } catch (err) {
      if (this.disposed) return;
      this.error = err instanceof Error ? err.message : String(err);
      this.notify();
    }
  }

  /** Stop folding further changes (the underlying stream is torn down by the connection's close). */
  dispose(): void {
    this.disposed = true;
    this.listeners.clear();
  }

  private applyChange(message: Record<string, unknown>): void {
    const changeType = String(message.changeType ?? message.ChangeType ?? "Full");
    if (changeType === "NoUpdate") return;
    const version = toVersion(message.version ?? message.Version);
    const isPatch = changeType === "Patch" || changeType === "1";

    // Dedup by the monotonic version: a frame at a version we've ALREADY folded (version <= held) is a
    // duplicate re-send or a stale/reordered frame — drop it. This is the whole ordering guard, and it
    // is what kills the non-determinism (the transport delivers duplicates with no ordering promise, so
    // folding one twice made the render depend on arrival timing). It does NOT require versions to be
    // contiguous — a subscriber's versions can skip (server-side coalescing), and demanding exact
    // succession would drop legitimate patches and leave content stuck loading. A versionless frame
    // (no version on the wire) can't be ordered, so it's applied best-effort — never dropped.
    if (version != null && version <= this.version) return;

    if (isPatch) {
      // A patch only means something ON TOP of a snapshot. Before the first Full there is no base to
      // patch, so drop it (applying it to an empty tree corrupts state); the Full that follows carries
      // the same content anyway.
      if (!this.hasSnapshot) return;
      const change = parseChange(message);
      if (change == null) return;
      this.state = applyJsonPatch(this.state, change);
    } else {
      // Full / Instance snapshot: replace the tree and mark that patches now have a base.
      const change = parseChange(message);
      if (change == null) return;
      this.state = normalizeEntityStore(change);
      this.hasSnapshot = true;
    }
    if (version != null) this.version = version;
    this.error = null;
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

/**
 * A STABLE, caching {@link AreaSourceFactory} for nested `@@`-macro / LayoutAreaControl embeds over
 * one connection. Hosts MUST memoize this per connection (`useMemo(..., [connection])`) and pass its
 * result as the EmbeddedAreaProvider `factory` — an INLINE arrow recreated each render churns
 * AddressAreaEmbed's effect (deps include `factory`), tearing down and re-subscribing every embed on
 * every re-render. That race is why doc-page `@@` regions rendered non-deterministically ("different
 * sections each refresh"). Caching by (address, area, id) also dedups: the same embedded area
 * rendered twice shares ONE subscription, and a re-mount of an already-seen embed returns the live
 * source instantly (no blank re-subscribe). Sources live for the factory's lifetime (= the
 * connection's, since it's memoized on it); the connection's close tears them all down.
 */
// Returns a NON-nullable-handle factory (this source is always available for a live connection);
// still assignable to AreaSourceFactory (whose return is EmbeddedAreaHandle | null) by covariance.
export function createGrpcEmbeddedFactory(
  connection: MeshConnectionLike,
): (address: string, ref: EmbeddedAreaReference) => EmbeddedAreaHandle {
  return new MeshAreaRegistry(connection).embeddedFactory();
}

/**
 * The ONE client-side area hub for a session: a single subscription registry over one connection.
 * Every layout area — the routed page AND every nested `@@` / LayoutAreaControl embed — resolves its
 * live source through here, so a given (address, area, id) has EXACTLY ONE {@link GrpcAreaSource}
 * (one gRPC-web watch stream) shared by all consumers. This is the client twin of a server node hub:
 * created once at session boot, it owns the connection's streams and their deterministic
 * version-ordered fold; {@link MeshAreaRegistry.close} tears every subscription down.
 *
 * Creating a fresh source per render / per embed instead — two separate caches, an inline factory
 * churned each render — is what made the same page render differently on every refresh. A host that
 * wants ONE hub holds ONE registry per connection and routes BOTH the page area and the embed factory
 * through it (see portal-next's session).
 */
export class MeshAreaRegistry {
  private readonly sources = new Map<string, GrpcAreaSource>();

  constructor(private readonly connection: MeshConnectionLike) {}

  private static key(address: string, reference: EmbeddedAreaReference): string {
    const area = reference.area ?? "";
    const id = reference.id == null ? "" : String(reference.id);
    return `${address}\u001f${area}\u001f${id}`;
  }

  /** The shared source for (address, area, id) — created and started ONCE, reused thereafter. */
  get(address: string, reference: EmbeddedAreaReference = {}): GrpcAreaSource {
    const key = MeshAreaRegistry.key(address, reference);
    let source = this.sources.get(key);
    if (!source) {
      source = new GrpcAreaSource(this.connection, address, {
        area: reference.area ?? "",
        id: reference.id == null ? undefined : String(reference.id),
        layout: reference.layout,
      });
      this.sources.set(key, source);
      void source.start();
    }
    return source;
  }

  /** An {@link AreaSourceFactory} view for nested `@@` / LayoutAreaControl embeds (EmbeddedAreaProvider). */
  embeddedFactory(): (address: string, ref: EmbeddedAreaReference) => EmbeddedAreaHandle {
    return (address, ref) => ({ source: this.get(address, ref), rootArea: ref.area ?? "" });
  }

  /** Tear down every subscription (session close / connection loss). */
  close(): void {
    this.sources.forEach((s) => s.dispose());
    this.sources.clear();
  }
}

/** Coerce a wire version (number or numeric string) to a number, or null when absent/non-numeric. */
function toVersion(v: unknown): number | null {
  if (typeof v === "number") return Number.isFinite(v) ? v : null;
  if (typeof v === "string" && v.trim() !== "") {
    const n = Number(v);
    return Number.isFinite(n) ? n : null;
  }
  return null;
}

/** The frame's change payload (camel/Pascal, JSON string or inlined RawJson) — null when absent. */
function parseChange(message: Record<string, unknown>): Json {
  const raw = (message.change ?? message.Change) as Json;
  return typeof raw === "string" ? (JSON.parse(raw) as Json) : raw;
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
