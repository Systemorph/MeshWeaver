#!/usr/bin/env bash
#
# dockerhub-mirror.sh — the route by which a CI job reaches a Docker Hub RUNTIME image it declares in
# .github/dockerhub-mirror.list (Doc/Architecture/DockerHubInCi). Base layers of images core BUILDS
# (`FROM` lines in path-filtered workflows) are out of its scope and listed on that page.
#
#   dockerhub-mirror.sh ref <name>:<tag>    print ghcr.io/systemorph/dockerhub/<name>:<tag>@<digest>
#                                           for a line of .github/dockerhub-mirror.list; RED for an
#                                           image the list does not declare
#   dockerhub-mirror.sh sync                copy every listed digest to the mirror (packages: write),
#                                           read each one back and compare; RED on any mismatch
#   dockerhub-mirror.sh --self-test         the parsing and refusal arms, no network
#
# 🚨 No retries and no fallback to docker.io. A consumer that cannot reach the mirror is red, naming
# the line — a silent fall back to the anonymous pull is exactly the rate-limited path this replaces.
# For a declared image, `sync` is the only thing that touches Docker Hub, once per new digest, from a
# main/scheduled run.
set -euo pipefail

SELF_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LIST="${DOCKERHUB_MIRROR_LIST:-$SELF_DIR/../dockerhub-mirror.list}"
MIRROR="${DOCKERHUB_MIRROR_PREFIX:-ghcr.io/systemorph/dockerhub}"

# Emits "<name>:<tag> <digest>" per declared line; RED on a malformed one, so a typo can never
# read as "not declared", and RED on a second line for the same <name>:<tag> — otherwise `sync`
# would mirror both digests while `ref` hands consumers whichever came first.
entries() {
  [ -f "$LIST" ] || { echo "::error::dockerhub-mirror: no list at $LIST" >&2; return 1; }
  local line n=0 seen=" "
  while IFS= read -r line || [ -n "$line" ]; do
    n=$((n + 1))
    line="${line%%#*}"; line="$(printf '%s' "$line" | tr -d '[:space:]')"
    [ -n "$line" ] || continue
    if [[ ! "$line" =~ ^([a-z0-9]+([._-][a-z0-9]+)*(/[a-z0-9]+([._-][a-z0-9]+)*)*):([A-Za-z0-9_][A-Za-z0-9._-]{0,127})@(sha256:[0-9a-f]{64})$ ]]; then
      echo "::error::dockerhub-mirror: $LIST line $n is not <name>:<tag>@sha256:<64 hex>: '$line'" >&2
      return 1
    fi
    if [[ "$seen" == *" ${BASH_REMATCH[1]}:${BASH_REMATCH[5]} "* ]]; then
      echo "::error::dockerhub-mirror: $LIST line $n declares ${BASH_REMATCH[1]}:${BASH_REMATCH[5]} a second time — one digest per <name>:<tag>; move the existing line instead of appending" >&2
      return 1
    fi
    seen="$seen${BASH_REMATCH[1]}:${BASH_REMATCH[5]} "
    printf '%s:%s %s\n' "${BASH_REMATCH[1]}" "${BASH_REMATCH[5]}" "${BASH_REMATCH[6]}"
  done < "$LIST"
}

ref() {
  local want="$1" all name digest
  all="$(entries)" || return 1
  while read -r name digest; do
    if [ "$name" = "$want" ]; then printf '%s/%s@%s\n' "$MIRROR" "$name" "$digest"; return 0; fi
  done <<< "$all"
  echo "::error::dockerhub-mirror: '$want' is not declared in .github/dockerhub-mirror.list — CI pulls no Docker Hub image anonymously. Add <name>:<tag>@sha256:<digest> there (the file says how to resolve the digest without spending the pull limit), merge it, and let dockerhub-mirror.yml sync it before a pull request depends on it." >&2
  return 1
}

