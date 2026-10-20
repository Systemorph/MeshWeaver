#!/bin/sh
# artifact-store-sync — ONE-WAY replication of sealed publications from an upstream registry into
# this environment's artifact store (Doc/Architecture/SharedArtifactStore, increment 4).
#
# Runs as the CronJob the chart renders on a client environment's CONTROL instance — the store's
# owner — when its record declares `artifactStore.upstream`. Pull-based and one-way: it READS the
# upstream's registry with this environment's own pull credential and needs no access of any kind
# to the upstream's store. For every identity × source it:
#
#   1. resolves `<registry>/plugins/<source>:<identity>` — the publication's OCI index — to a digest
#      (the layout every bundle fetch already reads: Doc/Architecture/PluginBundlesInTheRegistry);
#   2. names the generation by that digest (`upstream-<24 hex>`), so a publication that has not
#      changed upstream is a no-op here: its generation is already sealed and current;
#   3. pulls the digest's whole graph into a staging directory on the store's volume
#      (<root>/.artifact-store-staging/), never into a source directory;
#   4. hands the staged publication to artifact-store-publish.sh, which checks it whole, renames it
#      into place as its own generation, and moves `_current` LAST — so a portal of this
#      environment never reads a half-copied publication.
#
# Outcomes:
#   * the tag is absent upstream → UNSEALED there: logged, nothing written, the run continues;
#   * anything else fails (a denied pull, a torn publication) → that source is refused, the run
#     continues with the next, and the job exits 1 at the end so the CronJob's history says so.
#     The store keeps serving the generation that was current — a failed sync never removes one.
#   * nothing is ever deleted: retention is the store owner's prune job.
#
# Environment (rendered by the chart from `artifactStore.upstream`):
#   SYNC_REGISTRY         the upstream registry host                               required
#   SYNC_SOURCES          space-separated source names                             required
#   SYNC_ROOT             the store's per-architecture bundle root
#                         (<mount>/<rid>/prebuilt-bundles)                          required
#   SYNC_IDENTITIES       space-separated framework identities, beyond the file's  optional
#   SYNC_IDENTITY_FILE    a file whose first word is the control image's identity  optional
#   SYNC_REGISTRY_CONFIG  a docker config.json holding the pull credential         optional
#   SYNC_PLAIN_HTTP       "true" for a registry without TLS (tests only)           optional
#   PUBLISH               the publisher            default: artifact-store-publish.sh beside this file
#   ORAS                  the oras binary          default: /bin/oras
set -u

log() { printf 'artifact-store-sync: %s\n' "$*"; }
die() { printf 'artifact-store-sync: ERROR: %s\n' "$*" >&2; exit 1; }

registry="${SYNC_REGISTRY:-}"
sources="${SYNC_SOURCES:-}"
root="${SYNC_ROOT:-}"
identities="${SYNC_IDENTITIES:-}"
identity_file="${SYNC_IDENTITY_FILE:-}"
registry_config="${SYNC_REGISTRY_CONFIG:-}"
oras="${ORAS:-/bin/oras}"
publish="${PUBLISH:-$(dirname "$0")/artifact-store-publish.sh}"

[ -n "$registry" ] || die "SYNC_REGISTRY is not set"
[ -n "$sources" ] || die "SYNC_SOURCES is not set"
[ -n "$root" ] || die "SYNC_ROOT is not set"
command -v "$oras" >/dev/null 2>&1 || die "oras is not available at '$oras'"
[ -r "$publish" ] || die "the publisher is not readable at '$publish'"

if [ -n "$identity_file" ]; then
  [ -r "$identity_file" ] || die "SYNC_IDENTITY_FILE '$identity_file' is not readable"
  own=$(sed -n 's/^[[:space:]]*\([^[:space:]]\{1,\}\).*/\1/p' "$identity_file" | head -n 1)
  [ -n "$own" ] || die "SYNC_IDENTITY_FILE '$identity_file' holds no identity"
  identities="$own $identities"
fi
# De-duplicated, order kept: the image's own identity first.
unique=""
for id in $identities; do
  case " $unique " in *" $id "*) ;; *) unique="$unique $id" ;; esac
done
identities="${unique# }"
[ -n "$identities" ] || die "no identity to replicate — set SYNC_IDENTITY_FILE (the control image's) and/or SYNC_IDENTITIES"

set --
if [ -n "$registry_config" ]; then
  [ -r "$registry_config" ] || die "SYNC_REGISTRY_CONFIG '$registry_config' is not readable"
  set -- "$@" --registry-config "$registry_config"
fi
if [ "${SYNC_PLAIN_HTTP:-false}" = "true" ]; then
  set -- "$@" --plain-http
fi

mkdir -p "$root" || die "cannot create $root"
errlog=$(mktemp) || die "cannot create a temporary file"
trap 'rm -f "$errlog"' EXIT

published=0; current=0; unsealed=0; refused=0
for identity in $identities; do
  case "$identity" in
    *[!A-Za-z0-9]*|'') log "identity '$identity' is not letters and digits — skipped"; refused=$((refused + 1)); continue ;;
  esac
  for source in $sources; do
    repo_source=$(printf '%s' "$source" | tr '[:upper:]' '[:lower:]')
    case "$repo_source" in
      *[!a-z0-9._-]*|'') log "source '$source' is not a registry name component — skipped"; refused=$((refused + 1)); continue ;;
    esac
    ref="$registry/plugins/$repo_source:$identity"
    if ! digest=$("$oras" resolve "$@" "$ref" 2>"$errlog"); then
      if grep -q ': not found$' "$errlog"; then
        log "unsealed upstream: no publication of '$source' for $identity at $registry (tag absent) — nothing written"
        unsealed=$((unsealed + 1))
        continue
      fi
      cat "$errlog" >&2
      log "REFUSED '$source' for $identity: could not resolve $ref — the store keeps what it had"
      refused=$((refused + 1))
      continue
    fi
    case "$digest" in
      sha256:*) ;;
      *) log "REFUSED '$source' for $identity: resolve answered '$digest', not a digest"; refused=$((refused + 1)); continue ;;
    esac
    hex=$(printf '%s' "${digest#sha256:}" | cut -c1-24)
    generation="upstream-$hex"
    source_dir="$root/$identity/$source"
    if [ -f "$source_dir/$generation/_complete" ] \
       && [ "$(sed -n 's/^[[:space:]]*\([^[:space:]]\{1,\}\).*/\1/p' "$source_dir/_current" 2>/dev/null | head -n 1)" = "$generation" ]; then
      current=$((current + 1))
      continue
    fi
    # Beside the identities, never inside one: a reader enumerating <identity>/ would read a
    # staging directory there as a source. Same volume, so the publisher's move is one rename.
    staging="$root/.artifact-store-staging/sync-$identity-$source-$$"
    rm -rf "$staging"
    mkdir -p "$staging" || die "cannot create $staging"
    if ! "$oras" pull "$@" -o "$staging" "$registry/plugins/$repo_source@$digest" >"$errlog" 2>&1; then
      cat "$errlog" >&2
      rm -rf "$staging"
      log "REFUSED '$source' for $identity: pull of $digest failed — nothing written"
      refused=$((refused + 1))
      continue
    fi
    if sh "$publish" "$staging" "$root" "$identity" "$source" "$generation"; then
      published=$((published + 1))
      log "replicated '$source' for $identity: $digest → $generation"
    else
      refused=$((refused + 1))
    fi
    rm -rf "$staging"
  done
done

log "done: $published replicated, $current already current, $unsealed unsealed upstream, $refused refused — under $root"
[ "$refused" -eq 0 ] || exit 1
exit 0
