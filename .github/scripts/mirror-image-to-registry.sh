#!/usr/bin/env bash
#
# mirror-image-to-registry.sh <source image ref> <target image ref>...
#
# Copies ONE image (a manifest or a multi-arch index) from the registry it was pushed to into
# another registry, under one or more tags, and PROVES the copy: every target is read back and
# must resolve to the SAME digest as the source. This is how every image CD publishes reaches the
# fleet's own registry, cr.meshweaver.cloud (Doc/Architecture/ContainerRegistryInMemex), beside
# ACR — the same bytes, the same manifest, so a consumer pinned by digest (MW_IMAGE_DIGEST and its
# siblings, every `platform-image-digest:`) resolves the identical object on either host.
#
# `docker buildx imagetools create` with a single source and no annotations copies the descriptor
# as-is — index bytes included — mounting or re-uploading the referenced blobs across registries;
# it is the same operation `promote` has always used to mirror ACR to GHCR ("never a second
# `dotnet publish`: two independent builds of one commit are two compilations"). Credentials for
# BOTH registries come from ~/.docker/config.json — `az acr login` and registry-login.sh both
# write there.
#
# 🚨 THE READBACK IS THE VERIFICATION, and a mismatch is RED. A tag that exists on the target is
# not evidence that it names the bytes the source names (a re-marshalled index, a stale tag left
# by an earlier run, a push that half-landed) — and "verify the IMAGE, never the green tick" is the
# deployment doctrine. Fail-closed on anything that cannot be read: a digest that is not a
# `sha256:` is a failure, never an empty comparison that happens to pass.
set -euo pipefail

SRC="${1:-}"
[ -n "$SRC" ] || { echo "::error::mirror-image-to-registry: usage: mirror-image-to-registry.sh <source image ref> <target image ref>..."; exit 2; }
shift
[ $# -ge 1 ] || { echo "::error::mirror-image-to-registry: at least one target image ref is required"; exit 2; }
command -v docker > /dev/null 2>&1 || { echo "::error::mirror-image-to-registry: docker is not on PATH"; exit 1; }

digest_of() { # <image ref> — the digest of the manifest (or index) the ref resolves to
  docker buildx imagetools inspect "$1" --format '{{json .Manifest.Digest}}' | tr -d '"'
}

# ── Instrumentation: SIZE and RATE, one line per run ─────────────────────────────────────────
# 🚨 A copy that SLOWED DOWN and an image that GREW look identical in a duration, and a duration
# is all this lane has ever printed. #4566 measured this mirror at 7.5 → 18 min over twelve hours
# and could not tell the two apart from anything the run recorded; the answer (a rate excursion —
# the image is ~2 GiB throughout) had to be derived days later from an unrelated comment. Both
# numbers are printed here so the next excursion is a reading rather than an investigation.
#
# BEST EFFORT, deliberately: this is instrumentation, NOT the gate. The digest readback below is
# what proves the mirror, and nothing here may fail it. Every path resolves to "unknown" instead
# of erroring — a missing jq, a registry that will not serve `--raw`, an unreadable child.
stored_bytes() { # <image ref> — total stored bytes over every manifest in the index, or nothing
  (
    set +e +o pipefail
    command -v jq > /dev/null 2>&1 || exit 0
    ref="$1"
    raw=$(docker buildx imagetools inspect "$ref" --raw 2> /dev/null)
    [ -n "$raw" ] || exit 0
    # Drop a :tag or an @digest so each child can be addressed by digest in the SAME repository,
    # which is where an index's `manifests[]` entries always live.
    repo="${ref%@*}"; repo="${repo%:*}"
    total=0
    if printf '%s' "$raw" | jq -e 'has("manifests")' > /dev/null 2>&1; then
      for d in $(printf '%s' "$raw" | jq -r '.manifests[]?.digest // empty'); do
        one=$(docker buildx imagetools inspect "${repo}@${d}" --raw 2> /dev/null \
              | jq '[(.config.size // 0)] + [(.layers[]?.size // 0)] | add // 0' 2> /dev/null)
        # 🚨 One unreadable child ⇒ NO total. A partial sum would render as a smaller image and
        # read as growth in reverse — worse than "unknown", which at least abstains.
        case "$one" in '' | *[!0-9]*) exit 0 ;; esac
        total=$((total + one))
      done
    else
      total=$(printf '%s' "$raw" \
              | jq '[(.config.size // 0)] + [(.layers[]?.size // 0)] | add // 0' 2> /dev/null)
      case "$total" in '' | *[!0-9]*) exit 0 ;; esac
    fi
    [ "$total" -gt 0 ] && printf '%s' "$total"
    exit 0
  )
}

src_digest=$(digest_of "$SRC") || src_digest=""
case "$src_digest" in
  sha256:*) ;;
  *) echo "::error::mirror-image-to-registry: could not resolve a digest for the SOURCE $SRC (got '${src_digest:-<nothing>}') — is the runner logged in to its registry? Nothing was mirrored."; exit 1 ;;
esac

src_bytes=$(stored_bytes "$SRC") || src_bytes=""

args=()
for target in "$@"; do args+=(--tag "$target"); done
echo "→ mirroring $SRC ($src_digest) to: $*"
started=$(date +%s)
docker buildx imagetools create "${args[@]}" "$SRC"
elapsed=$(( $(date +%s) - started ))
# Clamp so a sub-second copy cannot divide by zero. It costs at most one second of accuracy on a
# measurement whose whole point is the difference between seven minutes and eighteen.
[ "$elapsed" -gt 0 ] || elapsed=1

if [ -n "$src_bytes" ]; then
  # MiB for the size a human compares against the last run; kB/s for the rate, because at the
  # 2 MB/s this lane has been measured at an integer MB/s rounds the whole excursion away.
  echo "::notice::mirror $SRC: $((src_bytes / 1048576)) MiB stored, copied in ${elapsed}s — \
≈$((src_bytes / elapsed / 1000)) kB/s effective (one image's stored bytes over the wall time of the copy)"
else
  echo "::notice::mirror $SRC: copied in ${elapsed}s — size unknown, so no rate can be derived"
fi

failed=0
for target in "$@"; do
  got=$(digest_of "$target") || got=""
  if [ "$got" = "$src_digest" ]; then
    echo "verified: $target = $src_digest (identical to the source)"
  else
    echo "::error::mirror-image-to-registry: $target resolves to '${got:-<unreadable>}' but the source $SRC is $src_digest — the mirror did NOT land the same manifest. A digest-pinned consumer would resolve a different object on this registry than on the source; refusing to report this image as published there."
    failed=$((failed + 1))
  fi
done
[ "$failed" -eq 0 ] || exit 1
echo "mirrored $SRC → $# target(s), digest $src_digest verified on every one"
