# The centralized Swiss-German speech-to-text container: a whisper.cpp HTTP server bound to a fine-tuned
# Swiss-German model. This replaces the on-device Whisper.net per-client model (memex/Memex.Client) with ONE
# hosted endpoint the portal, React Native, and MAUI all call. The build stage compiles `whisper-server`; the
# runtime stage BAKES the model in at /models/model.bin.
#
# ── Which model, and why the default is the one it is ─────────────────────────────────────────────────
# DEFAULT (MODEL_SOURCE=flix) — Flix-AI/flix-swissgerman-full, whisper-large-v3 fine-tune, APACHE-2.0,
#   converted to GGML and quantized to q8_0 INSIDE this build from a PINNED Hugging Face revision. It is the
#   model a COMMERCIAL deployment may serve (memex-cloud runs it; A/B 2026-08-20: Swiss German → Standard
#   German, plain German + English unregressed against the base). No credential, no SAS, no private
#   download: the inputs are public and pinned by revision + sha256, so any builder — CI included —
#   produces the image from this file alone.
# MODEL_SOURCE=context — whatever GGML file you put at models/model.bin in the build context. That is how the
#   CC BY-NC-4.0 Flurin17 derivative (`ggml-swiss-german-turbo-q5_0.bin`, private Plugins release) is
#   built for NON-commercial use. 🚨 It is BARRED from commercial serving — never build it for a
#   commercial estate. /Doc/Architecture/VoiceModelDistribution has the licence reasoning.
#
#   docker build deploy/whisper                                   # the Apache-2.0 default
#   docker build --build-arg MODEL_SOURCE=context deploy/whisper  # models/model.bin from the context
#
# The model is BAKED, never fetched at run time: the registry the cluster already authenticates to for the
# image is the one distribution channel that needs nothing new (#3906; no initContainer, no model URL).

ARG MODEL_SOURCE=flix

FROM ubuntu:24.04 AS build
RUN apt-get update && apt-get install -y --no-install-recommends \
        build-essential cmake git ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /src
# Pin to a release tag for reproducibility; bump deliberately.
RUN git clone --depth 1 --branch v1.7.4 https://github.com/ggml-org/whisper.cpp
WORKDIR /src/whisper.cpp
# BUILD_SHARED_LIBS=OFF: the runtime stage copies ONE binary, so it must be self-contained —
# the default shared build ships libwhisper.so.1 the binary can't find at run time
# (first real deploy died in CrashLoopBackOff on exactly that).
# GGML_NATIVE=OFF: the default compiles for the BUILD machine's CPU (-march=native). A CI runner with
# AVX-512 would then emit instructions a cluster node without it dies on (SIGILL). OFF builds the portable
# AVX/AVX2/FMA/F16C set every current x86-64 node has.
# quantize + whisper-cli are only for the model stage (convert → q8_0 → smoke test); the runtime ships
# whisper-server alone.
RUN cmake -B build -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DGGML_NATIVE=OFF \
    && cmake --build build -j --config Release --target whisper-server quantize whisper-cli

