# memex-voice-gateway — ESPHome voice satellite ↔ MeshWeaver threads, no Home Assistant.
FROM python:3.12-slim

# piper-tts brings the `piper` CLI + onnxruntime; the voice model is fetched at runtime
# by the entrypoint (kept out of the image, same policy as deploy/whisper).
RUN pip install --no-cache-dir piper-tts

WORKDIR /app
COPY pyproject.toml ./
COPY memex_voice_gateway ./memex_voice_gateway
RUN pip install --no-cache-dir .

COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

# TTS file server the satellite fetches audio from — must be reachable on the LAN.
EXPOSE 8200

ENTRYPOINT ["/entrypoint.sh"]
