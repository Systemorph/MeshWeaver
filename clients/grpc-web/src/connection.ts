// The gRPC-web participant connection — the browser + React-Native counterpart of the Node SDK's
// bidi MeshConnection (clients/typescript/src/connection.ts). Browsers and RN can't do the bidi `Open`
// (no HTTP/2 duplex, no Node http2), so the duplex is split server-side into a server-streaming
// `Connect` (mesh -> client) + a unary `Deliver` (client -> mesh); `Connect`'s ack carries the
// connection_id every `Deliver` quotes back. This class hides that split behind the SAME surface the
// Node SDK exposes — observe / post / watch — so it drops straight into the renderer's GrpcAreaSource
// as a `MeshConnectionLike`. See mesh.proto, MeshGrpcService.Connect/Deliver, and the C# WebSplit test.

import { createClient, type Client, type Interceptor, type Transport } from "@connectrpc/connect";
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
  /**
   * Inject the Connect transport instead of the default gRPC-web one. Use to supply a custom `fetch`
   * (e.g. a streaming-fetch polyfill on React Native) or an in-memory transport for tests. When set,
   * `token` is ignored — add your own auth interceptor to the transport.
   */
  transport?: Transport;
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
  // The SubscribeRequest params per live streamId — kept so a dropped Connect stream can be
  // re-opened and every subscription REPLAYED (see open()'s reconnect loop). Without replay, a
  // mid-delivery drop strands the UI on whatever subset of area frames arrived first.
  private readonly subscribeParams = new Map<string, { target: string; type: string; msg: Record<string, unknown> }>();

  private constructor(address: string, client: Client<typeof Mesh>, onError: (error: unknown) => void) {
    this.address = address;
    this.client = client;
    this.onError = onError;
  }

  static connect(url: string, opts: ConnectOptions = {}): Promise<MeshWebConnection> {
    const address = opts.address ?? `node/${newId()}`;
    const transport =
      opts.transport ??
      createGrpcWebTransport({ baseUrl: url, interceptors: opts.token ? [bearer(opts.token)] : [] });
    const conn = new MeshWebConnection(address, createClient(Mesh, transport), opts.onError ?? console.error);
    return conn.open();
  }

  /**
   * Open the `Connect` server-stream, resolve once the FIRST ack lands, then pump receives — with an
   * automatic RECONNECT loop. The gRPC-web server-stream can drop (proxy idle-close, network blip,
   * a truncating port-forward): the browser then renders only the SUBSET of area frames that arrived
   * before the drop, differently each reload ("renders a random subset / stops prematurely"). On a
   * drop we re-open the stream and REPLAY every active SubscribeRequest, so the missing streams get a
   * fresh Full frame; already-delivered streams dedup it by version (GrpcAreaSource), so replay
   * completes the page without disturbing what's already rendered. Only a real close() (abort) stops it.
   */
  private open(): Promise<MeshWebConnection> {
    return new Promise<MeshWebConnection>((resolve, reject) => {
      let firstAck = false;
      let attempt = 0;
      void (async () => {
        while (!this.abort.signal.aborted) {
          try {
            for await (const frame of this.client.connect(
              { address: JSON.stringify(this.address) },
              { signal: this.abort.signal },
            )) {
              if (frame.kind.case === "ack") {
                this.connectionId = frame.kind.value.connectionId;
                attempt = 0;
                if (!firstAck) {
                  firstAck = true;
                  resolve(this);
                } else {
                  // A reconnect ack: re-establish every live stream on the new connection id.
                  this.replaySubscriptions();
                }
              } else if (frame.kind.case === "receive") {
                this.onFrame(frame.kind.value);
              }
            }
            // Clean end (server closed the stream). If it ended BEFORE the first ack, the caller's
            // connect() would otherwise never settle — reject instead of silently entering the
            // reconnect loop (there was never a live connection to reconnect).
            if (!firstAck) {
              if (!this.abort.signal.aborted) reject(new Error("connect: stream ended before ack"));
              return;
            }
            // Acked once already — fall through to reconnect below.
          } catch (error) {
            if (this.abort.signal.aborted) return;
            if (!firstAck) {
              reject(error);
              return;
            }
            this.onError(error); // a live stream dropped — reconnect below
          }
          if (this.abort.signal.aborted) return;
          // Backoff before reconnecting (linear, capped ~5s); abort cancels the wait immediately.
          attempt++;
          await this.sleep(Math.min(250 * attempt, 5000));
        }
      })();
    });
  }

  /** Re-post the SubscribeRequest for every live stream (after a reconnect ack). */
  private replaySubscriptions(): void {
    for (const [streamId, p] of this.subscribeParams) this.post(p.target, p.type, { ...p.msg, streamId });
  }

  /** Abort-aware delay for the reconnect backoff. Cleans up the abort listener on BOTH paths (normal
   *  timer fire and abort) so listeners don't accumulate on the AbortSignal across reconnects. */
  private sleep(ms: number): Promise<void> {
    return new Promise<void>((resolve) => {
      let timer: ReturnType<typeof setTimeout>;
      const finish = () => {
        clearTimeout(timer);
        this.abort.signal.removeEventListener("abort", finish);
        resolve();
      };
      timer = setTimeout(finish, ms);
      this.abort.signal.addEventListener("abort", finish, { once: true });
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
    // Remember the subscribe params so a reconnect can replay this stream (see open()).
    this.subscribeParams.set(streamId, { target, type: subscribeType, msg: subscribeMsg });
    this.post(target, subscribeType, { ...subscribeMsg, streamId });
    try {
      while (!this.abort.signal.aborted) {
        if (queue.length === 0) await new Promise<void>((resolve) => (wake = resolve));
        while (queue.length) yield queue.shift()!;
      }
    } finally {
      this.subscriptions.delete(streamId);
      this.subscribeParams.delete(streamId);
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
