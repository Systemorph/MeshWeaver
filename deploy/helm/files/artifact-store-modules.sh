#!/bin/sh
# artifact-store-modules — composes the MODULE SURFACE a local-source build compiles against, from the
# store itself (Doc/Architecture/SharedArtifactStore, increment 5).
#
# Runs as the init container of each `artifact-build-<source>` CronJob, in the ORAS image (Alpine +
# busybox: POSIX sh, `unzip`, `sha256sum`), before the build container. For every module source, in
# the order given, it reads the CURRENT generation the source's `_current` names for this identity
# (the flat layout when there is no pointer), and for every `modules/*.module.nupkg` its
# `modules/_index` lists — the first source listing a package winning — it unpacks the bundle and lays
# the module out as `<out>/modules/<Name>/` with its entry assembly, its closure, its static assets
# and its native payloads: the layout `--module <out>/modules/<Name>/<Name>.dll` expects (the tester's
# compile loads an entry ASSEMBLY, never the archive) and the one node-repo-publish-bake composes.
#
# It writes two files for the build:
#   <out>/modules.list    one entry DLL path per line — the `--module` arguments
#   <out>/inputs          `<source>/<generation>/<bundle> <sha256>` per composed bundle — part of the
#                         compiler-input fingerprint the build names its generation by, so a changed
#                         upstream module set produces a NEW generation instead of reusing the old one
#
# Fails closed: a listed bundle the store does not hold, or one whose entry assembly cannot be
# identified, is exit 1 — a build against a silently short module surface would compile and publish
# bundles bound to modules nobody runs.
#
# Environment:
#   MODULES_ROOT        the store's per-architecture bundle root                         required
#   MODULES_SOURCES     space-separated sources whose sealed module sets are composed    optional
#   MODULES_IDENTITY_FILE  a file whose first word is the portal image's identity        required
#   MODULES_OUT         the output directory                                            required
set -u

log() { printf 'artifact-store-modules: %s\n' "$*"; }
die() { printf 'artifact-store-modules: ERROR: %s\n' "$*" >&2; exit 1; }

root="${MODULES_ROOT:-}"
sources="${MODULES_SOURCES:-}"
identity_file="${MODULES_IDENTITY_FILE:-}"
out="${MODULES_OUT:-}"
[ -n "$root" ] || die "MODULES_ROOT is not set"
[ -n "$out" ] || die "MODULES_OUT is not set"
[ -r "$identity_file" ] || die "MODULES_IDENTITY_FILE '$identity_file' is not readable"
identity=$(sed -n 's/^[[:space:]]*\([^[:space:]]\{1,\}\).*/\1/p' "$identity_file" | head -n 1)
case "$identity" in *[!A-Za-z0-9]*|'') die "the identity file '$identity_file' holds no identity" ;; esac

if command -v sha256sum >/dev/null 2>&1; then digest() { sha256sum "$1" | cut -d' ' -f1; }
else digest() { shasum -a 256 "$1" | cut -d' ' -f1; }; fi

mkdir -p "$out/modules" || die "cannot create $out/modules"
: > "$out/modules.list"
: > "$out/inputs"
seen=" "
composed=0
for source in $sources; do
  case "$source" in *[!a-z0-9._-]*|'') die "module source '$source' is not a registry name component" ;; esac
  dir="$root/$identity/$source"
  if [ ! -d "$dir" ]; then
    log "'$source' holds no publication for $identity — no modules from it"
    continue
  fi
  generation=$(sed -n 's/^[[:space:]]*\([^[:space:]]\{1,\}\).*/\1/p' "$dir/_current" 2>/dev/null | head -n 1)
  [ -n "$generation" ] && dir="$dir/$generation"
  if [ ! -f "$dir/_complete" ]; then
    log "'$source' is not sealed for $identity — no modules from it"
    continue
  fi
  [ -f "$dir/modules/_index" ] || continue
  while IFS= read -r bundle || [ -n "$bundle" ]; do
    bundle=$(printf '%s' "$bundle" | tr -d '[:space:]')
    [ -n "$bundle" ] || continue
    case "$bundle" in */*|..*) die "'$source' lists module bundle '$bundle', which escapes modules/" ;; esac
    key=$(printf '%s' "$bundle" | tr '[:upper:]' '[:lower:]')
    case "$seen" in *" $key "*) continue ;; esac
    [ -f "$dir/modules/$bundle" ] || die "'$source' lists module $bundle for $identity but the store does not hold it — the seal and the store disagree"
    seen="$seen$key "
    unpack="$out/.unpack-$composed"
    rm -rf "$unpack"; mkdir -p "$unpack"
    unzip -o -q "$dir/modules/$bundle" -d "$unpack" || die "cannot unpack $bundle from '$source'"
    name=""
    if [ -f "$unpack/meshweaver/manifest.json" ]; then
      # The module object's assemblyName (`.module.assemblyName`, ModulePackCommand writes it).
      name=$(tr -d '\n' < "$unpack/meshweaver/manifest.json" \
        | sed -n 's/.*"module"[[:space:]]*:[[:space:]]*{[^}]*"assemblyName"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
    fi
    if [ -z "$name" ]; then
      set -- "$unpack"/meshweaver/modules/*.dll
      { [ $# -eq 1 ] && [ -f "$1" ]; } || die "$bundle: cannot identify the module's entry assembly (no manifest module.assemblyName; meshweaver/modules/ holds $# dll(s))"
      name=$(basename "$1" .dll)
    fi
    case "$name" in *[!A-Za-z0-9._-]*|'') die "$bundle names the entry assembly '$name', which is not a bare name" ;; esac
    [ -f "$unpack/meshweaver/modules/$name.dll" ] || die "$bundle: meshweaver/modules/$name.dll is missing from the bundle"
    mkdir -p "$out/modules/$name"
    cp -R "$unpack"/meshweaver/modules/. "$out/modules/$name/"
    [ -d "$unpack/meshweaver/moduleassets" ] && cp -R "$unpack"/meshweaver/moduleassets/. "$out/modules/$name/"
    [ -d "$unpack/meshweaver/modulenatives" ] && cp -R "$unpack"/meshweaver/modulenatives/. "$out/modules/$name/"
    rm -rf "$unpack"
    printf '%s\n' "$out/modules/$name/$name.dll" >> "$out/modules.list"
    printf '%s/%s/%s %s\n' "$source" "${generation:-flat}" "$bundle" "$(digest "$dir/modules/$bundle")" >> "$out/inputs"
    composed=$((composed + 1))
    log "composed $name from '$source' (${generation:-flat}/$bundle)"
  done < "$dir/modules/_index"
done
log "module surface: $composed module(s) from [${sources:-none}] for $identity"
exit 0
