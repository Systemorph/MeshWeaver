#!/usr/bin/env bash
# Every kubectl READ in bin/ that THROWS AWAY ITS STDERR must be DECLARED, with the reason a
# refusal is safe to collapse there — or this check is red, naming the script, the verb and the
# resource.
#
# 🚨 WHY THIS EXISTS. `2>/dev/null` on a read discards the one fact that decides which sentence is
# true afterwards. A Forbidden — this operator's ClusterRole lacking the grant — then comes out as
# the thing being ABSENT, and the operator announces that a platform layer, an Ingress or a
# ConfigMap does not exist when it was merely NOT PERMITTED TO LOOK. MeshWeaver#4722 measured that
# in hosting-db-release's platform-layer preflight: "no node labelled workload=db" was what a
# refusal said, sending the reader off to provision a pool that already existed.
#
# 🚨 AND WHY IT IS A CHECK AND NOT A FIXED BUG. #4436 fixed the three probes #4722 named. It left
# two more reads in the SAME FILE and three in other commands collapsing the same two answers,
# because the discrimination was a local helper in one script and nothing compared it against its
# subject. That is the shape this repo keeps meeting — care did not catch check-rbac-coverage's two
# missing grants either, and a control did. An unlisted call here is RED; a listed call that no
# longer discards stderr is STALE and RED, so the list can only shrink by a deliberate edit.
#
# THE FIX for a red, never an allow line added to quiet it: hosting::probe in bin/_common.sh, which
# answers PRESENT / ABSENT / REFUSED and hands the caller HOSTING_PROBE_OUT and HOSTING_PROBE_ERR.
# Branch on all three. `hosting::probe … || hosting::die "…absent…"` is the defect wearing the
# fix's clothes.
#
# What it reads: every line naming `kubectl` that also discards stderr (`2>/dev/null`, or
# `>/dev/null 2>&1`). What it CANNOT read, stated so nobody takes green for more: a statement whose
# `kubectl` and whose redirect sit on DIFFERENT lines, a redirect built from a variable, and
# everything helm or az do with their own stderr. Those stay covered by review.
#
# Pure bash 3.2 + grep/sed/tr — the tools bin/ itself uses, so it runs on the macOS laptop, the
# ubuntu runner and inside the operator image (Azure Linux 3 has neither awk nor python), which is
# the run that proves the check can run where the scripts run.
set -u
HERE="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
BIN="${HOSTING_BIN:-$HERE/../bin}"
ALLOW="${HOSTING_STDERR_ALLOW:-$HERE/stderr-discarded.allow}"

if [ ! -d "$BIN" ]; then
  echo "check-stderr-discarded: ERROR: bin/ not found at ${BIN} — set HOSTING_BIN. A check whose input is absent is red, never skipped." >&2
  exit 2
fi
if [ ! -f "$ALLOW" ]; then
  echo "check-stderr-discarded: ERROR: the declaration list is not at ${ALLOW} — set HOSTING_STDERR_ALLOW. A check whose input is absent is red, never skipped." >&2
  exit 2
fi

# The declared set, as "<script> <verb> <resource>" lines; comments and blanks dropped, and the
# reason after `#` is for the reader, never matched on.
declared=""
while IFS= read -r line; do
  line="${line%%#*}"
  line="$(printf '%s' "$line" | sed 's/[[:space:]][[:space:]]*/ /g; s/^ //; s/ $//')"
  [ -n "$line" ] || continue
  declared="${declared}|${line}|"
done < "$ALLOW"

