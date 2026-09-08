#!/bin/sh
# bundle-fetch — the `bundle-fetch` init container of the portal pod.
#
# Materialises the sealed plugin publications the pre-warm reads (ShippedPrebuiltBundles,
# `PreWarm:PrebuiltBundleRoot`) from the fleet registry BEFORE the portal starts, so the portal
# keeps reading a filesystem and needs no registry client, no network and no mesh to seed.
# Design: Doc/Architecture/PluginBundlesInTheRegistry.
#
# For every source in BUNDLES_SOURCES it resolves `<registry>/plugins/<source>:<identity>` — the
# publication's image INDEX — to its digest, pulls that digest's whole graph with ORAS into a
# staging directory (every bundle's `.zip` and `.module.nupkg` plus every sidecar the publication
# carries, each written under the name its layer is titled with), checks the pulled `_complete`
# against what landed, and only then moves the staging directory into place as
# `<root>/<identity>/<source>/` — one rename, so a reader sees the publication whole or not at all.
#
# Outcomes, and they are deliberately distinct:
#   * the tag is absent (`not found` on resolve)  → UNSEALED: log it, write nothing for that
#     source, keep going, exit 0 at the end. The pre-warm then reports the source absent and the
#     sweep compiles, exactly as it does today for a source CI has not published.
#   * anything else fails (a denied pull, a network error, a torn pull, a listed-but-missing
#     bundle) → exit 1 with the reason. The kubelet restarts this container and the portal does not
#     start on a half-fetched shelf; a fetch that cannot complete must never read as "no bundles".
#
# Runs in the ORAS image (Alpine + busybox: POSIX sh, no bash, no jq). Configuration is the
# environment the Deployment renders from `bundles.*`:
#   BUNDLES_REGISTRY         the registry host (cr.meshweaver.cloud)             required
#   BUNDLES_SOURCES          space-separated source names, as the publisher named them
#                            (`plugins education`)                                required
#   BUNDLES_ROOT             the directory the layout is written under — the pod's
#                            PreWarm__PrebuiltBundleRoot                          required
#   BUNDLES_IDENTITY         the framework identity to pull                      one of the two
#   BUNDLES_IDENTITY_FILE    a file holding it (first non-blank line)            one of the two
#   BUNDLES_REGISTRY_CONFIG  a docker config.json holding the registry credential — the pod's
#                            imagePullSecret, mounted                             optional
#   BUNDLES_PLAIN_HTTP       "true" for a registry without TLS (tests only)      optional
#   ORAS                     the oras binary                                     default /bin/oras
set -u

log() { printf 'bundle-fetch: %s\n' "$*"; }
die() { printf 'bundle-fetch: ERROR: %s\n' "$*" >&2; exit 1; }

registry="${BUNDLES_REGISTRY:-}"
sources="${BUNDLES_SOURCES:-}"
root="${BUNDLES_ROOT:-}"
identity="${BUNDLES_IDENTITY:-}"
identity_file="${BUNDLES_IDENTITY_FILE:-}"
registry_config="${BUNDLES_REGISTRY_CONFIG:-}"
oras="${ORAS:-/bin/oras}"

[ -n "$registry" ] || die "BUNDLES_REGISTRY is not set"
[ -n "$sources" ] || die "BUNDLES_SOURCES is not set"
[ -n "$root" ] || die "BUNDLES_ROOT is not set"
command -v "$oras" >/dev/null 2>&1 || die "oras is not available at '$oras'"

if [ -z "$identity" ]; then
  [ -n "$identity_file" ] || die "neither BUNDLES_IDENTITY nor BUNDLES_IDENTITY_FILE is set"
  [ -r "$identity_file" ] || die "BUNDLES_IDENTITY_FILE '$identity_file' is not readable"
  identity=$(sed -n 's/^[[:space:]]*\([^[:space:]]\{1,\}\).*/\1/p' "$identity_file" | head -n 1)
  [ -n "$identity" ] || die "BUNDLES_IDENTITY_FILE '$identity_file' holds no identity"
fi
case "$identity" in
  */*|*' '*|.|..) die "identity '$identity' is not a single path segment" ;;
esac

# ORAS flags shared by every call: the credential file when one is mounted, plain HTTP for tests.
set --
if [ -n "$registry_config" ]; then
  [ -r "$registry_config" ] || die "BUNDLES_REGISTRY_CONFIG '$registry_config' is not readable"
  set -- "$@" --registry-config "$registry_config"
fi
if [ "${BUNDLES_PLAIN_HTTP:-false}" = "true" ]; then
  set -- "$@" --plain-http
fi

mkdir -p "$root" || die "cannot create $root"
staging_root="$root/.bundle-fetch"
rm -rf "$staging_root"
mkdir -p "$staging_root" || die "cannot create $staging_root"
errlog="$staging_root/oras.err"

fetched=0
unsealed=0
for source in $sources; do
  # Registry names are lowercase by the OCI grammar; the on-disk source directory keeps the
  # name as configured, which is the name the publisher wrote it under on the share.
  repo_source=$(printf '%s' "$source" | tr '[:upper:]' '[:lower:]')
  case "$repo_source" in
    *[!a-z0-9._-]*|'') die "source '$source' is not a valid registry name component" ;;
  esac
  ref="$registry/plugins/$repo_source:$identity"

  if ! digest=$("$oras" resolve "$@" "$ref" 2>"$errlog"); then
    if grep -q ': not found$' "$errlog"; then
      log "unsealed: no publication of '$source' for identity $identity at $registry/plugins/$repo_source (tag absent) — nothing written; the pre-warm compiles this source"
      unsealed=$((unsealed + 1))
      continue
    fi
    printf '%s\n' "$(cat "$errlog")" >&2
    die "could not resolve $ref"
  fi
  case "$digest" in
    sha256:*) ;;
    *) die "resolve of $ref answered '$digest', not a digest" ;;
  esac

  staging="$staging_root/$source"
  rm -rf "$staging"
  mkdir -p "$staging" || die "cannot create $staging"
  if ! "$oras" pull "$@" -o "$staging" "$registry/plugins/$repo_source@$digest" >"$errlog" 2>&1; then
    cat "$errlog" >&2
    die "pull of $registry/plugins/$repo_source@$digest failed — nothing written for '$source'"
  fi

  # The reader's own contract, applied before anything becomes visible: a publication is whole
  # when `_complete` is present and every bundle it lists landed.
  [ -f "$staging/_complete" ] || die "the publication $digest of '$source' carries no _complete — refusing to materialise it"
  missing=0
  while IFS= read -r name || [ -n "$name" ]; do
    name=$(printf '%s' "$name" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')
    [ -n "$name" ] || continue
    if [ ! -f "$staging/$name" ]; then
      log "listed bundle '$name' is absent from the pulled publication $digest of '$source'"
      missing=$((missing + 1))
    fi
  done < "$staging/_complete"
  [ "$missing" -eq 0 ] || die "$missing bundle(s) listed in _complete did not land — refusing to materialise '$source'"
  bundles=$(grep -c '[^[:space:]]' "$staging/_complete")

  dest_parent="$root/$identity"
  dest="$dest_parent/$source"
  mkdir -p "$dest_parent" || die "cannot create $dest_parent"
  rm -rf "$dest"
  mv "$staging" "$dest" || die "cannot move $staging into place at $dest"
  log "materialised '$source' for identity $identity: index $digest, $bundles bundle(s), at $dest"
  fetched=$((fetched + 1))
done

rm -rf "$staging_root"
log "done: $fetched source(s) materialised, $unsealed unsealed, under $root/$identity"
exit 0
