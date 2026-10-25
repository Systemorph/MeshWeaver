#!/usr/bin/env bash
# entrypoint.sh — the image's CMD. Starts `ollama serve`, then LOADS every model in $OLLAMA_PRELOAD into
# memory (keep_alive -1) before it declares the pod ready by writing /tmp/ready, which the chart's readiness
# probe reads. Without the preload the first caller after every start would pay the model load (~1–3 min
# for a 23 GB model from disk) inside its own request deadline.
#
# A model that fails to load leaves the pod NOT READY with the reason in the log — never a ready pod that
# answers 500 to its first real caller.
set -uo pipefail

rm -f /tmp/ready
ollama serve &
server=$!
trap 'kill -TERM "$server" 2>/dev/null; wait "$server"; exit $?' TERM INT

base="http://127.0.0.1:${OLLAMA_HOST##*:}"
for _ in $(seq 1 120); do curl -fsS "$base/api/version" >/dev/null 2>&1 && break; sleep 1; done

ok=1
for model in ${OLLAMA_PRELOAD:-}; do
  start=$(date +%s)
  # /api/show tells an embedder from a generator; each is loaded through its own endpoint.
  if curl -fsS --max-time 30 -d "{\"model\":\"$model\"}" "$base/api/show" | grep -q '"embedding"'; then
    body="{\"model\":\"$model\",\"input\":\"warm\",\"keep_alive\":-1}"; path=/api/embed
  else
    body="{\"model\":\"$model\",\"keep_alive\":-1}"; path=/api/generate
  fi
  if out=$(curl -fsS --max-time 900 -d "$body" "$base$path" 2>&1); then
    echo "[preload] $model loaded in $(( $(date +%s) - start ))s"
  else
    echo "[preload] FAILED to load $model after $(( $(date +%s) - start ))s: ${out:0:400}" >&2
    ok=0
  fi
done
if [ "$ok" -eq 1 ]; then
  touch /tmp/ready
  echo "[preload] ready: ${OLLAMA_PRELOAD:-<nothing preloaded>}"
else
  echo "[preload] NOT ready — see the failure above; the pod stays out of rotation" >&2
fi
wait "$server"
