// Typed client for the CENTRALIZED speech-to-text service (Doc/Architecture/CentralizedSpeech):
// the whole point of this branch is that the Swiss-German Whisper model runs ONCE as a container
// (deploy/whisper) and every client — Blazor portal, React Native, MAUI — posts audio to one place.
// No on-device recognition.
//
// Contract: multipart POST (`file` + `language` + `response_format=json`) → `{"text": "…"}` — the
// exact whisper.cpp `/inference` contract that `MeshWeaver.Speech.WhisperContainerTranscriber`
// speaks server-side (and the shape the portal endpoint forwards).
//
// TODO(portal endpoint): CentralizedSpeech.md specifies the client-facing endpoint as
// `POST /api/speech/transcribe` on the PORTAL (so the Whisper container stays cluster-internal
// behind portal auth) — that endpoint is still "next" on this branch. This client already speaks
// the intended contract; once the portal endpoint lands, only `url` matters (the default `path`
// below is the portal endpoint). For dev against a bare Whisper container use
// `{ url: "http://localhost:8080", path: "/inference" }` (see deploy/whisper/README.md).
//
// Pure TS — no react-native imports — so it unit-tests under vitest and runs in RN/Hermes alike
// (fetch/FormData are global on both; file parts use RN's `{ uri, name, type }` FormData extension).

/** The result of a transcription — mirrors MeshWeaver.Speech.SpeechTranscript. */
export interface SpeechTranscript {
  text: string;
  /** The resolved Whisper language, when the server reports it (else the requested hint). */
  language?: string;
}

/**
 * Recorded audio to transcribe. RN recorders produce a file `uri` (appended to FormData via RN's
 * `{ uri, name, type }` extension — no byte copy); tests and web callers pass `bytes`.
 */
export type AudioInput =
  | { uri: string; contentType: string; fileName: string; durationMs?: number }
  | { bytes: Uint8Array | ArrayBuffer; contentType: string; fileName: string; durationMs?: number };

export interface TranscriptionClientOptions {
  /** Base URL — the portal (same base as LiveOptions.url), or a Whisper container for dev. */
  url: string;
  /** Bearer token (mw_…) — the portal endpoint sits behind portal auth. */
  token?: string;
  /**
   * Endpoint path. Default: the portal's `/api/speech/transcribe` (CentralizedSpeech.md). For a
   * bare whisper.cpp container use `/inference` — the multipart contract is identical.
   */
  path?: string;
  /** Whisper language hint. Default "de" — Swiss German transcribes OUT as Standard German. */
  language?: string;
  /** Per-request timeout in ms. Default 120 000 (mirrors SpeechConfiguration.TimeoutSeconds). */
  timeoutMs?: number;
  /** Injectable fetch (tests; or a polyfilled fetch on RN). Default: the global fetch. */
  fetch?: typeof fetch;
}

export interface TranscribeOptions {
  /** Override the configured language for this call (e.g. force "fr"). */
  language?: string;
  signal?: AbortSignal;
}

/** The portal endpoint from CentralizedSpeech.md — clients never see the container directly in prod. */
export const DEFAULT_TRANSCRIBE_PATH = "/api/speech/transcribe";

export class SpeechTranscriptionClient {
  private readonly opts: TranscriptionClientOptions;

  constructor(opts: TranscriptionClientOptions) {
    if (!opts.url) throw new Error("SpeechTranscriptionClient requires a url.");
    this.opts = opts;
  }

  /** POST the audio to the centralized endpoint and resolve the transcript. Errors surface, never swallowed. */
  async transcribe(audio: AudioInput, options: TranscribeOptions = {}): Promise<SpeechTranscript> {
    const language = options.language ?? this.opts.language ?? "de";
    const endpoint =
      this.opts.url.replace(/\/+$/, "") + (this.opts.path ?? DEFAULT_TRANSCRIBE_PATH);

    const form = new FormData();
    if ("uri" in audio) {
      // React Native FormData accepts a file descriptor `{ uri, name, type }` — the platform
      // streams the file; the DOM lib types don't know the extension, hence the cast.
      form.append("file", { uri: audio.uri, name: audio.fileName, type: audio.contentType } as unknown as Blob);
    } else {
      const bytes = audio.bytes instanceof Uint8Array ? new Uint8Array(audio.bytes) : new Uint8Array(audio.bytes);
      form.append("file", new Blob([bytes.buffer as ArrayBuffer], { type: audio.contentType }), audio.fileName);
    }
    form.append("language", language);
    form.append("response_format", "json"); // whisper.cpp → {"text": "..."}

    // Per-call timeout — same behaviour as WhisperContainerTranscriber's linked token.
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), this.opts.timeoutMs ?? 120_000);
    const abortUpstream = () => controller.abort();
    options.signal?.addEventListener("abort", abortUpstream);

    try {
      const doFetch = this.opts.fetch ?? fetch;
      const response = await doFetch(endpoint, {
        method: "POST",
        body: form,
        headers: this.opts.token ? { Authorization: `Bearer ${this.opts.token}` } : undefined,
        signal: controller.signal,
      });
      const body = await response.text();
      if (!response.ok)
        throw new Error(`Transcription failed: HTTP ${response.status} ${body.slice(0, 200)}`);
      return { text: extractText(body).trim(), language };
    } finally {
      clearTimeout(timeout);
      options.signal?.removeEventListener("abort", abortUpstream);
    }
  }
}

/**
 * Lenient response parsing — the twin of WhisperContainerTranscriber.ExtractText: JSON
 * `{"text"|"Text": "…"}` when the body is JSON, else the raw body (`response_format=text`).
 */
export function extractText(body: string): string {
  const trimmed = body.trim();
  if (trimmed.length === 0 || trimmed[0] !== "{") return trimmed;
  try {
    const root = JSON.parse(trimmed) as Record<string, unknown>;
    const t = root["text"] ?? root["Text"];
    if (typeof t === "string") return t;
  } catch {
    // Not JSON after all — fall through to the raw body rather than throwing away a transcript.
  }
  return trimmed;
}