sync() {
  local all name digest target got rc=0
  all="$(entries)" || return 1
  [ -n "$all" ] || { echo "::error::dockerhub-mirror: the list declares no image — a sync over nothing has proven nothing" >&2; return 1; }
  while read -r name digest; do
    target="$MIRROR/$name"
    if got="$(docker buildx imagetools inspect "$target@$digest" --format '{{json .Manifest.Digest}}' 2>/dev/null)" && [ "${got//\"/}" = "$digest" ]; then
      echo "$name: already mirrored at $digest"
    else
      echo "$name: copying docker.io/$name@$digest → $target"
      if ! docker buildx imagetools create --tag "$target" "docker.io/${name%:*}@$digest"; then
        echo "::error::dockerhub-mirror: could not copy docker.io/${name%:*}@$digest to $target" >&2
        rc=1; continue
      fi
    fi
    # stderr stays in the log: an unreadable copy must say WHY it read as nothing.
    if ! got="$(docker buildx imagetools inspect "$target" --format '{{json .Manifest.Digest}}')"; then got=""; fi
    got="${got//\"/}"
    if [ "$got" != "$digest" ]; then
      echo "::error::dockerhub-mirror: $target reads back as '${got:-nothing}', not the declared $digest — a consumer pinned to the declared digest would not find it" >&2
      rc=1
    else
      echo "$name: $target = $digest (read back)"
    fi
  done <<< "$all"
  return "$rc"
}

self_test() {
  local tmp fail=0 out d1 d2
  tmp="$(mktemp -d)"
  d1="sha256:$(printf '%064d' 1)"; d2="sha256:$(printf '%064d' 2)"
  printf '# comment\n\nfoo/bar:1.2@%s  # trailing\nbaz:pg17@%s\n' "$d1" "$d2" > "$tmp/ok.list"
  printf 'foo/bar:latest\n' > "$tmp/bad.list"
  printf 'foo/bar:1.2@%s\nfoo/bar:1.2@%s\n' "$d1" "$d2" > "$tmp/dup.list"
  ok() { echo "  ✅ $1"; }
  ko() { echo "  ❌ $1"; fail=1; }
  out="$(DOCKERHUB_MIRROR_LIST="$tmp/ok.list" "$0" ref foo/bar:1.2)" || out=""
  if [ "$out" = "ghcr.io/systemorph/dockerhub/foo/bar:1.2@$d1" ]; then ok "a declared image resolves to the mirror, digest-pinned"; else ko "a declared image resolves to the mirror, digest-pinned (got '$out')"; fi
  out="$(DOCKERHUB_MIRROR_LIST="$tmp/ok.list" "$0" ref baz:pg17)" || out=""
  if [ "$out" = "ghcr.io/systemorph/dockerhub/baz:pg17@$d2" ]; then ok "a single-segment name resolves too"; else ko "a single-segment name resolves too (got '$out')"; fi
  if DOCKERHUB_MIRROR_LIST="$tmp/ok.list" "$0" ref foo/bar:9.9 2>/dev/null; then ko "an undeclared image is refused"; else ok "an undeclared image is refused"; fi
  if DOCKERHUB_MIRROR_LIST="$tmp/ok.list" "$0" ref foo/bar:1 2>/dev/null; then ko "a tag that only prefixes a declared one is refused"; else ok "a tag that only prefixes a declared one is refused"; fi
  if DOCKERHUB_MIRROR_LIST="$tmp/bad.list" "$0" ref foo/bar:latest 2>/dev/null; then ko "a line without a digest is RED, not 'undeclared'"; else ok "a line without a digest is RED, not 'undeclared'"; fi
  if DOCKERHUB_MIRROR_LIST="$tmp/dup.list" "$0" ref foo/bar:1.2 2>/dev/null; then ko "a second digest for the same name:tag is RED, never first-wins"; else ok "a second digest for the same name:tag is RED, never first-wins"; fi
  if DOCKERHUB_MIRROR_LIST="$tmp/none.list" "$0" ref foo/bar:1.2 2>/dev/null; then ko "a missing list is RED"; else ok "a missing list is RED"; fi
  out="$(entries)" || out=""
  if [ -n "$out" ]; then ok "the committed list parses and declares at least one image"; else ko "the committed list parses and declares at least one image"; fi
  rm -rf "$tmp"
  return "$fail"
}

case "${1:-}" in
  ref) [ -n "${2:-}" ] || { echo "usage: dockerhub-mirror.sh ref <name>:<tag>" >&2; exit 2; }; ref "$2" ;;
  sync) sync ;;
  --self-test) self_test ;;
  *) echo "usage: dockerhub-mirror.sh ref <name>:<tag> | sync | --self-test" >&2; exit 2 ;;
esac
