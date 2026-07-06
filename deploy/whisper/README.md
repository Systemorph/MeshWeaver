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
# 1. Fetch the Swiss-German model (~547 MB) into ./models/ (same file the on-device client downloads):
mkdir -p models
curl -fSL https://github.com/Systemorph/MeshWeaver/releases/download/voice-model-swiss-german/ggml-swiss-german-turbo-q5_0.bin \
     -o models/model.bin
# 2. Build + start the container:
docker compose up --build -d
# 3. Smoke-test with any WAV (16 kHz mono is ideal):
curl -F file=@sample.wav -F language=de -F response_format=json http://localhost:8080/inference
#   → {"text":"..."}
```

Then point the portal at it (appsettings or the portal's Speech settings):

```jsonc
"Speech": { "Endpoint": "http://localhost:8080", "Enabled": true, "Language": "de" }
```

## Notes

- **Model is mounted, not baked** — the image stays small and the (large, swappable) model lives in
  `./models/`. Swap in `ggml-base.bin` / `ggml-large-v3-turbo.bin` to trade Swiss-German accuracy for size.
- **Language `de`** transcribes Swiss German *out as Standard German* (the model was trained that way);
  `auto` detects mixed de/fr/it at some dialect-accuracy cost.
- **GPU**: this Dockerfile is CPU (portable). For throughput, build whisper.cpp with CUDA/Vulkan and add the
  device to the compose service — the `/inference` contract is unchanged.
- **Not runtime-verified in this repo's CI** — Docker isn't available in the build sandbox. The `MeshWeaver.Speech`
  client that calls this endpoint *is* unit-tested against the exact `/inference` contract
  (`test/MeshWeaver.Speech.Test`). Verify the container end-to-end on a machine with Docker + the model.
