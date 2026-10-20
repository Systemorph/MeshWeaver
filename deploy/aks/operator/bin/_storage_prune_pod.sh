# shellcheck shell=sh
# The IN-POD half of hosting-storage-prune — NOT run by the operator. hosting-storage-prune reads this
# file and hands it to a short-lived Job as `sh -c`, in the instance's namespace, on a busybox image,
# with the portal's DATA claim mounted at $MW_ROOT — READ-ONLY for a report, read-write only for an
# --execute run.
#
# It prints ONLY sentinel lines (`@@MW-STORE <word>\t…`); hosting-storage-prune parses nothing else:
#
#   @@MW-STORE begin\t<now epoch>\t<mode>
#   @@MW-STORE df\t<size KiB>\t<used KiB>\t<available KiB>
#   @@MW-STORE top\t<name>\t<KiB>                      every top-level entry but prebuilt-bundles
#   @@MW-STORE releases\t<markers>\t<KiB>              prebuilt-bundles/_releases
#   @@MW-STORE identity\t<id>\t<kind>\t<mtime>\t<KiB>  kind: compat | legacy | other (KiB of a compat
#                                                      identity is 0 here — its generations carry it)
#   @@MW-STORE generation\t<identity>\t<source>\t<generation>\t<state>\t<mtime>\t<KiB>\t<locked>
#                                                      state: current | sealed | unsealed
#   @@MW-STORE flat\t<identity>\t<source>\t<sealed 0|1>\t<mtime>\t<KiB>\t<locked>
#   @@MW-STORE tmp\t<relative path>\t<KiB>\t<mtime>   an abandoned assembly-cache temp file (.tmp-*)
#   @@MW-STORE dump\t<relative path>\t<KiB>\t<mtime>  a file under the DATA share's dumps/ directory
#   @@MW-STORE deleted\t<class>\t<relative path>\t<KiB>   --execute only
#   @@MW-STORE kept\t<class>\t<relative path>\t<reason>   --execute only: a candidate the re-check spared
#   @@MW-STORE unreadable\t<relative path>\t<du|stat>  a read that failed — the report is then INCOMPLETE
#   @@MW-STORE done                                   the walk reached its end
#   @@MW-STORE refused\t<sentence>                    it refused, and why — then exit non-zero
#
# 🚨 WHAT THIS SCRIPT MAY DELETE, and it is deliberately almost nothing (Doc/Architecture/StoragePrune):
#   • `cache-tmp`  — assembly-cache/**/.tmp-* older than MW_TMP_AGE_DAYS (default 1): an interrupted
#                    write of the content-addressed assembly store. No reader resolves a temp name.
#   • `misplaced-dump` — files under the DATA share's own dumps/ directory, older than
#                    MW_DUMP_AGE_DAYS (default 7), and ONLY when the operator established that the
#                    portal mounts a SEPARATE dump volume over /data/dumps (MW_DUMPS_SHADOWED=1). Then
#                    nothing in the portal can see these files at all: they are bytes written before
#                    the dump volume existed, hidden under its mount point.
# It NEVER deletes anything under prebuilt-bundles/. Those are release artifacts, governed by
# Doc/Architecture/ReleasedArtifactRetention: deleting one needs the complete fleet consumer inventory
# and the publication lease, and neither is available to a pod. The bundle store is REPORTED here —
# every identity and generation, with its age, size, seal and lock — so the control instance can
# classify it; it is never written.
#
# Every deletion is re-checked at the moment it happens: the path is resolved and must lie UNDER the
# mount (a symlink cannot walk out), it must still be a regular file, and its age is re-read. A file
# that got younger, moved, or became a link is KEPT and reported as kept.
#
# Inputs (env): MW_MODE (report|execute), MW_DUMPS_SHADOWED (0|1), MW_TMP_AGE_DAYS, MW_DUMP_AGE_DAYS;
# MW_ROOT (default /store) and MW_NOW (default: the clock) exist for the test.
set -u
root="${MW_ROOT:-/store}"
mode="${MW_MODE:-report}"
now="${MW_NOW:-$(date +%s)}"
tmp_age_days="${MW_TMP_AGE_DAYS:-1}"
dump_age_days="${MW_DUMP_AGE_DAYS:-7}"
dumps_shadowed="${MW_DUMPS_SHADOWED:-0}"
tab="$(printf '\t')"
emit() { printf '@@MW-STORE %s\n' "$*"; }
refuse() { emit "refused${tab}$*"; exit 3; }

