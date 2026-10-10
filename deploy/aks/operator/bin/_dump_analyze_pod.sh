# shellcheck shell=sh
# The IN-POD half of hosting-dump-analyze — NOT run by the operator. hosting-dump-analyze reads this
# file and hands it to the analysis Job as `sh -c`, in the instance's namespace, on the analyser image
# (Debian, GNU coreutils/findutils), with the dump claim mounted READ-ONLY at $MW_ROOT.
#
# It prints ONLY sentinel lines (`@@MW-DUMP <word>\t…`) and the text of a FIXED set of dotnet-dump
# commands; hosting-dump-analyze parses nothing else:
#
#   @@MW-DUMP listing                         then one `<bytes>\t<mtime epoch>\t<relative path>` per dump
#   @@MW-DUMP selected\t<rel>\t<bytes>\t<mtime epoch>
#   @@MW-DUMP section\t<name>\t<command>      then that command's output (at most $MW_SECTION_BYTES)
#   @@MW-DUMP end\t<name>\t<exit>\t<bytes the command printed in all>
#   @@MW-DUMP done                            the analysis reached its end
#   @@MW-DUMP refused\t<sentence>             it refused, and why — then exit non-zero
#
# 🚨 The selected path is resolved and must lie UNDER the mount: a symlink (file or directory) cannot
# walk the read out of the dump volume. The operator validated the selector's shape before the Job
# existed; this is the second half, where the filesystem is.
#
# Inputs (env): MW_DUMP | MW_POD | MW_AROUND (at most one), MW_DOTNET_DUMP_VERSION, MW_CMD_TIMEOUT,
# MW_SECTION_BYTES; MW_ROOT (default /dumps) and MW_TOOLS (default /tmp/dd) exist for the test.
set -u
root="${MW_ROOT:-/dumps}"
tools="${MW_TOOLS:-/tmp/dd}"
tab="$(printf '\t')"
emit() { printf '@@MW-DUMP %s\n' "$*"; }
refuse() { emit "refused${tab}$*"; exit 3; }

rootreal="$(realpath -e "$root")" || refuse "the dump root is not mounted"
emit "listing"
find "$rootreal" -mindepth 2 -maxdepth 2 -type f -name 'coredump.*' -printf '%s\t%Ts\t%P\n' | sort -t "$tab" -k2,2nr -k3,3

# The newest dump matching extra find predicates, relative to the root.
newest() {
  find "$rootreal" -mindepth 2 -maxdepth 2 -type f -name 'coredump.*' "$@" -printf '%Ts\t%P\n' \
    | sort -t "$tab" -k1,1nr | head -1 | cut -f2
}

rel=""
if [ -n "${MW_DUMP:-}" ]; then
  rel="$MW_DUMP"
elif [ -n "${MW_POD:-}" ]; then
  # A pod's live directory and its parked ones (`<pod>.<epoch>`, crash-dump-retention.sh step 0).
  rel="$(newest \( -path "$rootreal/$MW_POD/*" -o -path "$rootreal/$MW_POD.*/*" \))"
  [ -n "$rel" ] || refuse "pod $MW_POD left no dump on the volume"
elif [ -n "${MW_AROUND:-}" ]; then
  at="$(date -u -d "$MW_AROUND" +%s)" || refuse "cannot read the instant $MW_AROUND"
  best="" bestd=""
  # createdump names the file coredump.<pid>.<epoch> (DOTNET_DbgMiniDumpName …/coredump.%p.%t).
  candidates="$(find "$rootreal" -mindepth 2 -maxdepth 2 -type f -name 'coredump.*.*' -printf '%P\n' | sort)"
  while IFS= read -r f; do
    t="${f##*.}"
    case "$t" in ''|*[!0-9]*) continue ;; esac
    d=$((t - at)); [ "$d" -lt 0 ] && d=$((0 - d))
    if [ "$d" -le 1800 ] && { [ -z "$bestd" ] || [ "$d" -lt "$bestd" ]; }; then best="$f"; bestd="$d"; fi
  done <<EOF
$candidates
EOF
  [ -n "$best" ] || refuse "no dump on the volume was written within 30 minutes of $MW_AROUND"
  rel="$best"
else
  rel="$(newest)"
  [ -n "$rel" ] || refuse "the dump volume holds no dump"
fi

file="$rootreal/$rel"
{ [ -f "$file" ] && [ ! -L "$file" ]; } || refuse "$rel is not a dump file on the dump volume"
real="$(realpath -e "$file")" || refuse "$rel does not resolve"
case "$real" in "$rootreal"/*) ;; *) refuse "$rel resolves outside the dump volume" ;; esac
emit "selected${tab}${rel}${tab}$(stat -c %s "$real")${tab}$(stat -c %Y "$real")"

export HOME=/tmp DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet tool install --tool-path "$tools" dotnet-dump --version "$MW_DOTNET_DUMP_VERSION" >/tmp/mw-dd-install.log 2>&1 \
  || refuse "could not install dotnet-dump $MW_DOTNET_DUMP_VERSION from nuget.org: $(tail -3 /tmp/mw-dd-install.log | tr '\n' ' ')"

section() {
  name="$1"; shift
  emit "section${tab}${name}${tab}$*"
  timeout "$MW_CMD_TIMEOUT" "$tools/dotnet-dump" analyze "$real" -c "setsymbolserver -ms" -c "$*" -c exit </dev/null >/tmp/mw-dd-out.txt 2>&1
  rc=$?
  head -c "$MW_SECTION_BYTES" /tmp/mw-dd-out.txt
  printf '\n'
  emit "end${tab}${name}${tab}${rc}${tab}$(stat -c %s /tmp/mw-dd-out.txt)"
}

# FIXED. None of these prints an object's fields, a string's value or the environment.
section eeversion eeversion
section threadpool threadpool
section syncblk syncblk
section clrthreads clrthreads
section clrstack clrstack -all
section dumpasync dumpasync --coalesce
emit "done"
