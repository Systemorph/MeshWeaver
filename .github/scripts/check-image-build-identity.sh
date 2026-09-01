#!/usr/bin/env bash
#
# Does the PUBLISHED portal image identify itself as the version this run built?
#
#   .github/scripts/check-image-build-identity.sh <tag> <expected-version> [<repository>]
#   exit 0 = the image reports the expected version   exit 1 = it does not, or could not be read
#
# 🚨 A TAG IS NOT EVIDENCE ABOUT THE BYTES UNDER IT, and that gap shipped. Between 2026-08-25 and
# 2026-09-01 every memex-portal-ai image was TAGGED 3.0.0-rc9.ci.<n> and REPORTED itself as 1.0.0.
# The cause was one missing `-p:Version=` on the publish: the portal host had moved to
# MeshWeaver.Plugins, whose src/Directory.Build.props does not import core's root props, so
# $(Version) evaluated to the SDK default — and the csproj bakes $(Version) into the image config as
# MESHWEAVER_PLATFORM_VERSION, which is the FIRST thing a running portal reads to identify itself.
#
# Nothing was red. `promote` applied a correct tag to an image that misreported itself; /api/version
# still answered correctly (it reads a core-built assembly), so the one surface a monitor would
# check looked fine. What broke was the self-updater: VersionSelect compares registry tags against
# the REPORTED version, so with it pinned at 1.0.0 every tag was newer forever — the install could
# never reach "up to date" and re-rolled on every check floor, and the About page and the header
# build chip both lied about the running build.
#
# So this asserts the ARTIFACT, per the deployment doctrine "verify the IMAGE, never the green
# tick". It reads the image CONFIG out of the registry — the same bytes the container runtime will
# hand the process as an environment variable — and compares it to the version this run intended.
#
# 🚨 EVERY architecture is checked, not the index. The publish produces a manifest LIST over
# linux/amd64 + linux/arm64; each entry carries its OWN config blob, so checking one proves nothing
# about the other, and an arm64 node is exactly where nobody would look.
#
# 🚨 FAIL CLOSED. An unreadable manifest, an absent variable, a config without the key — every one
# is exit 1. "Could not check" must never be reported as "checked and fine": that equivalence is
# the whole reason the original defect survived a week of green runs.
#
# Runnable locally against the real registry, which is how it was verified:
#   az login && .github/scripts/check-image-build-identity.sh 3.0.0-rc9.ci.7231 3.0.0-rc9.ci.7231
set -uo pipefail

TAG="${1:?usage: check-image-build-identity.sh <tag> <expected-version> [<repository>]}"
EXPECTED="${2:?usage: check-image-build-identity.sh <tag> <expected-version> [<repository>]}"
REPO="${3:-memex-portal-ai}"
REGISTRY="${ACR_NAME:-meshweaver}"
SERVER="$REGISTRY.azurecr.io"

# The variable the portal reads first — MeshWeaver.Mesh.PlatformBuildInfo
# .PlatformVersionEnvironmentVariable. Kept as a literal here because this script runs before (and
# independently of) anything that could resolve it from the source tree.
VAR="MESHWEAVER_PLATFORM_VERSION"

summary() { [ -n "${GITHUB_STEP_SUMMARY:-}" ] && echo "$1" >> "$GITHUB_STEP_SUMMARY"; return 0; }
die()     { echo "::error::$1"; summary "- ❌ $1"; exit 1; }
ok()      { echo "$1";          summary "- ✅ $1"; }

summary "### Build identity baked into \`$REPO:$TAG\`"

# An ACR refresh token from the ambient `az login` (ARM), exchanged for a pull-scoped access token.
# `az acr login --expose-token` deliberately does NOT need a docker daemon — this reads blobs over
# the registry's own REST API, exactly as check-image-set.sh reads manifests over ARM.
REFRESH=$(az acr login --name "$REGISTRY" --expose-token --output tsv --query accessToken 2>/dev/null)
[ -n "$REFRESH" ] || die "could not obtain an ACR token for $SERVER — is the job logged in to Azure?"

BEARER=$(curl -fsS -u "00000000-0000-0000-0000-000000000000:$REFRESH" \
  "https://$SERVER/oauth2/token?service=$SERVER&scope=repository:$REPO:pull" 2>/dev/null \
  | jq -r '.access_token // empty')
