#!/bin/sh
# All-local run on a Mac: whisper.cpp STT + Ollama brain + macOS `say` TTS.
# Prereqs: brew install whisper-cpp ollama; the Swiss German GGML in ~/models/whisper/
# (voice-model-swiss-german release on the PRIVATE MeshWeaver.Plugins repo — gh auth); an Ollama model pulled (default qwen3.6).
# Required env: SATELLITE_HOST, SATELLITE_PSK, GATEWAY_HOST (this Mac's LAN IP).
set -e

WHISPER_MODEL="${WHISPER_MODEL:-$HOME/models/whisper/ggml-swiss-german-turbo-q5_0.bin}"
WHISPER_PORT="${WHISPER_PORT:-8090}"

if ! curl -sf "http://127.0.0.1:${WHISPER_PORT}/" >/dev/null 2>&1; then
    echo "Starting whisper-server on :${WHISPER_PORT} (${WHISPER_MODEL})"
    whisper-server -m "$WHISPER_MODEL" --port "$WHISPER_PORT" --host 127.0.0.1 &
    sleep 2
fi

export BRAIN="${BRAIN:-ollama}"
export OLLAMA_MODEL="${OLLAMA_MODEL:-qwen3.6}"
export STT_URL="${STT_URL:-http://127.0.0.1:${WHISPER_PORT}/inference}"
export TTS_ENGINE="${TTS_ENGINE:-say}"
export SAY_VOICE="${SAY_VOICE:-Anna}"

exec python -m memex_voice_gateway
