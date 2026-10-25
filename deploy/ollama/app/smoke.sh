#!/usr/bin/env bash
# smoke.sh <base-url> [--vision]
#
# Calls a running ollama service THROUGH THE CONTRACT THE PORTAL USES (the OpenAI-compatible /v1 surface)
# and asserts what each job needs. It runs in three places: at image build (bake.sh, embeddings only), in
# ollama-image.yml against the image's own CMD from a sibling container (embeddings only — a hosted runner
# has 16 GB of RAM and the vision model needs ~26 GB), and in the cluster as the chart's `helm test` pod
# (--vision: everything, on the real node pool).
#
#   decoder      offline: the JSON decoder returns a content string with escaped quotes and newlines WHOLE
#   catalog      /v1/models lists the vision and the embedding model by the names the portal configures
#   capability   /api/show: the vision model reports vision+completion, the embedder reports embedding
#   embeddings   /v1/embeddings: every vector has MW_EMBED_DIMENSIONS dims, and a sentence lies closer to
#                its translation (de/en/id) than to an unrelated German sentence
#   --vision     /v1/chat/completions with the German+Indonesian fixture page as an image: six words,
#                the umlaut included; then a translation (id → en) and a summary
#
# Every answer is DECODED with jq (never grepped out of the raw JSON, where an escaped quote would end the
# string early). Every assertion carries a NEGATIVE CONTROL in the same run — the same check applied where
# it must fail (the English page, the untranslated source, a swapped pair) — so a check that cannot go red
# is itself red. Exit status = number of failed assertions.
set -uo pipefail

BASE="${1:?usage: smoke.sh <base-url> [--vision]}"
BASE="${BASE%/}"
VISION=0
[ "${2:-}" = "--vision" ] && VISION=1
VISION_MODEL="${MW_VISION_MODEL:-qwen3.6}"
EMBED_MODEL="${MW_EMBED_MODEL:-bge-m3}"
DIMS="${MW_EMBED_DIMENSIONS:-1024}"
FIXTURES="${MW_FIXTURES:-/fixtures}"
CHAT_TIMEOUT="${MW_CHAT_TIMEOUT:-900}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
failures=0

pass() { echo "PASS $1"; }
fail() { echo "FAIL $1${2:+ — $2}"; failures=$((failures + 1)); }

post() { # post <path> <json-file> <timeout> → body on stdout
  curl -sS --fail-with-body --max-time "$3" -H 'Content-Type: application/json' --data-binary "@$2" "$BASE$1"
}
content_of() { jq -r '.choices[0].message.content // empty' 2>/dev/null; } # stdin: a chat completion
has_words() { local text="$1"; shift; local w; for w in "$@"; do case "$text" in *"$w"*) ;; *) echo "missing '$w'"; return 1;; esac; done; }

