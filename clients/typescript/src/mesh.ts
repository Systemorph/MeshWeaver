// Ergonomic mesh operations — the in-language port of MeshWeaver.AI.MeshOperations (mirrors the
// Python SDK's mesh.py). Each method is a thin composition of the MeshConnection primitives. The
// request type names / target addresses are the mesh's own contracts; annotated `WIRE:` where they
// must be confirmed against the running mesh. The transport underneath is already correct.

import { connect as connectTransport, MeshConnection, type ConnectOptions } from "./connection.js";
import { randomUUID } from "node:crypto";
import { meshNodeFromChange, type MeshNode } from "./types.js";

export interface MeshOptions extends ConnectOptions {
  meshAddress?: string; // WIRE: confirm the portal's mesh-service address
}

/**
 * The ROUTABLE owner address for a node namespace — the address CreateNodeRequest targets. A
 * satellite namespace ("roland/_Activity") strips its trailing `_Xxx` segment down to the owning
 * node's path. Mirrors @meshweaver/client-web's ownerOfNamespace: 'mesh/main' is NOT routable on
 * the distributed portal (its root hub is `mesh/{guid}`), so creates target the owner's hub.
 */
export function ownerOfNamespace(namespace: string): string {
  const segments = namespace.split("/").filter((s) => s.length > 0);
  while (segments.length > 0 && segments[segments.length - 1].startsWith("_")) segments.pop();
  return segments.join("/");
}

export class Mesh {
  private readonly conn: MeshConnection;
  private readonly meshAddress: string;

  private constructor(conn: MeshConnection, meshAddress: string) {
    this.conn = conn;
    this.meshAddress = meshAddress;
  }

  static async connect(url: string, opts: MeshOptions = {}): Promise<Mesh> {
    const conn = await connectTransport(url, opts);
    return new Mesh(conn, opts.meshAddress ?? "mesh/main");
  }

  close(): void {
    this.conn.close();
  }

  /** Free-text / structured mesh query (routes to vector or SQL server-side). */
  async search(query: string, basePath?: string, limit = 50): Promise<Record<string, unknown>[]> {
    const resp = await this.conn.observe(this.meshAddress, "QueryRequest", { query, basePath, limit }); // WIRE: confirm query request type
    return ((resp.message["results"] ?? resp.message["Results"]) as Record<string, unknown>[]) ?? [];
  }

  /** Read a single node's current state (one snapshot off its live stream). */
  async get(path: string): Promise<MeshNode> {
    for await (const node of this.watch(path)) return node;
    throw new Error("stream closed before first state");
  }

  /** Subscribe to a node's live state — yields on every change (Full, then merge-patches). */
  async *watch(path: string): AsyncIterableIterator<MeshNode> {
    const streamId = randomUUID().replace(/-/g, "");
    // WIRE: confirm the reference shape for a node path.
    for await (const delivery of this.conn.watch(path, streamId, "SubscribeRequest", { reference: { path } })) {
      yield meshNodeFromChange(delivery.message);
    }
  }

  /** Field-level partial update (content deep-merges, RFC 7396) — the canonical mutation. */
  patch(path: string, fields: Record<string, unknown>): void {
    this.conn.post(path, "PatchDataRequest", { path, change: fields }); // WIRE: confirm partial-update request type
  }

  /**
   * Create a node — targets the node's OWNER partition address (a routable per-node hub), never
   * 'mesh/main' (unroutable on the distributed portal). A refused create THROWS instead of
   * resolving silently.
   */
  async create(node: Record<string, unknown>): Promise<Record<string, unknown>> {
    const target = ownerOfNamespace(String(node["namespace"] ?? "")) || this.meshAddress;
    const resp = await this.conn.observe(target, "CreateNodeRequest", { node });
    const m = resp.message;
    const success = (m["success"] ?? m["Success"]) as boolean | undefined;
    if (success === false || m["$type"] === "DeliveryFailure")
      throw new Error(String(m["message"] ?? m["Message"] ?? `create failed for ${node["path"] ?? node["id"]}`));
    return m;
  }

  // Node-lifecycle handlers (Delete/Move/Copy) live on EVERY per-node hub, so target the node's
  // OWN path — routable (the node exists), never the unroutable 'mesh/main'. Mirrors
  // MeshOperations, where Move posts WithTarget(new Address(resolvedSource)).

  /** Delete a node by path — routed to the node's own hub. */
  async delete(path: string): Promise<void> {
    await this.conn.observe(path, "DeleteNodeRequest", { path });
  }

  /** Move a node (and its satellites) — routed to the SOURCE node's hub. */
  async move(source: string, target: string): Promise<void> {
    await this.conn.observe(source, "MoveNodeRequest", { source, target });
  }

  /** Copy a node to a new path — routed to the SOURCE node's hub. */
  async copy(source: string, target: string): Promise<void> {
    await this.conn.observe(source, "CopyNodeRequest", { source, target });
  }

  /**
   * Run an executable Code/activity node by flipping its control-plane trigger to Running (the
   * operations-as-scripts pattern — the owning hub's watcher reacts). Subscribe with watch(path) to
   * follow Status to a terminal state.
   */
  execute(path: string): void {
    this.patch(path, { requestedStatus: "Running" }); // WIRE: confirm the activity control-plane field
  }
}