[ -n "$BEARER" ] || die "could not exchange the ACR token for a pull scope on $REPO"

# Accepts both the OCI and the Docker spellings of index and manifest: the SDK's container publish
# has emitted each at different times, and a 406 here would read as "image missing".
ACCEPT='application/vnd.oci.image.index.v1+json,application/vnd.docker.distribution.manifest.list.v2+json,application/vnd.oci.image.manifest.v1+json,application/vnd.docker.distribution.manifest.v2+json'

fetch() { curl -fsS -H "Authorization: Bearer $BEARER" -H "Accept: $ACCEPT" "https://$SERVER/v2/$REPO/manifests/$1" 2>/dev/null; }

# 🚨 Blobs are NOT served by the registry — /v2/…/blobs/… answers 307 to a SAS-signed blob-storage
# URL, so this needs -L where manifests do not. Without it curl returns the redirect's HTML body and
# jq fails on it; measured against the live registry while writing this. The follow deliberately
# does NOT use --location-trusted: curl drops the Authorization header on a cross-host redirect, and
# blob storage REJECTS the request if it is forwarded.
fetch_blob() { curl -fsSL -H "Authorization: Bearer $BEARER" "https://$SERVER/v2/$REPO/blobs/$1" 2>/dev/null; }

INDEX=$(fetch "$TAG")
[ -n "$INDEX" ] || die "could not read the manifest for $REPO:$TAG"

# A manifest list names its children; a single manifest IS its own child. Normalising to a digest
# list keeps the loop below identical for both shapes.
DIGESTS=$(echo "$INDEX" | jq -r 'if .manifests then .manifests[] | select(.platform.os != "unknown") | .digest else empty end')
if [ -z "$DIGESTS" ]; then
  CONFIG_DIGEST=$(echo "$INDEX" | jq -r '.config.digest // empty')
  [ -n "$CONFIG_DIGEST" ] || die "$REPO:$TAG is neither an image index nor an image manifest"
  DIGESTS=""
  CONFIGS="$CONFIG_DIGEST"
else
  CONFIGS=""
  for digest in $DIGESTS; do
    MANIFEST=$(fetch "$digest")
    [ -n "$MANIFEST" ] || die "could not read the platform manifest $digest of $REPO:$TAG"
    CONFIG_DIGEST=$(echo "$MANIFEST" | jq -r '.config.digest // empty')
    [ -n "$CONFIG_DIGEST" ] || die "platform manifest $digest of $REPO:$TAG names no config blob"
    CONFIGS="$CONFIGS $CONFIG_DIGEST"
  done
fi

checked=0
for config in $CONFIGS; do
  BLOB=$(fetch_blob "$config")
  [ -n "$BLOB" ] || die "could not read the config blob $config of $REPO:$TAG"

  PLATFORM=$(echo "$BLOB" | jq -r '"\(.os // "?")/\(.architecture // "?")"')
  # .config.Env is the runtime environment the container starts with — the exact channel the csproj
  # ships the version on. `select` rather than a grep so a variable whose VALUE contains the name
  # cannot match.
  ACTUAL=$(echo "$BLOB" | jq -r --arg v "$VAR" '(.config.Env // [])[] | select(startswith($v + "=")) | sub("^" + $v + "=" ; "")')

  [ -n "$ACTUAL" ] || die "$REPO:$TAG ($PLATFORM) ships no $VAR — the running portal would fall back to the entry assembly, which in MeshWeaver.Plugins is the SDK default 1.0.0"
  [ "$ACTUAL" = "$EXPECTED" ] || die "$REPO:$TAG ($PLATFORM) reports $VAR=$ACTUAL but this run built $EXPECTED — the image would misreport itself and every registry tag would look newer to its self-updater"

  ok "$PLATFORM reports $VAR=$ACTUAL"
  checked=$((checked + 1))
done

# The loop can only be empty if the normalisation above produced nothing, which the guards already
# refuse — but a check that silently examined zero images is the failure this file exists to
# prevent, so it is asserted rather than assumed.
[ "$checked" -gt 0 ] || die "no image config was examined for $REPO:$TAG — refusing to report a pass"

ok "$REPO:$TAG identifies itself as $EXPECTED on all $checked architecture(s)"