# ── decoder (offline) ────────────────────────────────────────────────────────────────────────────────
# A transcript or translation routinely contains quotes and newlines; the decoder must return it whole.
sample='{"choices":[{"message":{"role":"assistant","content":"Er sagte: \"Das Gebäude ist versichert.\"\nZeile 2"}}]}'
decoded="$(printf '%s' "$sample" | content_of)"
expected="$(printf 'Er sagte: "Das Gebäude ist versichert."\nZeile 2')"
if [ "$decoded" = "$expected" ]; then pass "decoder returns escaped quotes and newlines whole"
else fail "decoder" "got: $decoded"; fi
# Negative control: the old grep-shaped extraction stops at the first escaped quote — it must NOT match.
grepped="$(printf '%s' "$sample" | grep -o '"content":"[^"]*' | sed 's/^"content":"//')"
[ "$grepped" = "$expected" ] && fail "decoder negative control" "a quote-stopping extraction matched the full content"

# ── catalog ──────────────────────────────────────────────────────────────────────────────────────────
curl -sS --max-time 30 "$BASE/v1/models" > "$WORK/models.json" 2>/dev/null || true
for m in "$VISION_MODEL" "$EMBED_MODEL"; do
  # /v1/models names an alias with its tag (`qwen3.6:latest`); either spelling is the configured name.
  if jq -e --arg m "$m" 'any(.data[]?; .id == $m or .id == ($m + ":latest"))' "$WORK/models.json" >/dev/null 2>&1; then
    pass "catalog lists $m"
  else fail "catalog lists $m" "got: $(head -c 400 "$WORK/models.json")"; fi
done
jq -e 'any(.data[]?; .id == "no-such-model")' "$WORK/models.json" >/dev/null 2>&1 && fail "catalog negative control" "an absent model was listed"

# ── capability ───────────────────────────────────────────────────────────────────────────────────────
caps() { jq -n --arg m "$1" '{model: $m}' > "$WORK/show.json"
         post /api/show "$WORK/show.json" 60 2>/dev/null | jq -c '.capabilities // []' 2>/dev/null || echo '[]'; }
has_cap() { jq -e --arg c "$2" 'index($c) != null' <<<"$1" >/dev/null; }
vcaps="$(caps "$VISION_MODEL")"; ecaps="$(caps "$EMBED_MODEL")"
if has_cap "$vcaps" vision && has_cap "$vcaps" completion; then pass "$VISION_MODEL reports vision+completion"
else fail "$VISION_MODEL reports vision+completion" "$vcaps"; fi
if has_cap "$ecaps" embedding; then pass "$EMBED_MODEL reports embedding"; else fail "$EMBED_MODEL reports embedding" "$ecaps"; fi
# Negative control: the embedder must NOT claim vision, or the capability read proves nothing.
has_cap "$ecaps" vision && fail "capability negative control" "$EMBED_MODEL claims vision: $ecaps"

# ── embeddings ───────────────────────────────────────────────────────────────────────────────────────
jq -n --arg m "$EMBED_MODEL" '{model: $m, input: [
  "Die Versicherung bezahlt den Schaden am Gebäude.",
  "The insurer pays for the damage to the building.",
  "Pihak asuransi membayar kerusakan pada bangunan.",
  "Morgen fahren wir mit dem Zug in die Berge zum Wandern."]}' > "$WORK/emb.json"
post /v1/embeddings "$WORK/emb.json" 120 > "$WORK/emb.out" 2>/dev/null || true
# One vector per line, as comma-separated numbers, in input order.
jq -r '.data | sort_by(.index) | .[].embedding | map(tostring) | join(",")' "$WORK/emb.out" > "$WORK/vec.txt" 2>/dev/null || : > "$WORK/vec.txt"
count=$(grep -c . "$WORK/vec.txt")
if [ "$count" -eq 4 ]; then pass "embeddings: 4 vectors"; else fail "embeddings: 4 vectors" "got $count: $(head -c 300 "$WORK/emb.out")"; fi
if [ "$count" -eq 4 ]; then
  bad=$(awk -F, -v d="$DIMS" 'NF != d { printf "%s ", NR ":" NF }' "$WORK/vec.txt")
  if [ -z "$bad" ]; then pass "embeddings: every vector has $DIMS dims"; else fail "embeddings: $DIMS dims" "line:dims $bad"; fi
  # cos(i,j) over the vectors on lines i and j.
  cosine() { awk -F, -v a="$1" -v b="$2" '
      NR==a { for (k=1;k<=NF;k++) x[k]=$k; n=NF } NR==b { for (k=1;k<=NF;k++) y[k]=$k }
      END { for (k=1;k<=n;k++) { d+=x[k]*y[k]; p+=x[k]*x[k]; q+=y[k]*y[k] } printf "%.4f", d/(sqrt(p)*sqrt(q)) }' "$WORK/vec.txt"; }
  # closer <anchor> <near> <far>: the anchor is closer to near than to far by a clear margin.
  closer() { awk -v n="$(cosine "$1" "$2")" -v f="$(cosine "$1" "$3")" 'BEGIN { exit !(n > f + 0.05) }'; }
  closer 1 2 4 && pass "embeddings: de is closer to its English translation than to an unrelated sentence ($(cosine 1 2) vs $(cosine 1 4))" \
               || fail "embeddings: de ~ en" "$(cosine 1 2) vs $(cosine 1 4)"
  closer 3 2 4 && pass "embeddings: id is closer to its English translation than to an unrelated sentence ($(cosine 3 2) vs $(cosine 3 4))" \
               || fail "embeddings: id ~ en" "$(cosine 3 2) vs $(cosine 3 4)"
  # Negative control: the SAME check with the pair swapped must fail.
  closer 1 4 2 && fail "embeddings negative control" "the unrelated sentence came out closer than the translation"
fi

# ── vision, translation, summary (the real model; the in-cluster helm test) ─────────────────────────
chat() { # chat <json-file> → the assistant's DECODED content on stdout ("" on error)
  if ! post /v1/chat/completions "$1" "$CHAT_TIMEOUT" > "$WORK/chat.out" 2> "$WORK/chat.err"; then
    cat "$WORK/chat.err" >&2; head -c 400 "$WORK/chat.out" >&2; return 0
  fi
  content_of < "$WORK/chat.out"
}
# reasoning_effort "none" turns qwen3.6's thinking off: a transcript needs no chain of thought, and on a CPU
# node every thought token is seconds.
vision_request() { # vision_request <png> → request file
  base64 -w0 "$1" > "$WORK/img.b64"
  jq -n --arg m "$VISION_MODEL" --rawfile img "$WORK/img.b64" '{model: $m, temperature: 0, reasoning_effort: "none",
    max_tokens: 400, messages: [{role: "user", content: [
      {type: "text", text: "Transcribe all text in this image exactly, line by line. Output only the text."},
      {type: "image_url", image_url: {url: ("data:image/png;base64," + $img)}}]}]}' > "$WORK/vision.json"
  echo "$WORK/vision.json"
}
text_request() { # text_request <system> <user> <max tokens> → request file
  jq -n --arg m "$VISION_MODEL" --arg s "$1" --arg u "$2" --argjson t "$3" '{model: $m, temperature: 0,
    reasoning_effort: "none", max_tokens: $t, messages: [{role: "system", content: $s}, {role: "user", content: $u}]}' > "$WORK/text.json"
  echo "$WORK/text.json"
}
lower() { printf '%s' "$1" | tr '[:upper:]' '[:lower:]'; }

