#!/bin/sh
set -e

# Fetch the Piper voice on first start (≈60 MB, kept in the /voices volume).
# High German out: the Swiss German Whisper fine-tune transcribes dialect INTO Standard
# German, and the assistant answers in Standard German.
VOICE="${PIPER_VOICE:-/voices/de_DE-thorsten-medium.onnx}"
VOICE_URL="${PIPER_VOICE_URL:-https://huggingface.co/rhasspy/piper-voices/resolve/main/de/de_DE/thorsten/medium/de_DE-thorsten-medium.onnx}"
if [ ! -f "$VOICE" ]; then
    mkdir -p "$(dirname "$VOICE")"
    echo "Fetching Piper voice → $VOICE"
    python -c "import urllib.request,sys; urllib.request.urlretrieve('$VOICE_URL', '$VOICE')"
    python -c "import urllib.request; urllib.request.urlretrieve('$VOICE_URL.json', '$VOICE.json')"
fi

exec memex-voice-gateway
