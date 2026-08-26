#!/usr/bin/env bash
# publish-bake-bundles.sh <bake-dir> <source-name> [<source-sha>] [<release-version>]
#
# Publishes a CI NodeType bake (the directory mw-plugin-test's --bake-output wrote: one
# <package>.zip per package + framework-mvid.txt) to the shared storage the portals read at boot
# (#1660 WS3). The portal side is ShippedPrebuiltBundles.SeedPublishedRoot: each pod seeds
# <PreWarm:PrebuiltBundleRoot>/<its-own-framework-identity>/**/*.zip, so the key layout here is
#
#     <base>/prebuilt-bundles/<framework-identity>/<source-name>/<bundle>.zip
#
# where <framework-identity> comes from the bake's framework-mvid.txt. 🚨 This used to say "for CI
# builds: the commit identity g<sha>", and that went stale when the surface identity landed: the
# bake host IS mw-plugin-test, which opts into the surface manifest
# (MeshWeaver.PluginTester.csproj) and therefore resolves the SURFACE identity s<hash>. g<sha> is
# now only the fallback for manifest-LESS processes (FrameworkBuildIdentity). The distinction is
# not cosmetic — s<hash> is architecture-SENSITIVE (four reference assemblies differ between the
# amd64 and arm64 variants of one image) while g<sha> is not, which is exactly what decides
# whether two architectures can publish side by side or collide. See the guard below.
# <source-name> is the
# producing repo's segment (e.g. meshweaver-content, plugins, education) so multiple independent
# producers publish without clobbering; the bundle manifests inside carry the exact source SHA.
#
# <source-sha> is the CONTENT identity — the producing repo's commit the bake was taken from
# (what the caller passed to mw-plugin-test --source-sha). The publication key is content ×
# framework: a sealed directory is skipped only when BOTH match (see the sealed-skip below).
# Omitting it degrades the skip to framework-identity-only — correct for a producer whose content
# lives in the framework repo itself, but a NODE repo must pass it: its content changes while the
# framework identity stays put, and a framework-only skip would freeze its first publication for
# the whole framework release.
#
# ENVIRONMENT
#   BAKE_PUBLISH_TARGETS   whitespace-separated targets, each  <storage-account>/<file-share>
#                          (optionally <storage-account>/<file-share>/<base-path>). These are the
#                          Azure Files shares the portals mount (on AKS: the share behind the
#                          memex-data PVC, mounted at /data — so the portal reads
#                          /data/prebuilt-bundles/... when PreWarm__PrebuiltBundleRoot is
#                          /data/prebuilt-bundles).
#
# AUTH: `az login` must already have happened (the CD jobs use OIDC). Data-plane access uses
# --auth-mode login with --backup-intent, which requires the identity to hold the
# "Storage File Data Privileged Contributor" role on the target storage accounts.
#
# <release-version> is the PLATFORM VERSION this publication belongs to (main-cd passes the
# promoted `memex-portal-ai:<version>`; node repos pass nothing — they do not define a platform
# release). When given, the script also records the version → framework-identity mapping at
#
#     <base>/prebuilt-bundles/_releases/<release-version>
#
# 🚨 That marker is the ONLY way anything outside the image can learn a release's framework
# identity: the identity is a property of the BINARIES (#1725), resolved by the image itself, so it
# cannot be computed from a tag or a commit. The release gates (#1754 deployment, #1755 build) read
# it to answer "is every deployed package available for the release we are about to roll to"; a
# missing marker therefore means one precise thing — that release published no platform content
# bake — and both gates HOLD on it rather than guessing (fail safe).
#
# 🚨 The marker is written on EVERY run, deliberately OUTSIDE the already-sealed skip below. The
# skip keys on content × framework, and the API-surface identity is breaking-change-keyed, so an
# ordinary release re-resolves the SAME identity and skips the upload — if the marker rode along
# with the bundles, the second and every later release of a surface generation would have no marker
# at all and every environment would be held forever on a release that is in fact perfectly fine.
#
# Loud by design: a missing/empty target list, identity file, or bundle set FAILS — a publish
# step that silently ships nothing is exactly the regression (#1347 → #1660) this lane fixes.
set -euo pipefail

