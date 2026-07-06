// The gRPC participant connection — the Node/Bun counterpart of the SignalR client. One bidi
// Open stream IS the mesh participant connection (see mesh.proto). Owns the transport, request/
// response correlation (by RequestId), and live-stream demux (by StreamId). Mirrors the Python
// SDK's connection.py. The proto is loaded at runtime via @grpc/proto-loader — no codegen step.

import * as grpc from "@grpc/grpc-js";
import * as protoLoader from "@grpc/proto-loader";
import { randomUUID } from "node:crypto";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { buildDeliver, parseDelivery, type Delivery } from "./envelope.js";

const here = path.dirname(fileURLToPath(import.meta.url));
// Canonical proto lives with the C# server — one contract, no copies. Override with MESHWEAVER_PROTO.
const PROTO_PATH =
  process.env.MESHWEAVER_PROTO ??
  path.resolve(here, "../../../src/MeshWeaver.Hosting.Grpc/Protos/mesh.proto");

type Frame = Record<string, unknown>;
type DuplexCall = grpc.ClientDuplexStream<Frame, Frame>;

export interface ConnectOptions {
  token?: string;
  address?: string;
}

export class MeshConnection {
  readonly address: string;
  private readonly call: DuplexCall;
  private readonly pending = new Map<string, (d: Delivery) => void>();
  private readonly subscriptions = new Map<string, (d: Delivery) => void>();
  private inbound: ((d: Delivery) => void | Promise<void>) | null = null;

  private constructor(address: string, call: DuplexCall) {
    this.address = address;
    this.call = call;
  }

  static async connect(url: string, opts: ConnectOptions = {}): Promise<MeshConnection> {
    const address = opts.address ?? `node/${randomUUID().replace(/-/g, "")}`;
    const target = url.replace(/^https?:\/\//, "");
    const creds = url.startsWith("https://")
      ? grpc.credentials.createSsl()
      : grpc.credentials.createInsecure();

    const pkgDef = protoLoader.loadSync(PROTO_PATH, {
      keepCase: false,
      longs: String,
      defaults: true,
      oneofs: true,
    });
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const proto = grpc.loadPackageDefinition(pkgDef) as any;
    const client = new proto.meshweaver.v1.Mesh(target, creds);

    const metadata = new grpc.Metadata();
    if (opts.token) metadata.set("authorization", `Bearer ${opts.token}`);
    const call: DuplexCall = client.Open(metadata);

    const conn = new MeshConnection(address, call);
    const ack = new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("connect: no ack within 30s")), 30_000);
      call.on("data", (frame: Frame) => conn.onFrame(frame, () => { clearTimeout(timer); resolve(); }));
      call.on("error", (e) => { clearTimeout(timer); reject(e); });
    });
    // Register our address for inbound routing, then wait for the server's ack.
    call.write({ connect: JSON.stringify(address) });
    await ack;
    return conn;
  }

  private onFrame(frame: Frame, onAck?: () => void): void {
    if (frame.ack !== undefined) { onAck?.(); return; }
    const receive = frame.receive as string | undefined;
    if (receive === undefined || receive === "") return;
    const delivery = parseDelivery(receive);
    // 1) request/response correlation
    if (delivery.requestId && this.pending.has(delivery.requestId)) {
      const resolve = this.pending.get(delivery.requestId)!;
      this.pending.delete(delivery.requestId);
      resolve(delivery);
      return;
    }
    // 2) live-stream change → demux by StreamId carried in the change message
    const streamId = (delivery.message["streamId"] ?? delivery.message["StreamId"]) as string | undefined;
    if (streamId && this.subscriptions.has(streamId)) { this.subscriptions.get(streamId)!(delivery); return; }
    // 3) an unsolicited inbound request targeted at us (e.g. SubmitCodeRequest) → the served handler
    if (this.inbound) void this.inbound(delivery);
  }

  /**
   * Register the handler for unsolicited inbound deliveries — requests targeted at THIS participant
   * (a SubmitCodeRequest for a worker, a PandasCommand for the pandas node, …). Responses and
   * live-stream frames are dispatched before this; the handler only sees genuine inbound requests.
   */
  serve(handler: (d: Delivery) => void | Promise<void>): void {
    this.inbound = handler;
  }

  /**
   * Reply to a request delivery. Correlated by RequestId = the request's id and routed back to its
   * sender, so a caller's `observe(...)` resolves. `accessContext` may echo the request's identity.
   */
  respond(request: Delivery, messageType: string, message: Record<string, unknown>): void {
    if (!request.sender) return;
    const deliveryId = randomUUID().replace(/-/g, "");
    this.call.write({ deliver: buildDeliver({
      deliveryId, sender: this.address, target: request.sender, messageType, message,
      requestId: request.id, accessContext: request.accessContext,
    }) });
  }

  /** Post a request to `target` and await its response (correlated by RequestId). */
  observe(target: string, messageType: string, message: Record<string, unknown>, timeoutMs = 30_000): Promise<Delivery> {
    const deliveryId = randomUUID().replace(/-/g, "");
    const payload = buildDeliver({ deliveryId, sender: this.address, target, messageType, message });
    return new Promise<Delivery>((resolve, reject) => {
      const timer = setTimeout(() => { this.pending.delete(deliveryId); reject(new Error("observe timeout")); }, timeoutMs);
      this.pending.set(deliveryId, (d) => { clearTimeout(timer); resolve(d); });
      this.call.write({ deliver: payload });
    });
  }

  /** Fire-and-forget a message to `target`, optionally under the caller's `accessContext`. */
  post(target: string, messageType: string, message: Record<string, unknown>,
       accessContext?: Record<string, unknown>): void {
    const deliveryId = randomUUID().replace(/-/g, "");
    this.call.write({ deliver: buildDeliver({ deliveryId, sender: this.address, target, messageType, message, accessContext }) });
  }

  /** Open a live stream: post a subscribe request, then yield each change addressed back to us. */
  async *watch(
    target: string,
    streamId: string,
    subscribeType: string,
    subscribeMsg: Record<string, unknown>,
  ): AsyncIterableIterator<Delivery> {
    const queue: Delivery[] = [];
    let wake: (() => void) | null = null;
    this.subscriptions.set(streamId, (d) => { queue.push(d); wake?.(); wake = null; });
    this.post(target, subscribeType, { ...subscribeMsg, streamId });
    try {
      while (true) {
        if (queue.length === 0) await new Promise<void>((resolve) => { wake = resolve; });
        while (queue.length) yield queue.shift()!;
      }
    } finally {
      this.subscriptions.delete(streamId);
    }
  }

  /** Resolves when the underlying stream ends or errors — lets a gate detect a dropped portal and reconnect. */
  waitClosed(): Promise<void> {
    return new Promise<void>((resolve) => {
      this.call.on("end", () => resolve());
      this.call.on("error", () => resolve());
    });
  }

  close(): void {
    this.call.end();
  }
}

/** Connect a Node/Bun process to the mesh as a participant. */
export function connect(url: string, opts: ConnectOptions = {}): Promise<MeshConnection> {
  return MeshConnection.connect(url, opts);
}
