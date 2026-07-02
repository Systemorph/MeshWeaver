import { describe, expect, it, vi } from "vitest";
import { DEFAULT_TRANSCRIBE_PATH, extractText, SpeechTranscriptionClient } from "./transcription";

// The typed client for the centralized Whisper endpoint — driven against a mocked fetch, asserting
// the exact multipart contract WhisperContainerTranscriber speaks server-side (file + language +
// response_format=json → {"text": "…"}) and the portal-endpoint defaults from CentralizedSpeech.md.

const ok = (body: string) =>
  vi.fn(async () => new Response(body, { status: 200 })) as unknown as typeof fetch;

const audio = { bytes: new Uint8Array([1, 2, 3, 4]), contentType: "audio/wav", fileName: "audio.wav" };

describe("SpeechTranscriptionClient", () => {
  it("POSTs multipart (file + language + response_format) to the portal transcribe endpoint", async () => {
    const doFetch = ok(JSON.stringify({ text: " Grüezi mitenand " }));
    const client = new SpeechTranscriptionClient({ url: "https://memex.example/", fetch: doFetch, language: "de" });

    const transcript = await client.transcribe(audio);

    expect(transcript.text).toBe("Grüezi mitenand"); // trimmed, like the .NET transcriber
    expect(transcript.language).toBe("de");
    const [endpoint, init] = (doFetch as unknown as ReturnType<typeof vi.fn>).mock.calls[0] as [string, RequestInit];
    expect(endpoint).toBe(`https://memex.example${DEFAULT_TRANSCRIBE_PATH}`); // trailing slash normalized
    expect(init.method).toBe("POST");
    const form = init.body as FormData;
    expect(form.get("language")).toBe("de");
    expect(form.get("response_format")).toBe("json");
    expect(form.get("file")).toBeInstanceOf(Blob);
    expect(((form.get("file") as Blob).type)).toBe("audio/wav");
  });

  it("sends the bearer token and honors a custom path (dev: a bare whisper container /inference)", async () => {
    const doFetch = ok('{"text":"hoi"}');
    const client = new SpeechTranscriptionClient({
      url: "http://localhost:8080",
      path: "/inference",
      token: "mw_secret",
      fetch: doFetch,
    });
    await client.transcribe(audio);
    const [endpoint, init] = (doFetch as unknown as ReturnType<typeof vi.fn>).mock.calls[0] as [string, RequestInit];
    expect(endpoint).toBe("http://localhost:8080/inference");
    expect((init.headers as Record<string, string>).Authorization).toBe("Bearer mw_secret");
  });

  it("per-call language overrides the configured hint", async () => {
    const doFetch = ok('{"text":"salut"}');
    const client = new SpeechTranscriptionClient({ url: "https://memex.example", fetch: doFetch, language: "de" });
    const transcript = await client.transcribe(audio, { language: "fr" });
    expect(transcript.language).toBe("fr");
    const [, init] = (doFetch as unknown as ReturnType<typeof vi.fn>).mock.calls[0] as [string, RequestInit];
    expect((init.body as FormData).get("language")).toBe("fr");
  });

  it("propagates server errors (status + body) — never swallowed", async () => {
    const doFetch = vi.fn(async () => new Response("model not loaded", { status: 503 })) as unknown as typeof fetch;
    const client = new SpeechTranscriptionClient({ url: "https://memex.example", fetch: doFetch });
    await expect(client.transcribe(audio)).rejects.toThrow(/HTTP 503.*model not loaded/);
  });

  it("is lenient about the response shape (Text casing / raw text body), like the .NET ExtractText", () => {
    expect(extractText('{"text":"a"}')).toBe("a");
    expect(extractText('{"Text":"b"}')).toBe("b");
    expect(extractText("  plain transcript  ")).toBe("plain transcript");
    expect(extractText("{not json")).toBe("{not json");
  });
});