USAGE="usage: publish-bake-bundles.sh <bake-dir> <source-name> [<source-sha>] [<release-version>]"
BAKE_DIR="${1:?$USAGE}"
SOURCE="${2:?$USAGE}"
SOURCE_SHA="${3:-}"
RELEASE_VERSION="${4:-}"

# Whitespace-only counts as unset: `for target in $BAKE_PUBLISH_TARGETS` would iterate zero
# times and the script would report success having published nowhere — the silent-nothing
# outcome this script exists to make impossible.
if [ -z "${BAKE_PUBLISH_TARGETS:-}" ] || ! grep -q '[^[:space:]]' <<<"${BAKE_PUBLISH_TARGETS:-}"; then
  echo "::error::BAKE_PUBLISH_TARGETS is not set (or holds no targets) — provision the repo variable with the portals' storage targets (<account>/<share>[/<base-path>], whitespace-separated). The CI bake cannot reach any portal without it."
  exit 1
fi

IDENTITY_FILE="$BAKE_DIR/framework-mvid.txt"
if [ ! -s "$IDENTITY_FILE" ]; then
  echo "::error::$IDENTITY_FILE is missing or empty — the bake carries no framework identity, so nothing can adopt it."
  exit 1
fi
IDENTITY="$(tr -d '[:space:]' < "$IDENTITY_FILE")"

# 🚨 ARCHITECTURE IS PART OF THE COMPATIBILITY CLAIM, and the identity does not always carry it.
# FrameworkBuildIdentity resolves surface identity (s<hash>) -> stamped commit identity (g<sha>)
# -> MVID set. The FIRST is architecture-sensitive: four reference assemblies genuinely differ
# between the amd64 and arm64 variants of one multi-arch image, so the two resolve different
# s<hash> values and cannot collide. The SECOND is NOT: g<sha> is the same string for every CI
# build of a commit, whatever it was built on.
#
# So the moment a second architecture publishes, a g<sha> lane has two producers writing ONE
# directory, and both outcomes are silent and wrong: same source commit => the sealed-skip below
# fires and the second architecture NEVER publishes while its pods adopt the first one's bytes;
# different source => it unseals and OVERWRITES. Adopting bytes from an identity you did not
# resolve is the exact shape CiContentBake.md forbids ("never publish a bake under several
# identities, never let a pod scan for a nearest one") and it surfaces as a TypeLoadException
# inside a collectible ALC at activation — no overlay, no compile error, nothing to grep.
#
# Recording it makes the collision DETECTABLE, and the guard below makes it LOUD.
BAKE_ARCHITECTURE="${BAKE_ARCHITECTURE:-linux-x64}"
case "$BAKE_ARCHITECTURE" in
  linux-x64|linux-arm64) ;;
  *)
    echo "::error::BAKE_ARCHITECTURE='$BAKE_ARCHITECTURE' is not a known architecture (linux-x64|linux-arm64). It keys a compatibility claim, so an unrecognised value must never be published."
    exit 1 ;;
esac
# -s passes a whitespace-only file; an empty identity would silently publish under
# 'prebuilt-bundles//<source>' — a directory no pod's identity ever resolves to.
if [ -z "$IDENTITY" ]; then
  echo "::error::$IDENTITY_FILE holds only whitespace — the bake carries no framework identity, so nothing can adopt it."
  exit 1
fi

