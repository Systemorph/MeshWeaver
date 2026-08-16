#!/usr/bin/env bash
# publish-bake-bundles.sh <bake-dir> <source-name>
#
# Publishes a CI NodeType bake (the directory mw-plugin-test's --bake-output wrote: one
# <package>.zip per package + framework-mvid.txt) to the shared storage the portals read at boot
# (#1660 WS3). The portal side is ShippedPrebuiltBundles.SeedPublishedRoot: each pod seeds
# <PreWarm:PrebuiltBundleRoot>/<its-own-framework-identity>/**/*.zip, so the key layout here is
#
#     <base>/prebuilt-bundles/<framework-identity>/<source-name>/<bundle>.zip
#
# where <framework-identity> comes from the bake's framework-mvid.txt (for CI builds: the commit
# identity g<sha>, identical for every CI build of the same commit — that determinism is what
# makes this publication findable by the images built from the same commit). <source-name> is the
# producing repo's segment (e.g. meshweaver-content, plugins, education) so multiple independent
# producers publish without clobbering; the bundle manifests inside carry the exact source SHA.
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
# Loud by design: a missing/empty target list, identity file, or bundle set FAILS — a publish
# step that silently ships nothing is exactly the regression (#1347 → #1660) this lane fixes.
set -euo pipefail

BAKE_DIR="${1:?usage: publish-bake-bundles.sh <bake-dir> <source-name>}"
SOURCE="${2:?usage: publish-bake-bundles.sh <bake-dir> <source-name>}"

if [ -z "${BAKE_PUBLISH_TARGETS:-}" ]; then
  echo "::error::BAKE_PUBLISH_TARGETS is not set — provision the repo variable with the portals' storage targets (<account>/<share>[/<base-path>], whitespace-separated). The CI bake cannot reach any portal without it."
  exit 1
fi

IDENTITY_FILE="$BAKE_DIR/framework-mvid.txt"
if [ ! -s "$IDENTITY_FILE" ]; then
  echo "::error::$IDENTITY_FILE is missing or empty — the bake carries no framework identity, so nothing can adopt it."
  exit 1
fi
IDENTITY="$(tr -d '[:space:]' < "$IDENTITY_FILE")"

shopt -s nullglob
BUNDLES=("$BAKE_DIR"/*.zip)
if [ "${#BUNDLES[@]}" -eq 0 ]; then
  echo "::error::no bundle zips under $BAKE_DIR — a bake that produced nothing must not reach the publish step."
  exit 1
fi

publish_one_target() { # <account> <share> <dest-dir>
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
  local zip
  for zip in "${BUNDLES[@]}"; do
    az storage file upload --account-name "$account" --share-name "$share" \
      --path "$dest/$(basename "$zip")" --source "$zip" \
      --auth-mode login --backup-intent --only-show-errors > /dev/null
    echo "published: $account/$share/$dest/$(basename "$zip")"
  done
}

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
  DEST="${BASE:+$BASE/}prebuilt-bundles/$IDENTITY/$SOURCE"
  # "Rebuild only when we need to" applies to the publish too (#1660 WS3): the identity is the
  # API-surface hash, so an internal-only merge resolves the SAME identity as the previous one —
  # its bake is byte-for-byte what is already published. Skip with a notice instead of
  # re-uploading; a genuinely new surface gets a new identity directory and publishes fully.
  existing=$(az storage file list --account-name "$ACCOUNT" --share-name "$SHARE" \
    --path "$DEST" --auth-mode login --backup-intent --query "length([?name!=null])" -o tsv \
    --only-show-errors 2>/dev/null || echo 0)
  if [ "${existing:-0}" -gt 0 ]; then
    echo "::notice::$ACCOUNT/$SHARE already holds $existing file(s) under $DEST — surface unchanged, bake already published; skipping."
    continue
  fi
  echo "→ $ACCOUNT/$SHARE: $DEST (${#BUNDLES[@]} bundle(s))"
  publish_one_target "$ACCOUNT" "$SHARE" "$DEST"
done

echo "bake published: identity=$IDENTITY source=$SOURCE bundles=${#BUNDLES[@]}"
