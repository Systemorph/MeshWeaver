# Centralized Swiss-German Whisper container

The speech-to-text model, hosted **once** as a container instead of on every device. It runs the same
fine-tuned Swiss-German Whisper model the MAUI client used on-device
([OnDeviceVoice](../../src/MeshWeaver.Documentation/Data/Architecture/OnDeviceVoice.md)) —
`ggml-swiss-german-turbo-q5_0.bin` — behind a `whisper.cpp` HTTP server, so the portal, React Native, and
MAUI all reach one endpoint. The mesh side is `MeshWeaver.Speech` (`WhisperContainerTranscriber` →
`POST /inference`); full design in
[CentralizedSpeech](../../src/MeshWeaver.Documentation/Data/Architecture/CentralizedSpeech.md).

## Run

```bash
cd deploy/whisper
# 1. Fetch the Swiss-German model (~547 MB) into ./models/ — a BUILD INPUT, baked into the image.
mkdir -p models
# Model on the PRIVATE MeshWeaver.Plugins repo (moved 2026-08-28) — requires gh auth.
gh release download voice-model-swiss-german --repo Systemorph/MeshWeaver.Plugins --pattern 'ggml-swiss-german-turbo-q5_0.bin' --output models/model.bin
# 2. Build + start the container (step 1 is not optional — the build fails naming the file without it):
docker compose up --build -d
# 3. Smoke-test with any WAV (16 kHz mono is ideal):
curl -F file=@sample.wav -F language=de -F response_format=json http://localhost:8080/inference
#   → {"text":"..."}
```

Then point the portal at it (appsettings or the portal's Speech settings):

```jsonc
"Speech": { "Endpoint": "http://localhost:8080", "Enabled": true, "Language": "de" }
```

## Model licence

The default model is a derivative of
[Flurin17/whisper-large-v3-turbo-swiss-german](https://huggingface.co/Flurin17/whisper-large-v3-turbo-swiss-german)
— **CC BY-NC 4.0, non-commercial use only**. (The base `openai/whisper-large-v3-turbo` is MIT, but a
derivative cannot be more permissive than what it derives from, so NC is the binding term.) That is why the
model is **never** republished for anonymous download and why the container gets it from an authenticated
channel; see [Voice model distribution](../../src/MeshWeaver.Documentation/Data/Architecture/VoiceModelDistribution.md).

For a commercial deployment, point this container at a permissively licensed model instead — put e.g.
`ggml-large-v3-turbo.bin` at `models/model.bin` and rebuild. You lose the Swiss-German dialect accuracy the
fine-tune exists for, and nothing else changes: the `/inference` contract is identical.

`clients/voice-gateway/README.md` has said the same thing since the gateway landed, and pointed here for the
detail — this section is that detail, which was missing.

## Notes

- **Model is BAKED, not mounted** — the image carries `/models/model.bin`, and the image registry the
  cluster already authenticates to is what distributes it. That is deliberate and it is the fix for
  [#3906](https://github.com/Systemorph/MeshWeaver/issues/3906): the chart used to fetch the model with an
  anonymous `curl` from a GitHub release on the core repo,
  [#2593](https://github.com/Systemorph/MeshWeaver/issues/2593) moved that asset to the private
  MeshWeaver.Plugins repo without moving the pin, and every pod then died in `Init:Error` on a 404. The
  model is a **CC BY-NC-4.0** derivative, so republishing it for anonymous download is not available as a
  fix — see [Voice model distribution](../../src/MeshWeaver.Documentation/Data/Architecture/VoiceModelDistribution.md)
  for that decision and the two rejected alternatives. Expected asset: 574,041,195 bytes,
  `sha256 2d56e773724a247360067b527417842b81d25ff891fed014341a6844f15ea612` (the build prints what it baked).
  🚨 Never bind-mount a volume over `/models` — an empty one shadows the baked model.
- **Swapping the model** — put a different GGML file at `models/model.bin` and rebuild (`ggml-base.bin` /
  `ggml-large-v3-turbo.bin` trade Swiss-German accuracy for size). The build asserts only that the file is
  big enough to be a model at all, so a swap needs no code change; the image tag is yours to choose.
- **Language `de`** transcribes Swiss German *out as Standard German* (the model was trained that way);
  `auto` detects mixed de/fr/it at some dialect-accuracy cost.
- **GPU**: this Dockerfile is CPU (portable). For throughput, build whisper.cpp with CUDA/Vulkan and add the
  device to the compose service — the `/inference` contract is unchanged.
- **Not runtime-verified in this repo's CI** — Docker isn't available in the build sandbox. The `MeshWeaver.Speech`
  client that calls this endpoint *is* unit-tested against the exact `/inference` contract
  (`test/MeshWeaver.Speech.Test`). Verify the container end-to-end on a machine with Docker + the model.