shopt -s nullglob
BUNDLES=("$BAKE_DIR"/*.zip)
if [ "${#BUNDLES[@]}" -eq 0 ]; then
  echo "::error::no bundle zips under $BAKE_DIR — a bake that produced nothing must not reach the publish step."
  exit 1
fi

# The completeness sentinel (must match ShippedPrebuiltBundles.CompletionSentinelFileName): its
# PRESENCE means "every bundle of this publication landed", because it is uploaded strictly LAST.
# Its content lists the bundle set, so the reader can detect a listed-but-missing bundle too.
#
# 🚨 The local copy lives in a temp DIRECTORY under its REAL name, and the upload below targets
# the destination DIRECTORY (not "$dest/$SENTINEL"): `az storage file upload` silently treats an
# EXTENSIONLESS --path as a directory and appends the source basename — so uploading a mktemp
# file to "$dest/_complete" actually attempts "$dest/_complete/tmp.XXXX" and fails
# `ParentNotFound` every time, while every ".zip" beside it lands fine. Verified live against the
# portals' Azure Files share 2026-08-17; with the naive shape the seal step can never succeed and
# every publication stays torn (unreadable to portals) forever.
SENTINEL="_complete"
SENTINEL_LOCAL_DIR=$(mktemp -d)
trap 'rm -rf "$SENTINEL_LOCAL_DIR"' EXIT
SENTINEL_LOCAL="$SENTINEL_LOCAL_DIR/$SENTINEL"
for zip in "${BUNDLES[@]}"; do basename "$zip"; done | sort > "$SENTINEL_LOCAL"

# The CONTENT identity marker: which source commit this publication was baked from. It is NOT
# part of the reader's contract (SeedPublishedRoot seeds only what the sentinel lists; extra
# files are ignored) — it exists solely so the sealed-skip below can compare content, not just
# framework identity. Written BEFORE the sentinel, so a sealed directory always carries a
# consistent marker.
SOURCE_MARKER="source-commit.txt"
SOURCE_MARKER_LOCAL="$SENTINEL_LOCAL_DIR/$SOURCE_MARKER"
printf '%s\n' "${SOURCE_SHA:-unknown}" > "$SOURCE_MARKER_LOCAL"

# The ARCHITECTURE marker — same contract as the content marker: not part of the reader's
# contract (SeedPublishedRoot seeds only what the sentinel lists), written BEFORE the sentinel so
# a sealed directory always carries one, and read by the cross-architecture guard below.
ARCH_MARKER="architecture.txt"
ARCH_MARKER_LOCAL="$SENTINEL_LOCAL_DIR/$ARCH_MARKER"
printf '%s\n' "$BAKE_ARCHITECTURE" > "$ARCH_MARKER_LOCAL"

# The release-marker directory (must match PublishedBundleCatalogue.ReleaseMarkerDirectoryName).
# Leading underscore so it can never collide with a framework-identity directory (s… / g…).
RELEASES_DIR="_releases"
RELEASE_MARKER_LOCAL=""
if [ -n "${RELEASE_VERSION:-}" ]; then
  # A version containing a path separator would escape the directory; the platform's versions are
  # semver tags, so anything else is a caller bug and must be loud, not silently rewritten.
  case "$RELEASE_VERSION" in
    */*|..|.) echo "::error::release-version '$RELEASE_VERSION' is not a plain version string"; exit 1;;
  esac
  RELEASE_MARKER_LOCAL="$SENTINEL_LOCAL_DIR/$RELEASE_VERSION"
  printf '%s\n' "$IDENTITY" > "$RELEASE_MARKER_LOCAL"
fi

ensure_directory() { # <account> <share> <dir-path>
  local account="$1" share="$2" dest="$3" path="" part
  # az storage directory create is not recursive and errors on an existing directory on some CLI
  # versions — create each level only when absent.
  local IFS='/'
  for part in $dest; do
    path="${path:+$path/}$part"
    local exists
    exists=$(az storage directory exists --account-name "$account" --share-name "$share" \
      --name "$path" --auth-mode login --backup-intent --query exists -o tsv --only-show-errors)
    if [ "$exists" != "true" ]; then
      az storage directory create --account-name "$account" --share-name "$share" \
        --name "$path" --auth-mode login --backup-intent --only-show-errors > /dev/null
    fi
  done
}

