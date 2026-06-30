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
}
