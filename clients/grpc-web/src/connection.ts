// The gRPC-web participant connection — the browser + React-Native counterpart of the Node SDK's
// bidi MeshConnection (clients/typescript/src/connection.ts). Browsers and RN can't do the bidi `Open`
// (no HTTP/2 duplex, no Node http2), so the duplex is split server-side into a server-streaming
// `Connect` (mesh -> client) + a unary `Deliver` (client -> mesh); `Connect`'s ack carries the
// connection_id every `Deliver` quotes back. This class hides that split behind the SAME surface the
// Node SDK exposes — observe / post / watch — so it drops straight into the renderer's GrpcAreaSource
// as a `MeshConnectionLike`. See mesh.proto, MeshGrpcService.Connect/Deliver, and the C# WebSplit test.

import { createClient, type Client, type Interceptor } from "@connectrpc/connect";
import { createGrpcWebTransport } from "@connectrpc/connect-web";
import { Mesh } from "./gen/mesh_pb";
import { buildDeliver, newId, parseDelivery, type Delivery } from "./envelope";

export interface ConnectOptions {
  /** Bearer token — validated server-side; the server stamps the AccessContext, never the client. */
  token?: string;
  /** Override the participant address (default `node/<id>` — a stream-routed foreign-participant type). */
  address?: string;
  /** Sink for background send/stream faults that have no awaiting caller (default `console.error`). */
  onError?: (error: unknown) => void;
}

/** Bearer-token interceptor — the token rides in gRPC-web call metadata, exactly like the Node SDK. */
function bearer(token: string): Interceptor {
  return (next) => (req) => {
    req.header.set("Authorization", `Bearer ${token}`);
    return next(req);
  };
}

export class MeshWebConnection {
  readonly address: string;
  private readonly client: Client<typeof Mesh>;
  private readonly onError: (error: unknown) => void;
  private readonly abort = new AbortController();
  private connectionId = "";
  private readonly pending = new Map<string, (d: Delivery) => void>();
  private readonly subscriptions = new Map<string, (d: Delivery) => void>();

  private constructor(address: string, client: Client<typeof Mesh>, onError: (error: unknown) => void) {
    this.address = address;
    this.client = client;
    this.onError = onError;
  }

  static connect(url: string, opts: ConnectOptions = {}): Promise<MeshWebConnection> {
    const address = opts.address ?? `node/${newId()}`;
    const transport = createGrpcWebTransport({
      baseUrl: url,
      interceptors: opts.token ? [bearer(opts.token)] : [],
    });
    const conn = new MeshWebConnection(address, createClient(Mesh, transport), opts.onError ?? console.error);
    return conn.open();
  }

  /** Open the `Connect` server-stream, resolve once the ack lands, then pump receives forever. */
  private open(): Promise<MeshWebConnection> {
    return new Promise<MeshWebConnection>((resolve, reject) => {
      let acked = false;
      // The pump runs for the connection's whole life; the promise only gates on the first ack.
      void (async () => {
        try {
          for await (const frame of this.client.connect(
            { address: JSON.stringify(this.address) },
            { signal: this.abort.signal },
          )) {
            if (frame.kind.case === "ack") {
              this.connectionId = frame.kind.value.connectionId;
              acked = true;
              resolve(this);
            } else if (frame.kind.case === "receive") {
              this.onFrame(frame.kind.value);
            }
          }
          if (!acked) reject(new Error("connect: stream ended before ack"));
        } catch (error) {
          if (!acked) reject(error);
          else if (!this.abort.signal.aborted) this.onError(error); // a live stream dropped, not a close()
        }
      })();
    });
  }

  // A receive frame: correlate to a pending request, else demux to a live subscription.
  private onFrame(receive: string): void {
    if (!receive) return;
    const delivery = parseDelivery(receive);
    if (delivery.requestId && this.pending.has(delivery.requestId)) {
      const resolve = this.pending.get(delivery.requestId)!;
      this.pending.delete(delivery.requestId);
      resolve(delivery);
      return;
    }
    const streamId = (delivery.message["streamId"] ?? delivery.message["StreamId"]) as string | undefined;
    if (streamId && this.subscriptions.has(streamId)) this.subscriptions.get(streamId)!(delivery);
  }

  /** Send `delivery` to the mesh via the unary Deliver, tagged with our connection id. */
  private deliver(payload: string): Promise<unknown> {
    return this.client.deliver({ connectionId: this.connectionId, delivery: payload }, { signal: this.abort.signal });
  }

  /** Post a request to `target` and await its response (correlated by RequestId). */
  observe(target: string, messageType: string, message: Record<string, unknown>, timeoutMs = 30_000): Promise<Delivery> {
    const deliveryId = newId();
    const payload = buildDeliver({ deliveryId, sender: this.address, target, messageType, message });
    return new Promise<Delivery>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(deliveryId);
        reject(new Error(`observe timeout after ${timeoutMs}ms (${messageType} -> ${target})`));
      }, timeoutMs);
      this.pending.set(deliveryId, (d) => {
        clearTimeout(timer);
        resolve(d);
      });
      this.deliver(payload).catch((error) => {
        clearTimeout(timer);
        this.pending.delete(deliveryId);
        reject(error);
      });
    });
  }

  /** Fire-and-forget a message to `target`. */
  post(target: string, messageType: string, message: Record<string, unknown>): void {
    const payload = buildDeliver({ deliveryId: newId(), sender: this.address, target, messageType, message });
    this.deliver(payload).catch(this.onError); // no awaiting caller — route faults to the error sink
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
    this.subscriptions.set(streamId, (d) => {
      queue.push(d);
      wake?.();
      wake = null;
    });
    this.post(target, subscribeType, { ...subscribeMsg, streamId });
    try {
      while (!this.abort.signal.aborted) {
        if (queue.length === 0) await new Promise<void>((resolve) => (wake = resolve));
        while (queue.length) yield queue.shift()!;
      }
    } finally {
      this.subscriptions.delete(streamId);
    }
  }

  /** Tear down the connection: aborts the Connect stream and every in-flight Deliver. */
  close(): void {
    this.abort.abort();
  }
}

/** Connect a browser / React-Native app to the mesh as a participant over gRPC-web. */
export function connect(url: string, opts: ConnectOptions = {}): Promise<MeshWebConnection> {
  return MeshWebConnection.connect(url, opts);
}