# The version → framework-identity mapping the release gates read (#1754/#1755). Written on every
# run for every target, never gated on the sealed-skip — see the header. The file NAME is the
# platform version and its CONTENT is the identity, so a reader needs one stat plus one read and
# no listing.
publish_release_marker() { # <account> <share> <base>
  local account="$1" share="$2" base="$3"
  local dir="${base:+$base/}prebuilt-bundles/$RELEASES_DIR"
  ensure_directory "$account" "$share" "$dir"
  # Same directory-as---path trick as the sentinel below: the CLI appends the source basename, and
  # the local file is already named after the version.
  az storage file upload --account-name "$account" --share-name "$share" \
    --path "$dir" --source "$RELEASE_MARKER_LOCAL" \
    --auth-mode login --backup-intent --only-show-errors > /dev/null
  echo "release marker: $account/$share/$dir/$RELEASE_VERSION → $IDENTITY"
}

publish_one_target() { # <account> <share> <dest-dir> <resealing>
  local account="$1" share="$2" dest="$3" resealing="$4"
  ensure_directory "$account" "$share" "$dest"
  # Republishing OVER a sealed directory: UNSEAL first. Readers must never seed a mid-replace
  # mix of old and new bundles under a stale sentinel — deleting the sentinel returns the
  # directory to the not-yet-complete state readers skip, and the re-seal below closes it again.
  if [ "$resealing" = "true" ]; then
    az storage file delete --account-name "$account" --share-name "$share" \
      --path "$dest/$SENTINEL" --auth-mode login --backup-intent --only-show-errors > /dev/null
    echo "unsealed: $account/$share/$dest ($SENTINEL removed — content changed, republishing)"
  fi
  local zip
  for zip in "${BUNDLES[@]}"; do
    az storage file upload --account-name "$account" --share-name "$share" \
      --path "$dest/$(basename "$zip")" --source "$zip" \
      --auth-mode login --backup-intent --only-show-errors > /dev/null
    echo "published: $account/$share/$dest/$(basename "$zip")"
  done
  # 🚨 Both marker uploads pass the DIRECTORY as --path on purpose — the CLI appends the source
  # basename. An extensionless "$dest/$SENTINEL" --path would be silently re-interpreted as a
  # DIRECTORY and fail ParentNotFound (see the SENTINEL_LOCAL comment above).
  az storage file upload --account-name "$account" --share-name "$share" \
    --path "$dest" --source "$SOURCE_MARKER_LOCAL" \
    --auth-mode login --backup-intent --only-show-errors > /dev/null
  az storage file upload --account-name "$account" --share-name "$share" \
    --path "$dest" --source "$ARCH_MARKER_LOCAL" \
    --auth-mode login --backup-intent --only-show-errors > /dev/null
  # LAST write — the atomic completeness marker. Anything that dies before this line leaves the
  # directory sentinel-less: unreadable to portals, re-published wholesale by the next run.
  az storage file upload --account-name "$account" --share-name "$share" \
    --path "$dest" --source "$SENTINEL_LOCAL" \
    --auth-mode login --backup-intent --only-show-errors > /dev/null
  echo "sealed: $account/$share/$dest/$SENTINEL (${#BUNDLES[@]} bundle(s), source ${SOURCE_SHA:-unknown})"
}

