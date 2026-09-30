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
# 1. Build. The DEFAULT needs nothing in ./models/: the build downloads the Apache-2.0
#    Flix-AI/flix-swissgerman-full model from a PINNED Hugging Face revision, converts it to GGML and
#    quantizes it to q8_0 (~1.7 GB), then transcribes a sample with it. No credential, no SAS.
docker compose up --build -d
#    (CI does the same and pushes meshweaver.azurecr.io/whisper-swiss-german:1.7.4-flix-large-v3-q8-<sha>
#     on every main change to this Dockerfile — .github/workflows/whisper-image.yml.)
# 2. Smoke-test with any WAV (16 kHz mono is ideal):
curl -F file=@sample.wav -F language=de -F response_format=json http://localhost:8080/inference
#   → {"text":"..."}
```

To bake a different GGML file instead — e.g. the NON-commercial Flurin17 derivative — put it at
`models/model.bin` and build with `--build-arg MODEL_SOURCE=context` (see "Model licence" first).

Then point the portal at it (appsettings or the portal's Speech settings):

```jsonc
"Speech": { "Endpoint": "http://localhost:8080", "Enabled": true, "Language": "de" }
```

## Model licence

**Default: [Flix-AI/flix-swissgerman-full](https://huggingface.co/Flix-AI/flix-swissgerman-full) —
Apache-2.0**, a whisper-large-v3 fine-tune (Swiss German transcribes to Standard German; plain German and
English verified unregressed against the base, A/B 2026-08-20). It may be served commercially, and it is
what memex-cloud runs. The build pins its Hugging Face revision and the sha256 of every input, and converts
it with whisper.cpp's own `convert-h5-to-ggml.py` + `quantize … q8_0`.

**Opt-in, NON-commercial only: the Flurin17 derivative** (`ggml-swiss-german-turbo-q5_0.bin`, private
Plugins release `voice-model-swiss-german`) — a derivative of
[Flurin17/whisper-large-v3-turbo-swiss-german](https://huggingface.co/Flurin17/whisper-large-v3-turbo-swiss-german),
**CC BY-NC 4.0**. (The base `openai/whisper-large-v3-turbo` is MIT, but a derivative cannot be more
permissive than what it derives from, so NC is the binding term.) 🚨 **Never build it for a commercial
estate.** It is built only with `MODEL_SOURCE=context`, never by CI, and is never republished for
anonymous download; see [Voice model distribution](../../src/MeshWeaver.Documentation/Data/Architecture/VoiceModelDistribution.md).

## Notes

- **Model is BAKED, not mounted** — the image carries `/models/model.bin`, and the image registry the
  cluster already authenticates to is what distributes it. That is deliberate and it is the fix for
  [#3906](https://github.com/Systemorph/MeshWeaver/issues/3906): the chart used to fetch the model with an
  anonymous `curl` from a GitHub release on the core repo,
  [#2593](https://github.com/Systemorph/MeshWeaver/issues/2593) moved that asset to the private
  MeshWeaver.Plugins repo without moving the pin, and every pod then died in `Init:Error` on a 404. For the
  NC model, republishing it for anonymous download is not available as a fix — see
  [Voice model distribution](../../src/MeshWeaver.Documentation/Data/Architecture/VoiceModelDistribution.md)
  for that decision and the two rejected alternatives; the default Apache-2.0 model is baked the same way so
  that one chart shape serves both. The build prints the size and sha256 of what it baked.
  🚨 Never bind-mount a volume over `/models` — an empty one shadows the baked model.
- **Swapping the model** — either change the pinned revision + digests in the Dockerfile (a new image
  LINE — rename `LINE` in `whisper-image.yml` with it), or put a GGML file at `models/model.bin` and build with
  `MODEL_SOURCE=context`. The build asserts only that the file is big enough to be a model at all.
- **Language `de`** transcribes Swiss German *out as Standard German* (the model was trained that way);
  `auto` detects mixed de/fr/it at some dialect-accuracy cost.
- **GPU**: this Dockerfile is CPU (portable). For throughput, build whisper.cpp with CUDA/Vulkan and add the
  device to the compose service — the `/inference` contract is unchanged.
- **Not runtime-verified in this repo's CI** — Docker isn't available in the build sandbox. The `MeshWeaver.Speech`
  client that calls this endpoint *is* unit-tested against the exact `/inference` contract
  (`test/MeshWeaver.Speech.Test`). Verify the container end-to-end on a machine with Docker + the model.
