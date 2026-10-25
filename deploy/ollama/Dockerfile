# ollama — the fleet's self-hosted model service: Ollama (CPU) with its models BAKED IN, cluster-internal,
# OpenAI-compatible at /v1. One pod serves every "local AI" job the portal has:
#
#   qwen3.6   (qwen3.6:35b-a3b, Q4_K_M + vision projector)  image recognition / OCR, translation, summary
#   bge-m3    (bge-m3:567m, 1024-dim)                        vector embeddings
#
# The weights are pulled at BUILD time from registry.ollama.ai and checked against the manifest digests
# pinned below, then never fetched again: the registry the cluster already pulls its images from is the
# one distribution channel (the deploy/whisper rule, #3906). The pod has no egress at all.
#
#   docker build -t ollama:dev deploy/ollama
#   docker build -t ollama:small --build-arg VISION_MODEL=qwen3.5:0.8b --build-arg VISION_MANIFEST_SHA256= \
#       deploy/ollama        # a laptop-sized variant to try the mechanics; an EMPTY sha skips the pin check
#
# The registry image is built by CI (.github/workflows/ollama-image.yml), never by hand. The full design is
# Doc/Architecture/LocalModelService.

# ── The engine: the upstream Ollama build, CUDA/ROCm runners removed (the fleet's pools are CPU) ─────────
# Pinned by digest; a bump is a new image LINE (LINE in ollama-image.yml + .github/release-channels.json).
FROM ollama/ollama:0.33.2@sha256:020e4134285e2ef4d8fd801234176de3b4faadc992a3eb06c8e66a2f9d4c4ba2 AS engine
RUN rm -rf /usr/lib/ollama/cuda_* /usr/lib/ollama/rocm* /usr/lib/ollama/vulkan* \
    && /usr/bin/ollama --version || true

# ── The image that ships: same Ubuntu as upstream, the binary + CPU libs, the models in one layer ────────
FROM ubuntu:24.04@sha256:68ee22004a9109b44973313b02ff7b1de250f1de0f5ecb628d73f579aaff33ac
ENV DEBIAN_FRONTEND=noninteractive
# curl + jq are for the probes and the smoke (jq DECODES the JSON answers — a grep would stop at the first
# escaped quote); ca-certificates for the build-time pull.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl jq \
    && rm -rf /var/lib/apt/lists/* /usr/share/doc /usr/share/man
COPY --from=engine /usr/bin/ollama /usr/bin/ollama
COPY --from=engine /usr/lib/ollama /usr/lib/ollama
COPY app /app
COPY fixtures /fixtures
RUN chmod 0755 /app/*.sh

# 🚨 THE PINS. The model TAG says what to pull; the manifest SHA256 says which bytes that tag must resolve to
# today (Ollama re-points tags). A changed upstream tag fails the build here, naming both digests — it never
# silently becomes a different model. Change a tag and its sha together, deliberately: that is a new LINE.
# The ALIAS is the name the portal configures (`qwen3.6`, `bge-m3` — the same names a Mac's Ollama answers
# to, so one config works on a laptop and in the cluster).
ARG VISION_MODEL=qwen3.6:35b-a3b
ARG VISION_MANIFEST_SHA256=a7eb95c53bcf96b4bdd008d0fab4a5dac88047d9c1a7a9ab88ed453423fbd87c
ARG VISION_ALIAS=qwen3.6
ARG EMBED_MODEL=bge-m3:567m
ARG EMBED_MANIFEST_SHA256=7907646426070047a77226ac3e684fbbe8410524f7b4a74d02837e43f2146bab
ARG EMBED_ALIAS=bge-m3
ARG EMBED_DIMENSIONS=1024
ENV OLLAMA_MODELS=/models
# ONE layer: pull → verify digests → alias → capability + embedding smoke (with its negative control) →
# stop the server. Only the model blobs survive into the image (~23.8 GB for the defaults).
RUN /app/bake.sh

# Runtime. Non-root, read-only root filesystem in the chart; Ollama writes only $HOME (its key) and /tmp.
#   OLLAMA_NOPRUNE         never touch /models at start (it is read-only and baked)
#   OLLAMA_CONTEXT_LENGTH  bounded: Ollama's default for qwen3.6 is its 262,144-token maximum, whose KV
#                          cache alone does not fit a CPU node — and a request may still ask for less
#   OLLAMA_KEEP_ALIVE=-1   keep both models resident; a 23 GB reload per request is the cost to avoid
#   OLLAMA_NUM_PARALLEL=1  one generation per model at a time; the rest queue (OLLAMA_MAX_QUEUE)
RUN useradd --uid 10001 --no-create-home ollama
USER 10001
ENV HOME=/tmp \
    OLLAMA_HOST=0.0.0.0:11434 \
    OLLAMA_NOPRUNE=1 \
    OLLAMA_CONTEXT_LENGTH=8192 \
    OLLAMA_KEEP_ALIVE=-1 \
    OLLAMA_MAX_LOADED_MODELS=2 \
    OLLAMA_NUM_PARALLEL=1 \
    OLLAMA_MAX_QUEUE=64 \
    OLLAMA_PRELOAD="${VISION_ALIAS} ${EMBED_ALIAS}" \
    MW_VISION_MODEL="${VISION_ALIAS}" \
    MW_EMBED_MODEL="${EMBED_ALIAS}" \
    MW_EMBED_DIMENSIONS="${EMBED_DIMENSIONS}"
EXPOSE 11434
CMD ["/app/entrypoint.sh"]
