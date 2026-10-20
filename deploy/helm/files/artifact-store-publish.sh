#!/bin/sh
# artifact-store-publish — places ONE staged, sealed publication into an artifact store as its own
# generation, and moves the source's `_current` pointer to it LAST.
#
#   artifact-store-publish.sh <staged-dir> <bundle-root> <identity> <source> <generation>
#
# The one writer the control instance's store jobs share (Doc/Architecture/SharedArtifactStore):
# the upstream sync (increment 4, artifact-store-sync.sh) and the local-source build (increment 5,
# artifact-store-build.sh). The layout is the reader contract of Sealed Publication Generations:
#
#   <bundle-root>/<identity>/<source>/_current            one line, a bare generation name
#   <bundle-root>/<identity>/<source>/<generation>/…      one publication instance
#   <bundle-root>/<identity>/<source>/<generation>/_complete   its seal
#
# THE RULES, and why:
#   * the staged directory is checked BEFORE anything becomes visible: `_complete` present, every
#     file it lists present, and every module bundle `modules/_index` lists present — a publication
#     that is not whole is refused (exit 1);
#   * a generation is moved into place with ONE rename, so a reader never resolves a half-written
#     one, and an existing SEALED generation of that name is the answer (the name is content-
#     addressed by the caller: a registry index digest, a source commit) — nothing is rewritten;
#   * an existing UNSEALED generation of that name (a run that died mid-move) is refused rather
#     than repaired: the name would assert content this run cannot vouch for. The operator removes
#     it; the retention job reports it as `unsealed`;
#   * the pointer is written to a temporary name and renamed over `_current`, strictly last, and
#     only when it names a different generation — a reader sees the previous generation (still on
#     the shelf) or the new one, never a mix;
#   * NOTHING IS DELETED. Superseded generations are the store's retention job's to collect
#     (hosting-storage-prune), under the protected-label set and the consumer inventory.
#
# Exit 0: published, or already current. Exit 1: refused, with the reason on stderr.
# POSIX sh + busybox (the ORAS image and the portal image both run it).
set -u

log() { printf 'artifact-store-publish: %s\n' "$*"; }
die() { printf 'artifact-store-publish: ERROR: %s\n' "$*" >&2; exit 1; }

[ $# -eq 5 ] || die "usage: artifact-store-publish.sh <staged-dir> <bundle-root> <identity> <source> <generation>"
staged="$1"; root="$2"; identity="$3"; source="$4"; generation="$5"

bare() {
  case "$1" in
    ''|.|..|.*|*/*|*' '*) return 1 ;;
  esac
  case "$1" in
    *[!A-Za-z0-9._-]*) return 1 ;;
  esac
  return 0
}
bare "$identity" || die "identity '$identity' is not a bare name"
bare "$source" || die "source '$source' is not a bare name"
bare "$generation" || die "generation '$generation' is not a bare name"
case "$generation" in
  _*) die "generation '$generation' starts with '_' — that namespace is the store's markers" ;;
esac
[ -d "$staged" ] || die "staged publication '$staged' is not a directory"
[ -f "$staged/_complete" ] || die "staged publication '$staged' carries no _complete — refusing to publish an unsealed publication"

missing=0
while IFS= read -r name || [ -n "$name" ]; do
  name=$(printf '%s' "$name" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')
  [ -n "$name" ] || continue
  case "$name" in
    /*|*..*) die "_complete lists '$name', which escapes the publication" ;;
  esac
  if [ ! -f "$staged/$name" ]; then
    log "listed file '$name' is absent from $staged"
    missing=$((missing + 1))
  fi
done < "$staged/_complete"
[ "$missing" -eq 0 ] || die "$missing file(s) listed in _complete are absent — refusing to publish '$source' for $identity"
# The module set the publication carries is sealed beside the bundles (modules/_index, MeshWeaver#2707)
# and `_complete` does not list it — so it is checked on its own: every indexed bundle present
# (the same rule push-bundle-publication.sh applies before it seals the registry copy).
if [ -f "$staged/modules/_index" ]; then
  while IFS= read -r name || [ -n "$name" ]; do
    name=$(printf '%s' "$name" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')
    [ -n "$name" ] || continue
    case "$name" in
      */*|..*) die "modules/_index lists '$name', which escapes modules/" ;;
    esac
    if [ ! -f "$staged/modules/$name" ]; then
      log "indexed module '$name' is absent from $staged/modules"
      missing=$((missing + 1))
    fi
  done < "$staged/modules/_index"
  [ "$missing" -eq 0 ] || die "$missing module bundle(s) listed in modules/_index are absent — refusing to publish '$source' for $identity"
fi

source_dir="$root/$identity/$source"
target="$source_dir/$generation"
mkdir -p "$source_dir" || die "cannot create $source_dir"

if [ -e "$target" ]; then
  [ -f "$target/_complete" ] || die "$target exists WITHOUT a seal — a publication of this name died mid-move; it is not repaired (the name would assert content this run cannot vouch for). Remove it, and the next run publishes."
  log "generation $generation of '$source' for $identity is already sealed in the store — nothing written"
else
  mv "$staged" "$target" || die "cannot move $staged into place at $target"
  log "generation $generation of '$source' for $identity placed at $target"
fi

current=""
if [ -f "$source_dir/_current" ]; then
  current=$(sed -n 's/^[[:space:]]*\([^[:space:]]\{1,\}\).*/\1/p' "$source_dir/_current" | head -n 1)
fi
if [ "$current" = "$generation" ]; then
  log "'$source' for $identity already points at $generation"
  exit 0
fi
tmp="$source_dir/.current.$$"
printf '%s\n' "$generation" > "$tmp" || die "cannot write $tmp"
mv -f "$tmp" "$source_dir/_current" || { rm -f "$tmp"; die "cannot move the pointer into place at $source_dir/_current"; }
log "'$source' for $identity now points at $generation (was ${current:-the flat layout or nothing})"
exit 0