case "$mode" in report|execute) ;; *) refuse "unknown mode '$mode'" ;; esac
for n in "$now" "$tmp_age_days" "$dump_age_days"; do
  case "$n" in ''|*[!0-9]*) refuse "a numeric setting ('$n') is not a whole number" ;; esac
done
# A day is the smallest age that makes anything a candidate: a zero window would delete a temp file a
# compile is writing right now.
[ "$tmp_age_days" -ge 1 ] || refuse "MW_TMP_AGE_DAYS must be at least 1"
[ "$dump_age_days" -ge 1 ] || refuse "MW_DUMP_AGE_DAYS must be at least 1"

rootreal="$(cd "$root" 2>/dev/null && pwd -P)" || refuse "the data root $root is not mounted"
[ -d "$rootreal" ] || refuse "the data root $root is not a directory"
# An empty mount is a claim that did not attach (or the wrong claim), never a clean volume: refusing
# keeps "nothing to prune" from being reported over a share nobody looked at.
[ -n "$(ls -A "$rootreal" 2>/dev/null)" ] || refuse "the data root $root is EMPTY — the claim did not attach, or it is not the portal's data claim"

emit "begin${tab}${now}${tab}${mode}"

# shellcheck disable=SC2046  # df's columns, split on purpose
set -- $(df -Pk "$rootreal" | tail -1)
emit "df${tab}${2:-0}${tab}${3:-0}${tab}${4:-0}"

# fd 3 is the log itself: kib/mtime run inside $(…), whose stdout is the VALUE, so their report of a
# failed read goes to fd 3, which still points at the pod's stdout.
exec 3>&1
incomplete="$(mktemp)"; rm -f "$incomplete"   # exists ⇔ some read failed
# A read that fails is NEVER a zero: it is reported as `unreadable` (and the size printed is what du
# managed to count), so the operator can mark the whole report incomplete — an incomplete walk licenses
# no deletion and must not read as a clean one.
kib() {
  _out="$(du -sk "$1" 2>/dev/null)" || { emit "unreadable${tab}${1#"$rootreal"/}${tab}du" >&3; : > "$incomplete"; }
  _k="$(printf '%s' "$_out" | cut -f1)"
  echo "${_k:-0}"
}
mtime() {
  stat -c %Y "$1" 2>/dev/null && return 0
  emit "unreadable${tab}${1#"$rootreal"/}${tab}stat" >&3
  : > "$incomplete"
  echo 0
}
# The newest mtime of a directory and its immediate children: a directory's own stamp on an SMB share
# moves only when an entry is added or removed, so a generation still being written could read older
# than it is from the directory alone.
newest() {
  best=0
  for f in "$1" "$1"/* "$1"/.[!.]*; do
    [ -e "$f" ] || continue
    t="$(mtime "$f")"
    [ "$t" -gt "$best" ] && best="$t"
  done
  echo "$best"
}

for entry in "$rootreal"/* "$rootreal"/.[!.]*; do
  [ -e "$entry" ] || continue
  name="${entry##*/}"
  [ "$name" = "prebuilt-bundles" ] && continue
  case "$name" in linux-x64|linux-arm64|linux-musl-x64|linux-musl-arm64|osx-arm64|osx-x64|win-x64|win-arm64)
    [ -d "$entry/prebuilt-bundles" ] && continue ;; esac
  emit "top${tab}${name}${tab}$(kib "$entry")"
done

