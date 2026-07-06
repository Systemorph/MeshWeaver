// gRPC-web-TEXT (base64) codec — the platform-agnostic core of the React Native transport
// (nativeFetch.native.ts). Kept import-free (no react-native-fetch-api) so it unit-tests under Node/vitest,
// where the base64 round-trip and the chunk-boundary streaming decode — the parts that actually break on
// binary data > 0x7F — are verified deterministically. Uses the GLOBAL ReadableStream (Node has it; RN gets
// the web-streams polyfill installed before use).

const B64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
const REV = (() => {
  const r = new Int16Array(128).fill(-1);
  for (let i = 0; i < B64.length; i++) r[B64.charCodeAt(i)] = i;
  return r;
})();

export function toUint8(body: any): Uint8Array {
  if (body == null) return new Uint8Array(0);
  if (body instanceof Uint8Array) return body;
  if (body instanceof ArrayBuffer) return new Uint8Array(body);
  if (ArrayBuffer.isView(body)) return new Uint8Array(body.buffer, body.byteOffset, body.byteLength);
  return new Uint8Array(0);
}

export function bytesToBase64(bytes: Uint8Array): string {
  let out = "";
  let i = 0;
  for (; i + 2 < bytes.length; i += 3) {
    const n = (bytes[i] << 16) | (bytes[i + 1] << 8) | bytes[i + 2];
    out += B64[(n >> 18) & 63] + B64[(n >> 12) & 63] + B64[(n >> 6) & 63] + B64[n & 63];
  }
  const rem = bytes.length - i;
  if (rem === 1) {
    const n = bytes[i] << 16;
    out += B64[(n >> 18) & 63] + B64[(n >> 12) & 63] + "==";
  } else if (rem === 2) {
    const n = (bytes[i] << 16) | (bytes[i + 1] << 8);
    out += B64[(n >> 18) & 63] + B64[(n >> 12) & 63] + B64[(n >> 6) & 63] + "=";
  }
  return out;
}

// Decode a base64 string whose length is a multiple of 4 (may carry '=' padding at group ends). Over-
// allocates then trims, so mid-stream padded groups (per-block base64) decode correctly too. Non-base64
// bytes (stray whitespace) are skipped rather than throwing.
// REV is only 128 wide; a char code ≥ 128 (or NaN past the end) indexes out of bounds → `undefined`,
// which is NOT caught by a `< 0` check. Fold every out-of-range/non-base64 char to -1 so it's skipped.
const rev = (cc: number): number => (cc >= 0 && cc < 128 ? REV[cc] : -1);

export function base64GroupsToBytes(s: string): Uint8Array {
  const out = new Uint8Array((Math.ceil(s.length / 4)) * 3);
  let o = 0;
  for (let i = 0; i + 3 < s.length; i += 4) {
    const c2 = s.charCodeAt(i + 2);
    const c3 = s.charCodeAt(i + 3);
    const a = rev(s.charCodeAt(i));
    const b = rev(s.charCodeAt(i + 1));
    const c = c2 === 61 ? 0 : rev(c2); // 61 = '='
    const d = c3 === 61 ? 0 : rev(c3);
    if (a < 0 || b < 0 || c < 0 || d < 0) continue;
    const n = (a << 18) | (b << 12) | (c << 6) | d;
    out[o++] = (n >> 16) & 255;
    if (c2 !== 61) out[o++] = (n >> 8) & 255;
    if (c3 !== 61) out[o++] = n & 255;
  }
  return out.subarray(0, o);
}

/**
 * Wrap a ReadableStream of ASCII base64 bytes (the grpc-web-text response) as a ReadableStream of the
 * decoded binary bytes connect-web's envelope reader expects. Buffers partial 4-char groups across chunk
 * boundaries — the split-point robustness is what the unit test hammers.
 */
export function base64DecodeStream(src: any): any {
  const Stream = (globalThis as any).ReadableStream;
  const reader = src.getReader();
  let buf = "";
  return new Stream({
    // Loop until this pull ENQUEUES or CLOSES — a pull that reads a chunk not completing a 4-char group
    // must not resolve empty (that strands the pending reader: the stream won't re-pull until the next
    // read() that never comes → deadlock). Keep reading source chunks until a group is ready or done.
    async pull(controller: any) {
      for (;;) {
        const { done, value } = await reader.read();
        if (done) {
          const rest = buf;
          buf = "";
          if (rest.length) {
            const padded = rest + "=".repeat((4 - (rest.length % 4)) % 4);
            const bytes = base64GroupsToBytes(padded);
            if (bytes.length) controller.enqueue(bytes);
          }
          controller.close();
          return;
        }
        let s = "";
        for (let i = 0; i < value.length; i++) s += String.fromCharCode(value[i]);
        buf += s;
        const cut = buf.length - (buf.length % 4);
        if (cut > 0) {
          const bytes = base64GroupsToBytes(buf.slice(0, cut));
          buf = buf.slice(cut);
          if (bytes.length) {
            controller.enqueue(bytes);
            return;
          }
        }
      }
    },
    cancel(reason: any) {
      try { reader.cancel(reason); } catch { /* already released */ }
    },
  });
}
