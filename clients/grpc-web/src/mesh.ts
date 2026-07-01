// Ergonomic mesh operations for the browser/RN client — the gRPC-web twin of @meshweaver/client's
// mesh.ts and the Python SDK's mesh.py (the in-language port of MeshWeaver.AI.MeshOperations). Each
// method is a thin composition of the MeshWebConnection primitives (observe / post / watch). The request
// type names / target addresses are the mesh's own contracts; annotated `WIRE:` where they must be
// confirmed against the running mesh (capture a sample from the C# round-trip test). The transport
// underneath is already correct + tested.

import { connect as connectTransport, MeshWebConnection, type ConnectOptions } from "./connection";
import { meshNodeFromChange, type MeshNode } from "./types";
import { newId } from "./envelope";

export interface MeshOptions extends ConnectOptions {
  meshAddress?: string; // WIRE: confirm the portal's mesh-service address
}

export class Mesh {
  private readonly conn: MeshWebConnection;
  private readonly meshAddress: string;

  private constructor(conn: MeshWebConnection, meshAddress: string) {
    this.conn = conn;
    this.meshAddress = meshAddress;
  }

  static async connect(url: string, opts: MeshOptions = {}): Promise<Mesh> {
    const conn = await connectTransport(url, opts);
    return new Mesh(conn, opts.meshAddress ?? "mesh/main");
  }

  /** The underlying connection — pass to a GrpcAreaSource to render a live layout area. */
  get connection(): MeshWebConnection {
    return this.conn;
  }

  close(): void {
    this.conn.close();
  }

  // ---- reads ----------------------------------------------------------------

  /** Free-text / structured mesh query (routes to vector or SQL server-side). */
  async search(query: string, basePath?: string, limit = 50): Promise<Record<string, unknown>[]> {
    const resp = await this.conn.observe(this.meshAddress, "QueryRequest", { query, basePath, limit }); // WIRE: query request type
    return ((resp.message["results"] ?? resp.message["Results"]) as Record<string, unknown>[]) ?? [];
  }

  /** Read a single node's current state (one snapshot off its live stream). */
  async get(path: string): Promise<MeshNode> {
    for await (const node of this.watch(path)) return node;
    throw new Error("stream closed before first state");
  }

  /** Subscribe to a node's live state — yields on every change (Full, then merge-patches). */
  async *watch(path: string): AsyncIterableIterator<MeshNode> {
    const streamId = newId();
    // WIRE: confirm the reference shape for a node path.
    for await (const delivery of this.conn.watch(path, streamId, "SubscribeRequest", { reference: { path } })) {
      yield meshNodeFromChange(delivery.message);
    }
  }

  // ---- writes (carry the caller's identity, server-stamped) -----------------

  /** Field-level partial update (content deep-merges, RFC 7396) — the canonical mutation. */
  patch(path: string, fields: Record<string, unknown>): void {
    this.conn.post(path, "PatchDataRequest", { path, change: fields }); // WIRE: partial-update request type
  }

  /** Create a node (node-lifecycle on the mesh hub — routes, doesn't mutate). */
  async create(node: Record<string, unknown>): Promise<Record<string, unknown>> {
    const resp = await this.conn.observe(this.meshAddress, "CreateNodeRequest", { node }); // WIRE: create request shape
    return resp.message;
  }

  /** Delete a node by path. */
  async delete(path: string): Promise<void> {
    await this.conn.observe(this.meshAddress, "DeleteNodeRequest", { path }); // WIRE: delete request shape
  }

  /** Move a node (and its satellites) from one path to another. */
  async move(source: string, target: string): Promise<void> {
    await this.conn.observe(this.meshAddress, "MoveNodeRequest", { source, target }); // WIRE: MoveNodeRequest fields
  }

  /** Copy a node to a new path. */
  async copy(source: string, target: string): Promise<void> {
    await this.conn.observe(this.meshAddress, "CopyNodeRequest", { source, target }); // WIRE: CopyNodeRequest fields
  }

  /**
   * Run an executable Code/activity node by flipping its control-plane trigger to Running (the
   * operations-as-scripts pattern — the owning hub's watcher reacts). Subscribe with `watch(path)`
   * to follow Status to a terminal state.
   */
  execute(path: string): void {
    this.patch(path, { requestedStatus: "Running" }); // WIRE: confirm the activity control-plane field
  }
}
