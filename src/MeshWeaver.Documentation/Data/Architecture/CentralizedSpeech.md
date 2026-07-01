---
NodeType: Markdown
Name: "Centralized speech — Whisper Swiss German as a container"
Abstract: "Inverting on-device Whisper into ONE hosted container the portal configures and every client (portal, React Native, MAUI) calls. The whisper.cpp server holds the Swiss-German model; MeshWeaver.Speech is the mesh-side client (ISpeechTranscriber over POST /inference, on the HTTP IIoPool); config lives in SpeechConfiguration and is set from the portal."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#0f6cbd'/><rect x='9.5' y='4' width='5' height='9' rx='2.5' fill='white'/><path d='M7 10.5a5 5 0 0 0 10 0' stroke='white' stroke-width='1.5' fill='none'/><rect x='11.2' y='15' width='1.6' height='3' fill='white'/><circle cx='12' cy='20' r='1.6' fill='white'/></svg>"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Voice"
  - "Whisper"
  - "Speech"
---

The MAUI client transcribes speech **on the device** with Whisper.net — see
[On-device voice](/Doc/Architecture/OnDeviceVoice). That's perfect for a phone (offline, private), but it
means the Swiss-German model is downloaded and run **per client**, and a browser can't do it at all. This
page describes the **inversion**: run the same fine-tuned Swiss-German Whisper model **once, as a container**,
and expose transcription to **every** client — the Blazor portal, React Native, and MAUI — from one place the
portal configures.

```
  Blazor chat mic ┐                         POST /api/speech/transcribe
  React Native    ┼── record WAV ──────────▶ (portal endpoint — "expose from everywhere")
  MAUI            ┘                                   │
                                                      ▼  ISpeechTranscriber  (mesh, HTTP IIoPool)
                                          WhisperContainerTranscriber
                                                      │  POST /inference (multipart)
                                    configure ───────▶ whisper.cpp CONTAINER + ggml-swiss-german-turbo-q5_0
                              (SpeechConfiguration: Endpoint, Language, Enabled — set in the portal)
```

## The pieces

| Piece | Where | Status |
|---|---|---|
| **The container** — `whisper.cpp` server + the Swiss-German model | `deploy/whisper/` (Dockerfile + compose + README) | built; not runtime-verified in CI (no Docker in the sandbox) |
| **`SpeechConfiguration`** — endpoint, language, enabled; the portal-settable config | `src/MeshWeaver.Speech/SpeechConfiguration.cs` | done |
| **`ISpeechTranscriber` / `WhisperContainerTranscriber`** — the centralized client; `POST /inference` on the HTTP `IIoPool`, cold `IObservable` | `src/MeshWeaver.Speech/` | done + **4 unit tests** against the real `/inference` contract |
| **Portal endpoint** — `POST /api/speech/transcribe` the clients call | portal host | next |
| **Mic UI** — a record button in `ThreadChatView` (browser `MediaRecorder`) → transcript into the message box | `MeshWeaver.Blazor.Portal/Chat` | next |
| **RN + MAUI** — record → same endpoint | `clients/react-native`, `memex/Memex.Client` | next |

## Why a container (vs. on-device or a cloud STT)

- **On-device** (the MAUI path) is great for the phone but impossible in a browser and wasteful to replicate
  everywhere; the model is 547 MB per client.
- **A public cloud STT** (Azure/OpenAI) does not do real Swiss-German dialect — it maps to `de-CH` Standard
  German at best. Our fine-tune (trained partly on Bernese) is the reason this works at all, so we must host
  **our** model.
- **One container** keeps the model, the GPU, and the config in a single place: `de` in, Standard German out;
  swap the model file to trade accuracy for size; add CUDA/Vulkan for throughput without touching clients.

## Config — the async + mutation rules

- The transcriber reads `SpeechConfiguration` **live** (`IOptionsMonitor` via a delegate), so the portal can
  repoint the endpoint at runtime without recreating the service.
- The HTTP round-trip runs on the **HTTP `IIoPool`** (`pool.Invoke(ct => …)`), never on a hub/circuit thread
  — see [Controlled I/O pooling](/Doc/Architecture/ControlledIoPooling). The public surface is a cold
  `IObservable<SpeechTranscript>`; nothing happens until subscribe. No `async`/`await` leaks onto a hub.

## Security

Audio is posted to the portal endpoint under the caller's session; the portal forwards it to the (typically
cluster-internal) Whisper container. The container endpoint is **not** exposed to clients directly — they only
ever see `/api/speech/transcribe`, so the model host stays behind the portal's auth.

## Status

**Done + tested here:** the container definition (`deploy/whisper`), `MeshWeaver.Speech` (config + transcriber),
and 4 unit tests that drive `WhisperContainerTranscriber` against an in-process server mimicking whisper.cpp's
`/inference` (transcribe, language forward + per-call override, unconfigured error, server-error propagation).

**Next:** the portal `POST /api/speech/transcribe` endpoint, the `ThreadChatView` mic button, and the RN/MAUI
record paths — then an end-to-end run against a live container (the one thing that needs Docker + the model).