# The sources of one identity. `_prefix` is unused today and kept empty; the lease file sits beside
# the source directories it guards.
walk_sources() {
  _idpath="$1" _level="$2" _prefix="$3"
  for srcpath in "$_level"/*; do
    [ -d "$srcpath" ] || continue
    src="${srcpath##*/}"
    locked=0
    [ -e "$_level/_publishing.$src" ] && locked=1
    if [ -f "$srcpath/_current" ]; then
      current="$(head -1 "$srcpath/_current" | tr -d '\r\n ')"
      for genpath in "$srcpath"/*; do
        [ -d "$genpath" ] || continue
        gen="${genpath##*/}"
        if [ "$gen" = "$current" ]; then state=current
        elif [ -e "$genpath/_complete" ]; then state=sealed
        else state=unsealed; fi
        size=0
        # A legacy identity was already measured whole; its generations are reported for their
        # state only, so the walk does not read the same bytes twice.
        [ "$kind" = legacy ] || size="$(kib "$genpath")"
        emit "generation${tab}${id}${tab}${_prefix}${src}${tab}${gen}${tab}${state}${tab}$(newest "$genpath")${tab}${size}${tab}${locked}"
      done
    elif [ "$kind" = compat ]; then
      sealed=0; [ -e "$srcpath/_complete" ] && sealed=1
      emit "flat${tab}${id}${tab}${_prefix}${src}${tab}${sealed}${tab}$(newest "$srcpath")${tab}$(kib "$srcpath")${tab}${locked}"
    fi
  done
}

# The bundle stores under the root. A per-portal data volume has ONE, `prebuilt-bundles/`. The shared
# artifact store (Doc/Architecture/SharedArtifactStore) has one PER ARCHITECTURE, `<rid>/prebuilt-bundles/`
# (linux-x64, osx-arm64, …): a compatibility key names no architecture, so each runtime gets its own
# store and a reader resolves its own. Identities of a RID store are reported as `<rid>/<identity>`.
walk_store() {
  store="$1" idprefix="$2"
  for idpath in "$store"/*; do
    [ -e "$idpath" ] || continue
    base="${idpath##*/}"
    id="${idprefix}${base}"
    if [ "$base" = "_releases" ]; then
      emit "releases${tab}$(ls "$idpath" 2>/dev/null | wc -l | tr -d ' ')${tab}$(kib "$idpath")"
      continue
    fi
    [ -d "$idpath" ] || { emit "identity${tab}${id}${tab}other${tab}$(mtime "$idpath")${tab}$(kib "$idpath")"; continue; }
    case "$base" in
      c[0-9][0-9][0-9]e[0-9][0-9][0-9]) kind=compat ;;
      s[0-9a-f]*|g[0-9a-f]*) kind=legacy ;;
      *) kind=other ;;
    esac
    if [ "$kind" = compat ]; then
      emit "identity${tab}${id}${tab}${kind}${tab}$(mtime "$idpath")${tab}0"
    else
      emit "identity${tab}${id}${tab}${kind}${tab}$(newest "$idpath")${tab}$(kib "$idpath")"
    fi
    walk_sources "$idpath" "$idpath" ""
  done
}
[ -d "$rootreal/prebuilt-bundles" ] && walk_store "$rootreal/prebuilt-bundles" ""
for ridpath in "$rootreal"/*; do
  rid="${ridpath##*/}"
  case "$rid" in
    linux-x64|linux-arm64|linux-musl-x64|linux-musl-arm64|osx-arm64|osx-x64|win-x64|win-arm64)
      [ -d "$ridpath/prebuilt-bundles" ] && walk_store "$ridpath/prebuilt-bundles" "$rid/" ;;
  esac
done

# ── the two classes this script may delete ─────────────────────────────────────────────────────
tmp_cut=$((now - tmp_age_days * 86400))
dump_cut=$((now - dump_age_days * 86400))

