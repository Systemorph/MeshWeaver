# The centralized Swiss-German speech-to-text container: a whisper.cpp HTTP server bound to the fine-tuned
# Swiss-German model. This replaces the on-device Whisper.net per-client model (memex/Memex.Client) with ONE
# hosted endpoint the portal, React Native, and MAUI all call. Build stage compiles `whisper-server`; the
# runtime stage BAKES the model in at /models/model.bin.
#
# 🚨 The model is baked, NOT fetched at run time — and that is the whole point. It is a CC BY-NC-4.0
# derivative published on a PRIVATE release, so no URL exists that a cluster can fetch it from without a
# credential, and a credential that can read that repo can read the entire private fleet source. The image
# registry the cluster ALREADY authenticates to in order to pull THIS image is the one distribution channel
# that needs nothing new. Full reasoning, and the two rejected alternatives:
# src/MeshWeaver.Documentation/Data/Architecture/VoiceModelDistribution.md (/Doc/Architecture/VoiceModelDistribution).

FROM ubuntu:24.04 AS build
RUN apt-get update && apt-get install -y --no-install-recommends \
        build-essential cmake git ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /src
# Pin to a release tag for reproducibility; bump deliberately.
RUN git clone --depth 1 --branch v1.7.4 https://github.com/ggml-org/whisper.cpp
WORKDIR /src/whisper.cpp
# Examples (incl. the HTTP `whisper-server`) build by default; Release for speed.
# BUILD_SHARED_LIBS=OFF: the runtime stage copies ONE binary, so it must be self-contained —
# the default shared build ships libwhisper.so.1 the binary can't find at run time
# (first real deploy died in CrashLoopBackOff on exactly that; this stage is not CI-verified).
RUN cmake -B build -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF \
    && cmake --build build -j --config Release --target whisper-server

FROM ubuntu:24.04
RUN apt-get update && apt-get install -y --no-install-recommends libgomp1 ca-certificates curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /src/whisper.cpp/build/bin/whisper-server /usr/local/bin/whisper-server
# Fail the BUILD, not the first pod, if the binary still needs anything beyond system libs.
RUN whisper-server --help >/dev/null 2>&1 || { ldd /usr/local/bin/whisper-server; exit 1; }

# ── The model ────────────────────────────────────────────────────────────────────────────────────────
# Place the file in the build context FIRST — it is gitignored (deploy/whisper/models/), so it is never a
# tracked blob and GitHub's 100 MB limit never applies:
#
#   gh release download voice-model-swiss-german --repo Systemorph/MeshWeaver.Plugins \
#     --pattern 'ggml-swiss-german-turbo-q5_0.bin' --output deploy/whisper/models/model.bin
#
# (PRIVATE repo — needs `gh auth`. 574,041,195 bytes,
#  sha256 2d56e773724a247360067b527417842b81d25ff891fed014341a6844f15ea612.)
#
# If it is absent, this COPY fails the BUILD — deliberately, with the file named, on the machine of the
# person who can fix it, instead of as a pod that cannot start in a cluster somebody else operates. Last
# among the COPYs so it never invalidates the (expensive) whisper-server build layers above it.
COPY models/model.bin /models/model.bin

# A COPY only proves a file arrived, not that it is a MODEL. An interrupted 547 MB download, or an HTML
# error page saved under the model's name, is a few KB and would otherwise surface much later as
# "failed to load model" inside a pod. The floor sits far above any error page and well below the
# smallest real GGML whisper model (ggml-tiny is ~75 MB), so it rejects rubbish without blocking a swap.
# The digest is PRINTED rather than asserted — asserting it would break the documented model swap, while
# printing puts the one value a reader needs to compare against the release into the build log.
RUN set -e; \
    size=$(wc -c < /models/model.bin); \
    if [ "$size" -lt 10000000 ]; then \
      echo "FATAL: /models/model.bin is $size bytes — that is not a GGML whisper model." >&2; \
      echo "Re-fetch it into the build context: gh release download voice-model-swiss-german \
--repo Systemorph/MeshWeaver.Plugins --pattern 'ggml-swiss-german-turbo-q5_0.bin' --output models/model.bin" >&2; \
      exit 1; \
    fi; \
    echo "Model baked: $size bytes, sha256 $(sha256sum /models/model.bin | cut -d' ' -f1)"

# Language + threads are configurable at run time. WHISPER_MODEL points at the baked file; override it only
# to load a model mounted from elsewhere (a swap), which is the ONLY case that needs a /models volume.
ENV WHISPER_MODEL=/models/model.bin \
    WHISPER_LANGUAGE=de \
    WHISPER_THREADS=4
EXPOSE 8080

# whisper.cpp server exposes POST /inference (multipart: file, language, response_format) — the exact
# contract WhisperContainerTranscriber speaks. --convert lets it accept common audio (wav/webm/ogg) via ffmpeg
# when present; WAV/PCM always works without it.
CMD ["/bin/sh", "-c", "whisper-server -m \"$WHISPER_MODEL\" -l \"$WHISPER_LANGUAGE\" -t \"$WHISPER_THREADS\" --host 0.0.0.0 --port 8080"]
