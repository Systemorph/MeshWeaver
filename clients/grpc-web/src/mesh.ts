// Ergonomic mesh operations for the browser/RN client — the gRPC-web twin of @meshweaver/client's
// mesh.ts and the Python SDK's mesh.py (the in-language port of MeshWeaver.AI.MeshOperations). Each
// method is a thin composition of the MeshWebConnection primitives (observe / post / watch). The request
// type names / target addresses are the mesh's own contracts; annotated `WIRE:` where they must be
// confirmed against the running mesh (capture a sample from the C# round-trip test). The transport
// underneath is already correct + tested.

import { connect as connectTransport, MeshWebConnection, type ConnectOptions } from "./connection";
import { meshNodeFromChange, type MeshNode } from "./types";
import { newId } from "./envelope";
import {
  buildSubmitPatch,
  buildThreadNode,
  createUserMessage,
  isOwnerlessThreadPath,
  type StartThreadOptions,
  type SubmitMessageOptions,
} from "./threads";

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

  /** Wrap an ALREADY-connected transport (e.g. the one a GrpcAreaSource renders from) in the ops surface. */
  static from(connection: MeshWebConnection, meshAddress = "mesh/main"): Mesh {
    return new Mesh(connection, meshAddress);
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

  // ---- chat threads (the client twin of MeshWeaver.AI.HubThreadExtensions) --

  /**
   * Create a new chat thread under `namespacePath` with the first user message queued — the
   * in-language port of `hub.StartThread(...)`: ONE CreateNodeRequest carrying the thread node
   * pre-seeded with content.pendingUserMessages, targeted at the namespace's hub (node-lifecycle,
   * not a mutation). The per-thread submission watcher dispatches the first round when the thread
   * hub activates. Follow the round with `watch(path)` (the same stream the GUI binds).
   *
   * NOTE: the .NET portal additionally falls back to the user's own partition on an access denial —
   * that is portal UX; the SDK surfaces the error to the caller instead.
   */
  async startThread(
    namespacePath: string,
    userText: string,
    opts: StartThreadOptions = {},
  ): Promise<{ path: string; userMessageId: string; node: Record<string, unknown> }> {
    const { node, path, userMessageId } = buildThreadNode(namespacePath, userText, opts);
    // Target the namespace address, exactly like hub.Post(CreateNodeRequest, o => o.WithTarget(new Address(ns))).
    const resp = await this.conn.observe(namespacePath, "CreateNodeRequest", { node });
    const success = (resp.message["success"] ?? resp.message["Success"]) as boolean | undefined;
    if (success === false) {
      const error = (resp.message["error"] ?? resp.message["Error"]) as string | undefined;
      throw new Error(`Thread creation failed: ${error ?? "unknown"}`);
    }
    const created = (resp.message["node"] ?? resp.message["Node"]) as Record<string, unknown> | undefined;
    return { path, userMessageId, node: created ?? node };
  }

  /**
   * Queue a user message on an existing thread — the port of `hub.SubmitMessage(...)`: a JSON-merge
   * patch adding the id to content.userMessageIds + the payload to content.pendingUserMessages (the
   * client-side `stream.Update`; the owning hub serialises the patch). Resolves to the new message
   * id, or null when there is nothing to submit (whitespace-only text — mirrors the .NET no-op).
   *
   * NOTE: userMessageIds is an ARRAY, and merge-patch replaces arrays wholesale — so this reads the
   * thread first and sends the appended list. Unlike the .NET stream.Update (which diffs against the
   * owner-fresh state), the read here can be momentarily stale under concurrent submitters.
   */
  async submitMessage(threadPath: string, userText: string, opts: SubmitMessageOptions = {}): Promise<string | null> {
    if (!threadPath) throw new Error("submitMessage requires threadPath. Use startThread for new threads.");
    if (isOwnerlessThreadPath(threadPath))
      throw new Error(`submitMessage refused a top-level/ownerless threadPath: ${threadPath}`);
    if (userText.trim().length === 0) return null; // nothing to submit — never enqueue an empty round

    const current = await this.get(threadPath);
    const currentIds = ((current.content["userMessageIds"] ?? current.content["UserMessageIds"]) as string[]) ?? [];
    const msgId = newId().slice(0, 8);
    const message = createUserMessage(userText, opts);
    this.patch(threadPath, buildSubmitPatch(currentIds, msgId, message, opts));
    return msgId;
  }
}
