import { describe, expect, it } from "vitest";
import { bytesToBase64, base64GroupsToBytes, base64DecodeStream } from "./grpcWebTextCodec";

// The gRPC-web-text codec is the whole reason the native transport works: base64 survives Hermes' lossy
// UTF-8 text-streaming where raw binary corrupts. These run in Node (global ReadableStream) and pin the
// two things that actually broke on-device — bytes > 0x7F, and the trailer flag (0x80) across chunk splits.

function everyByte(n: number): Uint8Array {
  const a = new Uint8Array(n);
  for (let i = 0; i < n; i++) a[i] = i & 255;
  return a;
}
function asciiChunks(s: string, size: number): Uint8Array[] {
  const bytes = new Uint8Array(s.length);
  for (let i = 0; i < s.length; i++) bytes[i] = s.charCodeAt(i);
  const chunks: Uint8Array[] = [];
  for (let i = 0; i < bytes.length; i += size) chunks.push(bytes.subarray(i, i + size));
  return chunks;
}
function streamFromChunks(chunks: Uint8Array[]): any {
  let i = 0;
  return new (globalThis as any).ReadableStream({
    pull(c: any) { if (i < chunks.length) c.enqueue(chunks[i++]); else c.close(); },
  });
}
async function readAll(stream: any): Promise<Uint8Array> {
  const reader = stream.getReader();
  const parts: Uint8Array[] = [];
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    parts.push(value);
  }
  const len = parts.reduce((a, p) => a + p.length, 0);
  const out = new Uint8Array(len);
  let o = 0;
  for (const p of parts) { out.set(p, o); o += p.length; }
  return out;
}
const eq = (a: Uint8Array, b: Uint8Array) => a.length === b.length && a.every((v, i) => v === b[i]);

describe("gRPC-web-text codec", () => {
  it("round-trips every byte value (incl. > 0x7F) and every padding length", () => {
    for (const n of [0, 1, 2, 3, 4, 5, 255, 256, 257, 1000]) {
      const src = everyByte(n);
      const back = base64GroupsToBytes(bytesToBase64(src));
      expect(eq(back, src)).toBe(true);
    }
  });

  it("matches a reference base64 vector", () => {
    // "Man" → "TWFu"; a high byte 0xFF → "/w=="
    expect(bytesToBase64(new Uint8Array([0x4d, 0x61, 0x6e]))).toBe("TWFu");
    expect(bytesToBase64(new Uint8Array([0xff]))).toBe("/w==");
    expect([...base64GroupsToBytes("/w==")]).toEqual([0xff]);
  });

  it("streaming-decodes across ANY chunk boundary, preserving high bytes", async () => {
    const src = everyByte(777); // includes the full 0x00..0xFF range repeatedly
    const b64 = bytesToBase64(src);
    for (const size of [1, 2, 3, 4, 5, 7, 64, 999]) {
      const decoded = await readAll(base64DecodeStream(streamFromChunks(asciiChunks(b64, size))));
      expect(eq(decoded, src)).toBe(true);
    }
  });

  it("preserves a gRPC-web data frame + trailer frame (the 0x80 flag) byte-for-byte", async () => {
    // A gRPC-web data frame: [0x00][len:4 BE][payload], then the trailer frame: [0x80][len:4 BE][text].
    const payload = everyByte(200); // binary protobuf-ish body, lots of bytes > 0x7F
    const trailerText = new TextEncoder().encode("grpc-status:0\r\n");
    const frame = (flag: number, body: Uint8Array) => {
      const f = new Uint8Array(5 + body.length);
      f[0] = flag;
      f[1] = (body.length >>> 24) & 255;
      f[2] = (body.length >>> 16) & 255;
      f[3] = (body.length >>> 8) & 255;
      f[4] = body.length & 255;
      f.set(body, 5);
      return f;
    };
    const data = frame(0x00, payload);
    const trailer = frame(0x80, trailerText);
    const whole = new Uint8Array(data.length + trailer.length);
    whole.set(data, 0);
    whole.set(trailer, data.length);

    const b64 = bytesToBase64(whole);
    // Feed byte-by-byte — the most hostile split.
    const decoded = await readAll(base64DecodeStream(streamFromChunks(asciiChunks(b64, 1))));

    expect(eq(decoded, whole)).toBe(true);
    expect(decoded[0]).toBe(0x00); // data frame flag
    expect(decoded[data.length]).toBe(0x80); // trailer frame flag survived — the exact byte that corrupted
  });
});