# A candidate list, one `<class>\t<relative path>` per line; reported in both modes.
cands="$(mktemp)"
trap 'rm -f "$cands" "$incomplete"' EXIT
# A listing that fails partway is an incomplete walk too, never a shorter one.
listerr="$(mktemp)"
trap 'rm -f "$cands" "$incomplete" "$listerr"' EXIT
if [ -d "$rootreal/assembly-cache" ]; then
  find "$rootreal/assembly-cache" -type f -name '.tmp-*' 2>"$listerr" | while IFS= read -r f; do
    t="$(mtime "$f")"
    rel="${f#"$rootreal"/}"
    emit "tmp${tab}${rel}${tab}$(kib "$f")${tab}${t}"
    [ "$t" -gt 0 ] && [ "$t" -lt "$tmp_cut" ] && printf 'cache-tmp\t%s\n' "$rel" >> "$cands"
  done
fi
[ -s "$listerr" ] && { emit "unreadable${tab}assembly-cache${tab}find"; : > "$incomplete"; }
: > "$listerr"
if [ -d "$rootreal/dumps" ]; then
  find "$rootreal/dumps" -type f 2>"$listerr" | while IFS= read -r f; do
    t="$(mtime "$f")"
    rel="${f#"$rootreal"/}"
    emit "dump${tab}${rel}${tab}$(kib "$f")${tab}${t}"
    [ "$dumps_shadowed" = 1 ] && [ "$t" -gt 0 ] && [ "$t" -lt "$dump_cut" ] && printf 'misplaced-dump\t%s\n' "$rel" >> "$cands"
  done
fi

[ -s "$listerr" ] && { emit "unreadable${tab}dumps${tab}find"; : > "$incomplete"; }

if [ "$mode" = execute ]; then
  while IFS="$tab" read -r class rel; do
    [ -n "$rel" ] || continue
    # An incomplete walk licenses no deletion — not even of the garbage classes.
    if [ -e "$incomplete" ]; then emit "kept${tab}${class}${tab}${rel}${tab}the walk was incomplete (a read failed)"; continue; fi
    f="$rootreal/$rel"
    case "$class" in
      cache-tmp)      cut="$tmp_cut";  under="$rootreal/assembly-cache/" ;;
      misplaced-dump) cut="$dump_cut"; under="$rootreal/dumps/" ;;
      *) emit "kept${tab}${class}${tab}${rel}${tab}unknown class"; continue ;;
    esac
    if [ -L "$f" ] || [ ! -f "$f" ]; then emit "kept${tab}${class}${tab}${rel}${tab}no longer a regular file"; continue; fi
    dir="$(cd "${f%/*}" 2>/dev/null && pwd -P)" || { emit "kept${tab}${class}${tab}${rel}${tab}its directory cannot be resolved"; continue; }
    case "$dir/" in "$under"*) ;; *) emit "kept${tab}${class}${tab}${rel}${tab}resolves outside ${under#"$rootreal"/}"; continue ;; esac
    case "$class" in
      cache-tmp) case "${f##*/}" in .tmp-*) ;; *) emit "kept${tab}${class}${tab}${rel}${tab}not a temp file name"; continue ;; esac ;;
      misplaced-dump) [ "$dumps_shadowed" = 1 ] || { emit "kept${tab}${class}${tab}${rel}${tab}the dumps directory is not shadowed by a dump volume"; continue; } ;;
    esac
    t="$(mtime "$f")"
    if [ "$t" -le 0 ] || [ "$t" -ge "$cut" ]; then emit "kept${tab}${class}${tab}${rel}${tab}younger than its window on re-check"; continue; fi
    size="$(kib "$f")"
    if rm -f -- "$f"; then emit "deleted${tab}${class}${tab}${rel}${tab}${size}"
    else emit "kept${tab}${class}${tab}${rel}${tab}rm failed"; fi
  done < "$cands"
  # Directories the misplaced dumps leave empty go too — never the dumps/ directory itself, which is
  # the dump volume's mount point.
  [ "$dumps_shadowed" = 1 ] && [ -d "$rootreal/dumps" ] \
    && find "$rootreal/dumps" -mindepth 1 -depth -type d -exec rmdir {} \; 2>/dev/null
fi

emit "done"
