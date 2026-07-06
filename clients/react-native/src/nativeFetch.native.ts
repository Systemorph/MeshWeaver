// React Native (Hermes) transport for gRPC-web. Two Hermes limitations to defeat:
//
//  1. Hermes' built-in `fetch` can't expose a readable response body — but the mesh's `Connect` call is
//     a gRPC-web SERVER STREAM, so the transport must read the body incrementally. react-native-fetch-api
//     surfaces the body as a stream (via XHR `textStreaming`).
//  2. That streaming path is TEXT: RN decodes the response bytes as UTF-8 and react-native-fetch-api
//     re-encodes them (Fetch.js), which is LOSSY for any byte > 0x7F — so binary gRPC-web frames (and the
//     trailer) corrupt ("missing trailer"). The tiny ASCII ack survives, larger protobuf frames don't.
//
// Fix: speak gRPC-web-TEXT (base64) instead of binary. Base64 is ASCII, so it round-trips the lossy text
// path intact. The server (Grpc.AspNetCore.Web `UseGrpcWeb()`) already accepts `application/grpc-web-text`.
// connect-web only speaks BINARY gRPC-web, so we transcode transparently in the fetch seam: base64-encode
// the request body + flip the content-type to grpc-web-text on the way out, and base64-DECODE the response
// stream + present it back as `application/grpc-web+proto` on the way in. connect-web keeps doing binary
// gRPC-web against a fully binary-looking Response; the wire underneath is ASCII. Injected via
// connect(url, { fetch }) in live.ts. Web uses nativeFetch.ts (no transcode — browsers stream binary fine).
// The base64 + stream logic lives in grpcWebTextCodec.ts (unit-tested under Node).
import { fetch as rnFetch } from "react-native-fetch-api";
import { ReadableStream } from "web-streams-polyfill";
import { TextEncoder, TextDecoder } from "text-encoding";
import { base64DecodeStream, bytesToBase64, toUint8 } from "./grpcWebTextCodec";

let installed = false;
function installGlobals(): void {
  if (installed) return;
  const g = globalThis as any;
  if (typeof g.ReadableStream === "undefined") g.ReadableStream = ReadableStream;
  if (typeof g.TextEncoder === "undefined") g.TextEncoder = TextEncoder;
  if (typeof g.TextDecoder === "undefined") g.TextDecoder = TextDecoder;
  installed = true;
}

// react-native-fetch-api's Headers ingests ONLY its own Headers class / an array / a plain object — NOT
// the global Headers instance connect-web hands us (its fields aren't own-enumerable), so those headers
// silently vanish. Flatten to [name, value] pairs so Content-Type et al. survive.
function toHeaderPairs(h: any): [string, string][] {
  if (!h) return [];
  if (Array.isArray(h)) return h as [string, string][];
  if (typeof h.forEach === "function") {
    const out: [string, string][] = [];
    h.forEach((value: string, name: string) => out.push([name, value]));
    return out;
  }
  return Object.keys(h).map((name) => [name, String(h[name])]);
}

const GRPC_WEB_BINARY = /^application\/grpc-web(\+proto)?$/i;

export function nativeStreamingFetch(): typeof globalThis.fetch {
  installGlobals();
  return (async (input: any, init?: any) => {
    const pairs = toHeaderPairs(init?.headers);
    const ctIdx = pairs.findIndex(([k]) => k.toLowerCase() === "content-type");
    const isGrpcWeb = ctIdx >= 0 && GRPC_WEB_BINARY.test(pairs[ctIdx][1]);

    // Non-gRPC-web (e.g. the speech multipart POST) — plain streaming fetch, headers preserved.
    if (!isGrpcWeb) {
      return rnFetch(input, { ...(init ?? {}), headers: pairs, reactNative: { textStreaming: true } });
    }

    // Request: base64 the binary envelope + advertise grpc-web-text. The server (Grpc.AspNetCore.Web)
    // only RESPONDS in text (base64) when the request ACCEPTS it — without this Accept header it replies
    // binary grpc-web, which then corrupts over Hermes' UTF-8 text streaming. connect-web never sets it.
    const outPairs = pairs
      .filter(([k]) => k.toLowerCase() !== "accept")
      .map(([k, v]): [string, string] =>
        k.toLowerCase() === "content-type" ? [k, v.replace(/grpc-web/i, "grpc-web-text")] : [k, v],
      );
    outPairs.push(["Accept", "application/grpc-web-text+proto"]);
    const body = init?.body != null ? bytesToBase64(toUint8(init.body)) : undefined;

    const resp: any = await rnFetch(input, {
      ...(init ?? {}),
      headers: outPairs,
      body,
      reactNative: { textStreaming: true },
    });

    // Response: base64-decode the stream + present a binary grpc-web+proto face to connect-web.
    const headers = new Headers();
    resp.headers?.forEach?.((value: string, name: string) => headers.set(name, value));
    headers.set("content-type", "application/grpc-web+proto");

    return {
      status: resp.status,
      ok: resp.status >= 200 && resp.status < 300,
      headers,
      body: resp.body ? base64DecodeStream(resp.body) : null,
    };
  }) as unknown as typeof globalThis.fetch;
}
