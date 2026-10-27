#!/usr/bin/env bash
# bake.sh — runs ONCE, in the image build (Dockerfile). Pulls the pinned models into $OLLAMA_MODELS, refuses
# any whose manifest is not the pinned bytes, names them by the aliases the portal configures, proves the
# capabilities and the embedder through the real HTTP contract (smoke.sh), and leaves only the model files.
set -euo pipefail

: "${OLLAMA_MODELS:?}" "${VISION_MODEL:?}" "${VISION_ALIAS:?}" "${EMBED_MODEL:?}" "${EMBED_ALIAS:?}" "${EMBED_DIMENSIONS:?}"
mkdir -p "$OLLAMA_MODELS"
export HOME=/tmp/bake-home OLLAMA_HOST=127.0.0.1:11434 OLLAMA_CONTEXT_LENGTH=2048
mkdir -p "$HOME"

ollama serve > /tmp/bake-serve.log 2>&1 &
server=$!
stop() { kill "$server" 2>/dev/null || true; wait "$server" 2>/dev/null || true; }
trap 'stop; echo "--- ollama serve log (tail) ---"; tail -n 40 /tmp/bake-serve.log' ERR
for _ in $(seq 1 60); do curl -fsS http://127.0.0.1:11434/api/version >/dev/null 2>&1 && break; sleep 1; done
echo "engine: $(curl -fsS http://127.0.0.1:11434/api/version)"

# manifest_path <name:tag> — where Ollama stores the manifest it pulled, verbatim (library models only).
manifest_path() { local name="${1%%:*}" tag="${1#*:}"; [ "$tag" = "$1" ] && tag=latest
                  echo "$OLLAMA_MODELS/manifests/registry.ollama.ai/library/$name/$tag"; }

pull_pinned() { # pull_pinned <name:tag> <sha256 or empty> <alias>
  local model="$1" want="$2" alias="$3" got
  echo "pulling $model"
  ollama pull "$model" 2>&1 | tail -n 2
  got="$(sha256sum "$(manifest_path "$model")" | cut -d' ' -f1)"
  if [ -z "$want" ]; then
    echo "WARNING: $model is UNPINNED (manifest sha256 $got) — a laptop variant, never what CI ships"
  elif [ "$got" != "$want" ]; then
    echo "FATAL: $model resolved to manifest sha256 $got, the pin is $want." >&2
    echo "The upstream tag moved. Re-pin deliberately (tag + sha + a new LINE), never by editing the sha alone." >&2
    exit 1
  else
    echo "pinned: $model = manifest sha256 $got"
  fi
  [ "$alias" = "$model" ] || ollama cp "$model" "$alias"
}

pull_pinned "$VISION_MODEL" "${VISION_MANIFEST_SHA256:-}" "$VISION_ALIAS"
pull_pinned "$EMBED_MODEL" "${EMBED_MANIFEST_SHA256:-}" "$EMBED_ALIAS"
ollama list

# The capabilities and the embedder, through the same contract the portal calls. The vision model is NOT
# run here: on a CPU build host its ~26 GB working set is the job of the in-cluster helm test.
MW_VISION_MODEL="$VISION_ALIAS" MW_EMBED_MODEL="$EMBED_ALIAS" MW_EMBED_DIMENSIONS="$EMBED_DIMENSIONS" \
  /app/smoke.sh http://127.0.0.1:11434

stop
trap - ERR
# The runtime user (uid 10001) reads the models; nothing writes them again (read-only root, OLLAMA_NOPRUNE).
chmod -R a+rX "$OLLAMA_MODELS"
rm -rf /tmp/bake-home /tmp/bake-serve.log
du -sh "$OLLAMA_MODELS"