# ── MODEL_SOURCE=flix: convert the Apache-2.0 fine-tune from its pinned Hugging Face revision ───────────
FROM ubuntu:24.04 AS model-flix
RUN apt-get update && apt-get install -y --no-install-recommends \
        python3 python3-venv ca-certificates curl libgomp1 && rm -rf /var/lib/apt/lists/*
# CPU torch + a transformers 4.x that loads the (bf16) safetensors as float32 by default — the converter
# calls .numpy(), which bf16 tensors refuse.
RUN python3 -m venv /venv \
    && /venv/bin/pip install --no-cache-dir --index-url https://download.pytorch.org/whl/cpu torch==2.5.1 \
    && /venv/bin/pip install --no-cache-dir transformers==4.46.3 safetensors==0.4.5 numpy==2.1.3
COPY --from=build /src/whisper.cpp/models/convert-h5-to-ggml.py /tools/convert-h5-to-ggml.py
COPY --from=build /src/whisper.cpp/build/bin/quantize /src/whisper.cpp/build/bin/whisper-cli /tools/
COPY --from=build /src/whisper.cpp/samples/jfk.wav /tools/jfk.wav
# The pins. Change the revision and every digest together, deliberately — a model swap is a new image line.
ARG HF_REPO=Flix-AI/flix-swissgerman-full
ARG HF_REVISION=a9c347a904af5d31b28c537ac732e0618aed6136
ARG SHA_SAFETENSORS=cd5bf4fdfd7fbcabed5bc52c44954f1ce28ff339e53f1f595dc06bf7f6131de6
ARG SHA_CONFIG=f9a221a42ec00fe173611abc8e0ae307001fa839bd8377b820728bd5266617b1
ARG SHA_GENERATION_CONFIG=dcfa6777587933df667a5075076cec8eaf44db14f86ef805b40296416a1048f3
ARG SHA_TOKENIZER=2e8603547facf4bb2115da666fa22d96f637a01b92b8cbcdb699d8ed4fa0efb6
# The mel filterbank the converter embeds (large-v3 = 128 bins), from openai/whisper at a tag.
ARG WHISPER_PY_TAG=v20240930
ARG SHA_MEL=7450ae70723a5ef9d341e3cee628c7cb0177f36ce42c44b7ed2bf3325f0f6d4c
# ONE layer on purpose: the 3.1 GB safetensors and the 3.1 GB f16 intermediate are deleted before the
# layer is committed, so only the ~1.7 GB q8_0 file survives into the build cache.
RUN set -eu; \
    mkdir -p /hf /whisper/whisper/assets /work /out; \
    get() { curl -fsSL --retry 5 --retry-delay 10 -o "$2" "$1"; echo "$3  $2" | sha256sum -c -; }; \
    base="https://huggingface.co/${HF_REPO}/resolve/${HF_REVISION}"; \
    get "$base/model.safetensors"      /hf/model.safetensors      "$SHA_SAFETENSORS"; \
    get "$base/config.json"            /hf/config.json            "$SHA_CONFIG"; \
    get "$base/generation_config.json" /hf/generation_config.json "$SHA_GENERATION_CONFIG"; \
    get "$base/tokenizer.json"         /hf/tokenizer.json         "$SHA_TOKENIZER"; \
    get "https://raw.githubusercontent.com/openai/whisper/${WHISPER_PY_TAG}/whisper/assets/mel_filters.npz" \
        /whisper/whisper/assets/mel_filters.npz "$SHA_MEL"; \
    # The converter reads vocab.json + added_tokens.json; the repo ships only tokenizer.json. Derive both
    # from the model's OWN tokenizer (measured identical to openai/whisper-large-v3's, 50257 + 1609).
    /venv/bin/python3 -c 'import json; t=json.load(open("/hf/tokenizer.json")); json.dump(t["model"]["vocab"], open("/hf/vocab.json","w"), ensure_ascii=False); json.dump({a["content"]: a["id"] for a in t["added_tokens"]}, open("/hf/added_tokens.json","w"), ensure_ascii=False); print("vocab", len(t["model"]["vocab"]), "added", len(t["added_tokens"]))'; \
    /venv/bin/python3 /tools/convert-h5-to-ggml.py /hf /whisper /work > /work/convert.log; \
    tail -n 3 /work/convert.log; \
    rm -rf /hf; \
    /tools/quantize /work/ggml-model.bin /out/model.bin q8_0; \
    rm -rf /work; \
    # Smoke test: the model LOADS and TRANSCRIBES. A converter/quantizer mismatch fails here, in the build,
    # not as a pod that never gets Ready. The transcript is printed, not asserted (the fine-tune may
    # render English oddly); it must merely be non-empty.
    text=$(/tools/whisper-cli -m /out/model.bin -f /tools/jfk.wav -l en -t "$(nproc)" -nt 2>/dev/null | tr -s ' \n' ' '); \
    echo "smoke transcript: $text"; \
    [ -n "$(echo "$text" | tr -d ' ')" ] || { echo "FATAL: the converted model transcribed nothing" >&2; exit 1; }

# ── MODEL_SOURCE=context: a GGML file you placed at models/model.bin (gitignored) ─────────────────────
FROM scratch AS model-context
COPY models/model.bin /out/model.bin

FROM model-${MODEL_SOURCE} AS model

FROM ubuntu:24.04
RUN apt-get update && apt-get install -y --no-install-recommends libgomp1 ca-certificates curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /src/whisper.cpp/build/bin/whisper-server /usr/local/bin/whisper-server
# Fail the BUILD, not the first pod, if the binary still needs anything beyond system libs.
RUN whisper-server --help >/dev/null 2>&1 || { ldd /usr/local/bin/whisper-server; exit 1; }

# Last among the COPYs so a model change never invalidates the (expensive) whisper-server layers above.
COPY --from=model /out/model.bin /models/model.bin

# A COPY only proves a file arrived, not that it is a MODEL. An interrupted download, or an HTML error page
# saved under the model's name, is a few KB and would otherwise surface much later as "failed to load model"
# inside a pod. The floor sits far above any error page and well below the smallest real GGML whisper model
# (ggml-tiny is ~75 MB). The digest is PRINTED rather than asserted, so the build log carries the value to
# compare against.
RUN set -e; \
    size=$(wc -c < /models/model.bin); \
    if [ "$size" -lt 10000000 ]; then \
      echo "FATAL: /models/model.bin is $size bytes — that is not a GGML whisper model." >&2; \
      echo "Default build: rebuild without MODEL_SOURCE. MODEL_SOURCE=context: place a real GGML file at models/model.bin." >&2; \
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
# contract WhisperContainerTranscriber speaks. WAV/PCM always works.
CMD ["/bin/sh", "-c", "whisper-server -m \"$WHISPER_MODEL\" -l \"$WHISPER_LANGUAGE\" -t \"$WHISPER_THREADS\" --host 0.0.0.0 --port 8080"]