found=""; scanned=0; undeclared=0
for f in "$BIN"/hosting-* "$BIN"/_common.sh "$BIN"/run.sh; do
  [ -f "$f" ] || continue
  script="$(basename "$f")"
  while IFS= read -r line; do
    case "$line" in *2\>/dev/null*|*\>/dev/null\ 2\>\&1*) ;; *) continue ;; esac
    case "$line" in *kubectl*) ;; *) continue ;; esac
    # A comment LINE describes the defect; it does not commit it. (bin/ comments quote the old
    # shapes on purpose, so this exclusion is load-bearing, not tidiness.)
    #
    # 🚨 It must match a comment line and NOTHING ELSE. The obvious glob — `[[:space:]]*\#*` —
    # reads as "one space-class char, then anything, then a #, then anything", so it also swallows
    # every real line carrying a TRAILING comment: `live="$(kubectl … 2>/dev/null)"  # why`. That
    # is a skip-trapdoor inside the gate written to close one, and it would have been invisible —
    # a call excluded from the scan looks exactly like a call that is not there. Strip the leading
    # whitespace first, then test the FIRST character; the self-test at the bottom pins both
    # directions.
    trimmed="${line#"${line%%[![:space:]]*}"}"
    case "$trimmed" in '#'*) continue ;; esac
    while read -r verb res; do
      [ -n "$verb" ] || continue
      res="${res%%/*}"
      case "$res" in
        '$'*|'"$'*|'') res="<dynamic>" ;;
        *) res="$(printf '%s' "$res" | tr -cd 'A-Za-z0-9-')" ;;
      esac
      [ -n "$res" ] || res="<dynamic>"
      key="${script} ${verb} ${res}"
      case "$found" in *"|${key}|"*) continue ;; esac
      found="${found}|${key}|"
      scanned=$((scanned+1))
      case "$declared" in
        *"|${key}|"*) ;;
        *) echo "  UNDECLARED  ${key}"
           echo "              This read discards its stderr, so a Forbidden is indistinguishable from an absence."
           echo "              Use hosting::probe (bin/_common.sh) and branch on REFUSED, or declare it in"
           echo "              $(basename "$ALLOW") with the reason collapsing the two answers is safe here."
           undeclared=$((undeclared+1)) ;;
      esac
    done < <(printf '%s\n' "$line" \
      | grep -o 'kubectl \(-n [^ ]* \)\?\(get\|create\|delete\|patch\|apply\|label\|annotate\|scale\|logs\|rollout\|wait\|describe\|auth\) [^ ;|)]*' \
      | sed 's/^kubectl //; s/^-n [^ ]* //' \
      | while read -r v r _; do printf '%s %s\n' "$v" "$r"; done)
  done < "$f"
done

# A declaration whose call is gone is STALE: it permits something that no longer exists, and the
# next reader takes it for a statement about today's tree. Shrink-only, like every ratchet here.
stale=0
while IFS= read -r key; do
  [ -n "$key" ] || continue
  case "$found" in
    *"|${key}|"*) ;;
    *) echo "  STALE       ${key} — declared in $(basename "$ALLOW"), but bin/ has no such kubectl call discarding stderr. Delete the line."
       stale=$((stale+1)) ;;
  esac
done < <(printf '%s' "$declared" | tr '|' '\n' | grep .)

# ── the parser's own control ────────────────────────────────────────────────────────────────────
# A gate whose scan silently drops lines reports "0 undeclared" for a tree full of them, and the
# two readings are indistinguishable from outside. One case on each side of the comment test.
selftest_fail=0
_is_comment() { local l="$1" t; t="${l#"${l%%[![:space:]]*}"}"; case "$t" in '#'*) return 0 ;; *) return 1 ;; esac; }
_is_comment '# kubectl get pv 2>/dev/null'            || { echo "  SELFTEST  a bare comment line must be excluded" >&2; selftest_fail=1; }
_is_comment '   # kubectl get pv 2>/dev/null'         || { echo "  SELFTEST  an indented comment line must be excluded" >&2; selftest_fail=1; }
_is_comment '  x="$(kubectl get pv 2>/dev/null)"  # w' && { echo "  SELFTEST  a real call with a TRAILING comment must be SCANNED, not excluded" >&2; selftest_fail=1; }
_is_comment 'x="$(kubectl get pv 2>/dev/null)"'       && { echo "  SELFTEST  a plain call must be scanned" >&2; selftest_fail=1; }
[ "$selftest_fail" -eq 0 ] || { echo "check-stderr-discarded: ERROR: the parser's own control failed — its verdict below means nothing" >&2; exit 2; }

echo "check-stderr-discarded: ${scanned} kubectl read(s) discarding stderr across bin/, ${undeclared} undeclared, ${stale} stale declaration(s)"
# A zero denominator is a broken parser, not a clean tree: bin/ legitimately holds declared ones.
[ "$scanned" -gt 0 ] || { echo "check-stderr-discarded: ERROR: found no such calls at all — the parser is broken, not the scripts clean" >&2; exit 2; }
[ "$undeclared" -eq 0 ] && [ "$stale" -eq 0 ]
