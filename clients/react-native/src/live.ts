// Live mesh data on React Native — a GrpcAreaSource fed by the gRPC-web client (@meshweaver/client-web).
// RN can't use @grpc/grpc-js (Node http2) or the bidi `Open`, so the client speaks the server's
// Connect+Deliver gRPC-web split. Swap createLiveSource(...) in for StaticAreaSource in App.tsx to render
// a real portal layout area instead of the bundled sample. See README "Live transport".
//
// Runtime note: gRPC-web server-streaming needs a fetch with a readable response body. Browsers have it;
// on RN/Hermes install a streaming-fetch polyfill (e.g. react-native-fetch-api + a TextDecoder) before
// connecting. The code below is transport-correct; only the platform fetch needs that capability.

import { GrpcAreaSource, type AreaSource } from "@meshweaver/react/core";
import { connect, type MeshWebConnection } from "@meshweaver/client-web";
import { nativeStreamingFetch } from "./nativeFetch";

export interface LiveOptions {
  /** Portal base URL, e.g. https://atioz.meshweaver.cloud */
  url: string;
  /** Bearer token (mw_…) — validated server-side; the server stamps the AccessContext, never the client. */
  token: string;
  /** The layout-area host address to subscribe to, e.g. "@app/Home". */
  address: string;
  /** The area name on that host. */
  area: string;
  /** Optional area instance id. */
  id?: string;
}

export interface LiveSource {
  source: AreaSource;
  connection: MeshWebConnection;
}

/** Connect over gRPC-web and return a started AreaSource the renderer consumes, plus the connection to close. */
export async function createLiveSource(opts: LiveOptions): Promise<LiveSource> {
  // On a device the Hermes fetch can't stream the Connect server-stream; nativeStreamingFetch() supplies
  // the polyfilled streaming fetch (undefined on web → the browser fetch is used unchanged).
  const connection = await connect(opts.url, { token: opts.token, fetch: nativeStreamingFetch() });
  const source = new GrpcAreaSource(connection, opts.address, { area: opts.area, id: opts.id });
  void source.start(); // folds the live area stream into the tree as merge-patches arrive
  return { source, connection };
}
