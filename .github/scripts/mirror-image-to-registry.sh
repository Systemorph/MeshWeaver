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

src_digest=$(digest_of "$SRC") || src_digest=""
case "$src_digest" in
  sha256:*) ;;
  *) echo "::error::mirror-image-to-registry: could not resolve a digest for the SOURCE $SRC (got '${src_digest:-<nothing>}') — is the runner logged in to its registry? Nothing was mirrored."; exit 1 ;;
esac

args=()
for target in "$@"; do args+=(--tag "$target"); done
echo "→ mirroring $SRC ($src_digest) to: $*"
docker buildx imagetools create "${args[@]}" "$SRC"

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