if [ "$VISION" -eq 1 ]; then
  start=$(date +%s); text="$(chat "$(vision_request "$FIXTURES/de-id.png")")"; secs=$(( $(date +%s) - start ))
  if why=$(has_words "$text" Versicherung Gebäude Rechnung asuransi kerusakan tagihan); then
    pass "vision: the German+Indonesian page reads with all six words, umlaut included (${secs}s)"
  else fail "vision: German+Indonesian page" "$why; got: ${text:0:400}"; fi
  # Negative control: the same assertion on the English page must fail.
  start=$(date +%s); text_en="$(chat "$(vision_request "$FIXTURES/en.png")")"; secs=$(( $(date +%s) - start ))
  if has_words "$text_en" Versicherung Gebäude >/dev/null; then fail "vision negative control" "the English page yielded the German words: ${text_en:0:200}"
  elif has_words "$text_en" insurer building >/dev/null; then pass "vision negative control: the English page reads as English (${secs}s)"
  else fail "vision negative control" "the English page did not read at all: ${text_en:0:200}"; fi

  source_id="Pihak asuransi sudah memeriksa kerusakan pada bangunan dan akan membayar tagihan itu sebelum akhir bulan."
  start=$(date +%s)
  en="$(chat "$(text_request "Translate the user text to English. Answer with the translation only." "$source_id" 200)")"
  secs=$(( $(date +%s) - start ))
  if has_words "$(lower "$en")" insur damage >/dev/null && { has_words "$(lower "$en")" invoice >/dev/null || has_words "$(lower "$en")" bill >/dev/null; }; then
    pass "translation id → en (${secs}s): $en"
  else fail "translation id → en" "got: ${en:0:300}"; fi
  # Negative control: the source itself must not pass the check (the check reads English, not the input).
  has_words "$(lower "$source_id")" insur damage >/dev/null && fail "translation negative control" "the untranslated source passed"

  long="The insurer reviewed the damage to the warehouse roof after the storm in March. The adjuster found that the gutters had been blocked for months, which the policy excludes, but agreed that the wind damage to the north side is covered. The insurer will pay the roofing invoice of 48,000 euros for the north side before the end of the month and declines the remaining 12,000 euros for the gutters. The client may appeal within thirty days."
  start=$(date +%s); summary="$(chat "$(text_request "Summarise the user text in one sentence." "$long" 120)")"; secs=$(( $(date +%s) - start ))
  if [ -n "$summary" ] && [ "${#summary}" -lt $(( ${#long} * 2 / 3 )) ] && has_words "$(lower "$summary")" insur >/dev/null; then
    pass "summary (${secs}s, ${#summary} of ${#long} chars): $summary"
  else fail "summary" "got ${#summary} chars: ${summary:0:300}"; fi
  # Negative control: the length bound must reject the unsummarised input itself.
  [ "${#long}" -lt $(( ${#long} * 2 / 3 )) ] && fail "summary negative control" "the input passes its own length bound"
fi

echo "$failures failure(s)"
exit "$failures"
