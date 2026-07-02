# The centralized Swiss-German speech-to-text container: a whisper.cpp HTTP server bound to the fine-tuned
# Swiss-German model. This replaces the on-device Whisper.net per-client model (memex/Memex.Client) with ONE
# hosted endpoint the portal, React Native, and MAUI all call. Build stage compiles `whisper-server`; the
# runtime image is small (no model baked — the model is mounted at /models/model.bin, see docker-compose.yml
# + README) so the image stays language-agnostic and the (large) model is swappable without a rebuild.

FROM ubuntu:24.04 AS build
RUN apt-get update && apt-get install -y --no-install-recommends \
        build-essential cmake git ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /src
# Pin to a release tag for reproducibility; bump deliberately.
RUN git clone --depth 1 --branch v1.7.4 https://github.com/ggml-org/whisper.cpp
WORKDIR /src/whisper.cpp
# Examples (incl. the HTTP `whisper-server`) build by default; Release for speed.
RUN cmake -B build -DCMAKE_BUILD_TYPE=Release && cmake --build build -j --config Release --target whisper-server

FROM ubuntu:24.04
RUN apt-get update && apt-get install -y --no-install-recommends libgomp1 ca-certificates curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /src/whisper.cpp/build/bin/whisper-server /usr/local/bin/whisper-server

# Model + language are configurable at run time (the "local model running as container" the portal points at).
ENV WHISPER_MODEL=/models/model.bin \
    WHISPER_LANGUAGE=de \
    WHISPER_THREADS=4
EXPOSE 8080

# whisper.cpp server exposes POST /inference (multipart: file, language, response_format) — the exact
# contract WhisperContainerTranscriber speaks. --convert lets it accept common audio (wav/webm/ogg) via ffmpeg
# when present; WAV/PCM always works without it.
CMD ["/bin/sh", "-c", "whisper-server -m \"$WHISPER_MODEL\" -l \"$WHISPER_LANGUAGE\" -t \"$WHISPER_THREADS\" --host 0.0.0.0 --port 8080"]