PUBLISHED=0
MARKERS=0
for target in $BAKE_PUBLISH_TARGETS; do
  ACCOUNT="${target%%/*}"
  REST="${target#*/}"
  SHARE="${REST%%/*}"
  BASE=""
  case "$REST" in */*) BASE="${REST#*/}";; esac
  if [ -z "$ACCOUNT" ] || [ -z "$SHARE" ] || [ "$ACCOUNT" = "$target" ]; then
    echo "::error::malformed BAKE_PUBLISH_TARGETS entry '$target' — expected <account>/<share>[/<base-path>]"
    exit 1
  fi
  # 🚨 BEFORE the sealed-skip below, which `continue`s past everything that follows it. The
  # version → identity mapping must land on EVERY run — including the (common) run whose bundles
  # are already published — or the release gates would hold every environment on a release that is
  # perfectly fine, and an environment frozen for weeks is its own outage. A failure here is fatal
  # by `set -e`, deliberately: a silently missing marker holds everything.
  if [ -n "${RELEASE_MARKER_LOCAL:-}" ]; then
    publish_release_marker "$ACCOUNT" "$SHARE" "$BASE"
    MARKERS=$((MARKERS + 1))
  fi
  DEST="${BASE:+$BASE/}prebuilt-bundles/$IDENTITY/$SOURCE"
  # "Rebuild only when we need to" applies to the publish too (#1660 WS3), but the key is
  # CONTENT × FRAMEWORK: a sealed directory is already-published only when the framework
  # identity (the directory) AND the source commit (the marker) both match. A framework-only
  # skip would freeze a node repo's FIRST publication for the whole framework release — every
  # later content merge resolves the same framework identity, finds the seal, and ships nothing.
  #
  # 🚨 The skip keys on the _complete SENTINEL, never on "any file exists": the sentinel is
  # written LAST, after every bundle uploaded, so a publish that died mid-way (cancelled run,
  # network fault) leaves a directory WITHOUT it — and the next publish re-uploads everything
  # (idempotent overwrites) instead of freezing the identity incomplete forever. The read side
  # (ShippedPrebuiltBundles.SeedPublishedRoot) honours the same contract: a source directory
  # without its sentinel is never seeded.
  # 🚨 CROSS-ARCHITECTURE GUARD — runs BEFORE the skip/reseal decision below, because the SKIP is
  # itself one of the two silent failures: a second architecture publishing the same source commit
  # under a g<sha> identity would be told "already published" and ship nothing, leaving its pods to
  # adopt the other architecture's bytes. Refusing is always safe (the lane fails, an operator
  # reads why); overwriting or skipping is not.
  #
  # Deliberately NOT a skip and NOT a warning. A warning here would be read as noise by the one
  # run that most needs to stop, and a skip is the defect.
  # 🚨 EXISTENCE FIRST, and fail CLOSED. Reading the marker with `download … || echo ""` would
  # turn every failure — a transient fault, an expired credential, a CLI error — into "no marker
  # recorded", which is the one answer that lets the publish proceed. A guard whose error path is
  # indistinguishable from its permissive path is not a guard.
  arch_exists=$(az storage file exists --account-name "$ACCOUNT" --share-name "$SHARE" \
    --path "$DEST/$ARCH_MARKER" --auth-mode login --backup-intent --query exists -o tsv \
    --only-show-errors 2>/dev/null || echo "unknown")
  if [ "$arch_exists" != "true" ] && [ "$arch_exists" != "false" ]; then
    echo "::error::could not determine whether $ACCOUNT/$SHARE holds $DEST/$ARCH_MARKER (az returned no usable answer). Refusing rather than assuming the marker is absent — that assumption is what would let one architecture overwrite another's publication."
    exit 1
  fi
  published_arch=""
  if [ "$arch_exists" = "true" ]; then
    if ! az storage file download --account-name "$ACCOUNT" --share-name "$SHARE" \
        --path "$DEST/$ARCH_MARKER" --dest "$SENTINEL_LOCAL_DIR/remote-$ARCH_MARKER" \
        --auth-mode login --backup-intent --only-show-errors > /dev/null 2>&1; then
      echo "::error::$DEST/$ARCH_MARKER EXISTS under $ACCOUNT/$SHARE but could not be read. Refusing: an unreadable marker is not an absent one."
      exit 1
    fi
    published_arch="$(tr -d '[:space:]' < "$SENTINEL_LOCAL_DIR/remote-$ARCH_MARKER")"
    rm -f "$SENTINEL_LOCAL_DIR/remote-$ARCH_MARKER"
    if [ -z "$published_arch" ]; then
      echo "::error::$DEST/$ARCH_MARKER under $ACCOUNT/$SHARE is present but EMPTY — the incumbent's architecture cannot be established, so this publication cannot be proven safe. Refusing."
      exit 1
    fi
  fi
  if [ -n "$published_arch" ] && [ "$published_arch" != "$BAKE_ARCHITECTURE" ]; then
    echo "::error::$ACCOUNT/$SHARE holds a publication under $DEST built for '$published_arch', but this bake is '$BAKE_ARCHITECTURE'. One framework identity cannot hold two architectures: the reference assemblies differ, so pods resolving this identity would adopt bytes they did not build against (TypeLoadException inside a collectible ALC at activation). This means the identity is architecture-INDEPENDENT — a g<sha> commit stamp rather than an s<hash> surface hash — so the two lanes need distinct identities before both can publish. Refusing rather than overwriting '$published_arch'."
    exit 1
  fi
  complete=$(az storage file exists --account-name "$ACCOUNT" --share-name "$SHARE" \
    --path "$DEST/$SENTINEL" --auth-mode login --backup-intent --query exists -o tsv \
    --only-show-errors 2>/dev/null || echo false)
  # An incumbent with NO architecture marker predates this recording, and the only lane that has
  # ever published is amd64 — so it is treated as linux-x64.
  if [ -z "$published_arch" ] && [ "$complete" = "true" ]; then
    if [ "$BAKE_ARCHITECTURE" = "linux-x64" ]; then
      # 🚨 BACKFILL, and it must happen HERE — before the sealed-skip below, which `continue`s.
      # Without it the two rules deadlock: the arm64 lane refuses an unmarked incumbent and points
      # at this lane to stamp it, while this lane recognises its own publication, skips, and never
      # writes the marker — so the arm64 lane is blocked forever by an instruction that can never
      # be carried out. Stamping is safe precisely because the refusal below is sound: only the
      # amd64 lane has ever published, and this IS that lane.
      az storage file upload --account-name "$ACCOUNT" --share-name "$SHARE" \
        --path "$DEST" --source "$ARCH_MARKER_LOCAL" \
        --auth-mode login --backup-intent --only-show-errors > /dev/null
      echo "::notice::stamped $DEST/$ARCH_MARKER = $BAKE_ARCHITECTURE on a pre-existing publication (it predates architecture recording). Another architecture can now establish whether it may publish under this identity."
      published_arch="$BAKE_ARCHITECTURE"
    else
      echo "::error::$ACCOUNT/$SHARE holds a COMPLETE publication under $DEST with no $ARCH_MARKER — it predates architecture recording, so it can only be the linux-x64 lane. This bake is '$BAKE_ARCHITECTURE' and would overwrite it. The next linux-x64 publication to this target stamps the marker automatically (even when it skips the bundles); retry after it has run."
      exit 1
    fi
  fi
  resealing=false
  if [ "$complete" = "true" ]; then
    published_sha=$(az storage file download --account-name "$ACCOUNT" --share-name "$SHARE" \
      --path "$DEST/$SOURCE_MARKER" --dest "$SENTINEL_LOCAL_DIR/remote-$SOURCE_MARKER" \
      --auth-mode login --backup-intent --only-show-errors > /dev/null 2>&1 \
      && tr -d '[:space:]' < "$SENTINEL_LOCAL_DIR/remote-$SOURCE_MARKER" || echo "")
    if [ -n "${SOURCE_SHA:-}" ] && [ "$published_sha" = "$SOURCE_SHA" ]; then
      echo "::notice::$ACCOUNT/$SHARE holds a COMPLETE publication of THIS content under $DEST (sentinel present, source $published_sha) — already published; skipping."
      continue
    fi
    if [ -z "${SOURCE_SHA:-}" ]; then
      # No content identity given (framework-repo producer): the framework identity IS the
      # content key, so a sealed directory is already this publication.
      echo "::notice::$ACCOUNT/$SHARE holds a COMPLETE publication under $DEST ($SENTINEL present) — surface unchanged, bake already published; skipping."
      continue
    fi
    resealing=true
    echo "sealed publication under $DEST is from source '${published_sha:-<unrecorded>}' but this bake is from '$SOURCE_SHA' — republishing."
  fi
  echo "→ $ACCOUNT/$SHARE: $DEST (${#BUNDLES[@]} bundle(s))"
  publish_one_target "$ACCOUNT" "$SHARE" "$DEST" "$resealing"
  PUBLISHED=$((PUBLISHED + 1))
done

echo "bake published: identity=$IDENTITY arch=$BAKE_ARCHITECTURE source=$SOURCE source-sha=${SOURCE_SHA:-unknown} bundles=${#BUNDLES[@]} targets-published=$PUBLISHED release=${RELEASE_VERSION:-none} release-markers=$MARKERS"
