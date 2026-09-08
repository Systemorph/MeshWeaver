#!/usr/bin/env bash
# carry-forward-bundles.sh <bake-dir> <published-listing> <target> <identity> <source>
#
# Makes a NARROWED bake publish a COMPLETE publication.
#
# A narrowed bake (see bake-scope.sh) recompiles only the modules a change actually affects, so
# --bake-output writes bundles only for those. Publishing that directory as-is would be a delta —
# and the publication is atomic per identity: `_complete` is written LAST and lists the whole
# bundle set, and a portal seeds EXACTLY what the sentinel lists
# (ShippedPrebuiltBundles.SeedPublishedRoot). A delta publication would therefore not "add" the
# new bundles; it would REPLACE the sentinel and shrink what every portal adopts to just the
# delta, silently putting every other module back to compiling at boot. That is the reason the
# bake was full until now, and this script is what removes it.
#
# So: for every bundle the currently-sealed publication lists that this bake did NOT produce,
# download it into the bake directory. publish-bake-bundles.sh then runs COMPLETELY UNCHANGED —
# it sees the whole set as local files, uploads every one of them, and seals a sentinel listing
# all of them. The publication is indistinguishable from a full bake's; only the COMPILE was
# narrowed.
#
# 🚨 A missing carried-forward bundle is FATAL, never a shrink. If the publication lists a bundle
# that cannot be fetched, the only two options are "publish less than is published today" and
# "stop". Shrinking is the silent failure — every portal missing that bundle recompiles it at
# boot, nothing is red, and the tell is a slow start weeks later. So this exits non-zero and the
# job goes red with the bundle named; the existing publication stays sealed and intact, because
# nothing has been written yet.
#
# AUTH: `az login` must already have happened (data-plane reads use --auth-mode login
# --backup-intent, exactly as publish-bake-bundles.sh's own reads do).
set -euo pipefail

# ── self-test ────────────────────────────────────────────────────────────────────────────────
# 🚨 This script is the ONLY thing standing between a narrowed bake and a SHRUNK publication, and
# a shrunk publication is invisible: no error, no red job — just every portal quietly recompiling
# the bundles the sentinel stopped listing, weeks later, as a slow boot. So it owes proof that it
# refuses rather than shrinks.
#
#     .github/scripts/carry-forward-bundles.sh --self-test
if [ "${1:-}" = "--self-test" ]; then
  # The cases below EXPECT non-zero exits from the re-invoked script, so the harness itself
  # must not die on them; the runs under test keep `set -e` (they are separate processes).
  set +e
  ST=$(mktemp -d); trap 'rm -rf "$ST"' EXIT
  ME="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"
  mkdir -p "$ST/bin"
  # The stub models the two calls this script makes — `file download` and the `file show` whose
  # ONE query it reads back — and REFUSES anything else, so the script and the stub cannot drift
  # apart silently. Metadata lives in a sidecar named after the file.
  cat > "$ST/bin/az" <<'AZ'
#!/usr/bin/env bash
group="$2"          # file | directory
action="$3"
account=""; share=""; path=""; dest=""; query=""; name=""
while [ $# -gt 0 ]; do
  case "$1" in
    --account-name) account="$2"; shift 2;; --share-name) share="$2"; shift 2;;
    --path) path="$2"; shift 2;; --dest) dest="$2"; shift 2;; --name) name="$2"; shift 2;;
    --query) query="$2"; shift 2;; *) shift;;
  esac
done
file="$MOCK_AZ_ROOT/$account/$share/$path"
if [ "$group/$action" = "directory/exists" ]; then
  { [ -d "$MOCK_AZ_ROOT/$account/$share/$name" ] && echo true; } || echo false; exit 0
fi
case "$action" in
  exists)
    { [ -f "$file" ] && echo true; } || echo false ;;
  download)
    [ -f "$file" ] || exit 1
    mkdir -p "$(dirname "$dest")"; cp "$file" "$dest" ;;
  show)
    if [ "$query" != "[[metadata.digest || '-', metadata.publication || '-']]" ]; then
      echo "stub-az: unmodelled query '$query' — teach the stub rather than loosening it" >&2; exit 64
    fi
    [ -f "$file" ] || exit 1
    [ -f "$file.meta.unreadable" ] && exit 1
    # knack renders `[[a || '-', b || '-']]` as ONE tab-separated row, '-' where the value is
    # absent. The sidecar already holds that shape; a file with no metadata at all is '-\t-'.
    if [ -f "$file.meta" ]; then cat "$file.meta"; else printf -- '-\t-\n'; fi ;;
  *)
    echo "stub-az: unmodelled action '$action'" >&2; exit 64 ;;
esac
AZ
  chmod +x "$ST/bin/az"; PATH="$ST/bin:$PATH"; export MOCK_AZ_ROOT="$ST/remote"
  PREFIX="$ST/remote/acct/share/prebuilt-bundles/ID1/plugins"
  # Where a fixture's publication lives. The flat layout writes it AT the prefix; the generation
  # layout writes it under a token the `_current` pointer names, and this script must follow that
  # pointer or it would carry bundles forward from the compatibility copy instead.
  REMOTE="$PREFIX"
  FAILED=0
  PASSED=0
  ok()   { printf '  OK   %s\n' "$1"; PASSED=$((PASSED + 1)); }
  bad()  { printf '  FAIL %s\n' "$1"; FAILED=$((FAILED + 1)); }

  digest_of() { if command -v sha256sum > /dev/null 2>&1; then sha256sum "$1" | awk '{print $1}';
                else shasum -a 256 "$1" | awk '{print $1}'; fi; }
  stamp() {  # <bundle> <publication> — record the stamp publish-bake-bundles.sh writes
    printf '%s\t%s\n' "$(digest_of "$REMOTE/$1")" "$2" > "$REMOTE/$1.meta"
  }

  setup() {  # <listing lines…> — the sealed publication holds a bundle for each, all from ONE run
    rm -rf "$ST/bake" "$ST/remote" "$ST/listing"
    REMOTE="$PREFIX"
    mkdir -p "$ST/bake" "$REMOTE"
    for b in "$@"; do echo "published $b" > "$REMOTE/$b"; stamp "$b" "Systemorph-MeshWeaver-1-1"; done
    printf '%s\n' "$@" > "$ST/listing"
  }
  setup_generation() {  # <generation> <listing…> — the publication lives under a pointed-to token
    local gen="$1"; shift
    rm -rf "$ST/bake" "$ST/remote" "$ST/listing"
    REMOTE="$PREFIX/$gen"
    mkdir -p "$ST/bake" "$REMOTE"
    for b in "$@"; do echo "published $b" > "$REMOTE/$b"; stamp "$b" "Systemorph-MeshWeaver-7-1"; done
    printf '%s\n' "$gen" > "$PREFIX/_current"
    printf '%s\n' "$@" > "$ST/listing"
  }
  setup_unstamped() {  # a publication sealed before the stamp existed
    rm -rf "$ST/bake" "$ST/remote" "$ST/listing"
    REMOTE="$PREFIX"
    mkdir -p "$ST/bake" "$REMOTE"
    for b in "$@"; do echo "published $b" > "$REMOTE/$b"; done
    printf '%s\n' "$@" > "$ST/listing"
  }
  rebuilt() { echo "rebuilt $1" > "$ST/bake/$1"; }
  run()     { ( unset GITHUB_STEP_SUMMARY; bash "$ME" "$ST/bake" "$ST/listing" acct/share ID1 plugins 2>&1 ); }

  echo "carry-forward:"
  setup Store.zip Edu.zip Chess.zip RolePlay.zip
  rebuilt Edu.zip; rebuilt Chess.zip
  OUT=$(run); RC=$?
  if [ "$RC" -eq 0 ] && [ "$(ls "$ST/bake" | wc -l | tr -d ' ')" = "4" ] \
     && [ "$(cat "$ST/bake/Edu.zip")" = "rebuilt Edu.zip" ] \
     && [ "$(cat "$ST/bake/Store.zip")" = "published Store.zip" ]; then
    ok "the bake dir ends up holding the WHOLE published set — rebuilt bundles win, the rest are fetched"
  else
    bad "happy path (rc=$RC): $(printf '%s' "$OUT" | tail -3)"
  fi

  setup Store.zip Edu.zip
  rebuilt Edu.zip; rebuilt Brand.zip          # a NEW module the publication does not list yet
  OUT=$(run); RC=$?
  if [ "$RC" -eq 0 ] && [ -f "$ST/bake/Brand.zip" ] && [ -f "$ST/bake/Store.zip" ]; then
    ok "a NEW module's bundle is additive — the set may grow, never shrink"
  else
    bad "new-module superset (rc=$RC)"
  fi

  echo "refusals (the publication must never shrink):"
  setup Store.zip Edu.zip Chess.zip
  rebuilt Edu.zip; rm "$REMOTE/Chess.zip"     # listed, not rebuilt, not fetchable
  OUT=$(run); RC=$?
  if [ "$RC" -ne 0 ] && printf '%s' "$OUT" | grep -q "could not carry forward: Chess.zip"; then
    ok "a listed bundle that can neither be rebuilt nor fetched is FATAL, and it is NAMED"
  else
    bad "unfetchable bundle should be fatal (rc=$RC)"
  fi

  setup Store.zip; : > "$ST/listing"
  OUT=$(run); RC=$?
  [ "$RC" -ne 0 ] && ok "an empty published listing is refused (it cannot say what to carry forward)" \
                  || bad "empty listing should be fatal"

  setup Store.zip; printf '%s\n' "../escape.zip" > "$ST/listing"
  OUT=$(run); RC=$?
  [ "$RC" -ne 0 ] && ok "a listing entry that is not a plain file name is refused" \
                  || bad "path-bearing listing entry should be fatal"

  rm -rf "$ST/bake"; setup Store.zip; rm -rf "$ST/bake"
  OUT=$(run); RC=$?
  [ "$RC" -ne 0 ] && ok "a missing bake directory is refused" || bad "missing bake dir should be fatal"

  echo "the publication pointer (MeshWeaver#3461) — carry forward from the generation, not the prefix:"
  setup_generation Systemorph-MeshWeaver-7-1 Store.zip Edu.zip
  # A DECOY at the prefix under the same name and different bytes: if this script read the prefix
  # it would carry the decoy forward and the digest check would say so. Only following the pointer
  # gets the publication's own bytes.
  echo "decoy Store.zip" > "$PREFIX/Store.zip"
  rebuilt Edu.zip
  OUT=$(run); RC=$?
  if [ "$RC" -eq 0 ] && [ "$(cat "$ST/bake/Store.zip")" = "published Store.zip" ]; then
    ok "the carried bundle comes from the generation _current names, not the decoy at the prefix"
  else
    bad "pointer resolution (rc=$RC): $(printf '%s' "$OUT" | tail -3)"
  fi

  echo "refusals (the carried set must come from ONE publication — MeshWeaver#3461):"
  setup Store.zip Edu.zip Chess.zip
  rebuilt Edu.zip
  OUT=$(run); RC=$?
  if [ "$RC" -eq 0 ] && printf '%s' "$OUT" | grep -q "carry-forward consistency: 2 of 2 carried bundle(s) stamped, all from publication"; then
    ok "a settled publication carries forward, and the check SAYS how many it proved (2 of 2)"
  else
    bad "one-publication control (rc=$RC): $(printf '%s' "$OUT" | tail -3)"
  fi

  # THE DEFECT: the publication is replaced between the listing and the downloads, so the bundles
  # carried forward come from two bakes. Sealing them would claim one publication for both.
  setup Store.zip Edu.zip Chess.zip
  rebuilt Edu.zip
  echo "republished Chess.zip" > "$REMOTE/Chess.zip"; stamp Chess.zip "Systemorph-MeshWeaver.Plugins-2-1"
  OUT=$(run); RC=$?
  if [ "$RC" -ne 0 ] && printf '%s' "$OUT" | grep -q "name 2 DIFFERENT publications"; then
    ok "bundles carried from TWO publications are refused, and both are named"
  else
    bad "two-publication carry-forward should be fatal (rc=$RC): $(printf '%s' "$OUT" | tail -3)"
  fi

  setup Store.zip Edu.zip
  rebuilt Edu.zip
  # The bytes moved but the stamp did not — a replaced download, or a stamp that no longer
  # describes the file. Either way what arrived is not what the publication records.
  printf '%s\t%s\n' "0000000000000000000000000000000000000000000000000000000000000000" \
    "Systemorph-MeshWeaver-1-1" > "$REMOTE/Store.zip.meta"
  OUT=$(run); RC=$?
  if [ "$RC" -ne 0 ] && printf '%s' "$OUT" | grep -q "what arrived here hashes to"; then
    ok "a carried bundle whose bytes do not match the recorded digest is refused"
  else
    bad "digest mismatch should be fatal (rc=$RC): $(printf '%s' "$OUT" | tail -3)"
  fi

  setup Store.zip Edu.zip
  rebuilt Edu.zip; touch "$REMOTE/Store.zip.meta.unreadable"
  OUT=$(run); RC=$?
  if [ "$RC" -ne 0 ] && printf '%s' "$OUT" | grep -q "an unreadable stamp is not an absent one"; then
    ok "an unreadable stamp is refused, never read as 'no stamp' (fail closed)"
  else
    bad "unreadable metadata should be fatal (rc=$RC): $(printf '%s' "$OUT" | tail -3)"
  fi

  # ADOPTION: a publication sealed before the stamp existed. The check has no evidence, and must
  # SAY SO with numbers rather than reporting a consistency it never established.
  setup_unstamped Store.zip Edu.zip
  rebuilt Edu.zip
  OUT=$(run); RC=$?
  if [ "$RC" -eq 0 ] && printf '%s' "$OUT" | grep -q "1 of 1 carried bundle(s) carry no publication stamp"; then
    ok "an unstamped incumbent still carries forward, and the check reports that it proved NOTHING"
  else
    bad "unstamped incumbent (rc=$RC): $(printf '%s' "$OUT" | tail -3)"
  fi

  if [ "$FAILED" -gt 0 ]; then
    echo "::error title=carry-forward self-test failed::$FAILED case(s) — this is the only thing preventing a narrowed bake from shrinking the publication."
    exit 1
  fi
  echo ""
  # The denominator, computed rather than written down: a case list that silently stopped
  # executing would otherwise keep printing the number someone typed here.
  if [ "$PASSED" -lt 11 ]; then
    echo "::error title=carry-forward self-test ran too few cases::$PASSED case(s) executed, at least 11 expected — a harness that quietly tests less renders the same green tick as one that tests the lane."
    exit 1
  fi
  echo "carry-forward self-test: $PASSED case(s) — hydration, shrink refusals, and the one-publication postcondition — all green."
  exit 0
fi

USAGE="usage: carry-forward-bundles.sh <bake-dir> <published-listing> <target> <identity> <source>"
BAKE_DIR="${1:?$USAGE}"
LISTING="${2:?$USAGE}"
TARGET="${3:?$USAGE}"
IDENTITY="${4:?$USAGE}"
SOURCE="${5:?$USAGE}"

[ -d "$BAKE_DIR" ] || { echo "::error::bake dir '$BAKE_DIR' does not exist"; exit 1; }
[ -s "$LISTING" ] || { echo "::error::published listing '$LISTING' is missing or empty — a narrowed bake cannot know what to carry forward, and publishing without it would shrink the publication."; exit 1; }

ACCOUNT="${TARGET%%/*}"
REST="${TARGET#*/}"
SHARE="${REST%%/*}"
BASE=""
case "$REST" in */*) BASE="${REST#*/}";; esac

# ══════════════ THE PUBLICATION POINTER (MeshWeaver#3461) ══════════════
#
# A source directory MAY hold `_current`: one line naming the SUBDIRECTORY that holds the
# publication which currently applies. Absent, it IS its own publication directory — the flat
# layout, and the only one anything has written until a caller opts in to `publication-layout:
# generation`.
#
# 🚨 THE RULES ARE THE READER'S, EXACTLY (ShippedPrebuiltBundles.PublicationDirectoryOf, and the
# resolver in publish-bake-bundles.sh). This file, bake-scope.sh and publish-bake-bundles.sh are
# fetched at ONE `platform-ref`, so no pin can carry half of them — but they must also AGREE, or
# this lane would read what portals do not serve. Absent, blank, unreadable, not a single path
# segment, or naming a directory that is not there ⇒ the source directory. A pointer is a NAME: it
# must never be able to address bytes outside its own source directory.
POINTER="_current"
RESOLVED_DIR=""
resolve_publication_dir() { # <account> <share> <source-dir>
  _rp_account="$1"; _rp_share="$2"; _rp_source="$3"
  RESOLVED_DIR="$_rp_source"
  _rp_exists=$(az storage file exists --account-name "$_rp_account" --share-name "$_rp_share" \
    --path "$_rp_source/$POINTER" --auth-mode login --backup-intent --query exists -o tsv \
    --only-show-errors 2>/dev/null || echo "unknown")
  [ "$_rp_exists" = "true" ] || return 0
  _rp_local="$(mktemp)"
  if ! az storage file download --account-name "$_rp_account" --share-name "$_rp_share" \
      --path "$_rp_source/$POINTER" --dest "$_rp_local" \
      --auth-mode login --backup-intent --only-show-errors > /dev/null 2>&1; then
    rm -f "$_rp_local"
    return 0
  fi
  _rp_named=$(sed -e 's/[[:space:]]*$//' -e 's/^[[:space:]]*//' "$_rp_local" | grep -m1 '[^[:space:]]' || true)
  rm -f "$_rp_local"
  [ -n "$_rp_named" ] || return 0
  case "$_rp_named" in
    # A rooted name always contains a '/', so `*/*` already covers it — shellcheck SC2222 is
    # right that a separate `/*` arm can never match anything this one does not.
    .|..|*/*|*\\*)
      echo "::warning::$_rp_source/$POINTER names '$_rp_named', which is not a single directory name — reading $_rp_source as its own publication directory."
      return 0 ;;
  esac
  _rp_exists=$(az storage directory exists --account-name "$_rp_account" --share-name "$_rp_share" \
    --name "$_rp_source/$_rp_named" --auth-mode login --backup-intent --query exists -o tsv \
    --only-show-errors 2>/dev/null || echo "unknown")
  if [ "$_rp_exists" != "true" ]; then
    echo "::warning::$_rp_source/$POINTER names generation '$_rp_named', which is not on the share (exists=$_rp_exists) — reading $_rp_source as its own publication directory."
    return 0
  fi
  RESOLVED_DIR="$_rp_source/$_rp_named"
  return 0
}

SRC_PREFIX="${BASE:+$BASE/}prebuilt-bundles/$IDENTITY/$SOURCE"
# The generation the listing came from — resolved by the reader's rules, exactly as bake-scope.sh
# resolved it to take that listing.
#
# 🚨 THE POINTER CAN MOVE BETWEEN THE TWO READS, and that is already covered rather than newly
# opened: every carried bundle is verified against the digest the publication records and the
# carried set must name ONE `publication`, so a publication replaced between the listing and the
# downloads is REFUSED by name — the shell analogue of the reader's If-Match. Resolving here rather
# than being handed the directory keeps this script correct when its caller is pinned to a workflow
# copy that does not know about generations.
resolve_publication_dir "$ACCOUNT" "$SHARE" "$SRC_PREFIX"
SRC_DIR="$RESOLVED_DIR"

# 🚨 THE CARRY-FORWARD IS AN N+1 READ OF A DIRECTORY THAT CAN BE REPLACED UNDERNEATH IT
# (MeshWeaver#3461). The listing came from the sealed publication `bake-scope.sh` read; each
# download below is a separate request, and `<identity>/plugins` has several publishers — core CD's
# `plugins-bake`, the satellite's own `publish-bake`, and (measured four times in one day, more
# often than the cross-lane case) two concurrent runs of ONE of them. If the publication is
# replaced between the listing and a download, two things can happen:
#
#   a listed name has GONE          → it lands in missing[] below and this script goes RED. Loud.
#   a listed name has NEW BYTES     → today it is carried forward silently, and the publication
#                                     this bake then seals holds bundles from two generations. The
#                                     seal claims one publication for bytes that came from two.
#
# The second is the same silent mix as the two-writer case, reached by one writer alone, and
# publish-bake-bundles.sh's own postcondition cannot see it: that check asks "is the shelf what I
# uploaded", and a carried-forward mix IS what this run uploaded.
#
# So every carried file is checked against the stamps publish-bake-bundles.sh writes — the SAME
# two metadata values, not a third detector: `digest` (the bytes, so a truncated or replaced
# download is caught) and `publication` (which publication put them there, so bundles carried from
# two of them are caught). All carried files must name ONE publication.
sha256_of() { # <file> — hex digest, portable across the CI runner and a developer's macOS
  if command -v sha256sum > /dev/null 2>&1; then
    sha256sum "$1" | awk '{print $1}'
  else
    shasum -a 256 "$1" | awk '{print $1}'
  fi
}

carried=0
rebuilt=0
stamped=0
missing=()
corrupt=()
publications=""
while IFS= read -r name; do
  [ -n "$name" ] || continue
  case "$name" in */*|..|.) echo "::error::the published listing names '$name', which is not a plain file name"; exit 1;; esac
  if [ -f "$BAKE_DIR/$name" ]; then
    rebuilt=$((rebuilt + 1))
    continue
  fi
  if az storage file download --account-name "$ACCOUNT" --share-name "$SHARE" \
       --path "$SRC_DIR/$name" --dest "$BAKE_DIR/$name" \
       --auth-mode login --backup-intent --only-show-errors > /dev/null 2>&1; then
    carried=$((carried + 1))
    echo "carried forward: $name"
    # Read the stamp of the file we just took. A read that FAILS is not "no stamp" — an unreadable
    # answer and a permissive one must never be the same value, so it is recorded as corrupt.
    if props=$(az storage file show --account-name "$ACCOUNT" --share-name "$SHARE" \
         --path "$SRC_DIR/$name" --auth-mode login --backup-intent \
         --query "[[metadata.digest || '-', metadata.publication || '-']]" -o tsv --only-show-errors); then
      # 🚨 The query is a list-of-ONE-ROW and the fields carry a '-' guard, both for the reason
      # publish-bake-bundles.sh sets out at length: knack's format_tsv renders a top-level list as
      # ROWS (so `[a, b]` prints two LINES, and `$2` reads empty for every file), and a null field
      # renders as the literal string 'None'. Measured against azure-cli 2.90.0. The field count is
      # asserted so a rendering change is a refusal, never a wrong answer.
      fields=$(printf '%s' "$props" | awk -F'\t' 'NR == 1 { print NF }')
      if [ "${fields:-0}" -ne 2 ]; then
        corrupt+=("$name (az answered '$props' — one tab-separated row of TWO fields was expected; the CLI's --query rendering has changed)")
        continue
      fi
      remote_digest=$(printf '%s' "$props" | awk -F'\t' 'NR == 1 { print $1 }')
      remote_pub=$(printf '%s' "$props" | awk -F'\t' 'NR == 1 { print $2 }')
      if [ "$remote_digest" = "-" ]; then remote_digest=""; fi
      if [ "$remote_pub" = "-" ]; then remote_pub=""; fi
    else
      corrupt+=("$name (its metadata could not be read — an unreadable stamp is not an absent one)")
      continue
    fi
    if [ -n "$remote_digest" ]; then
      local_digest=$(sha256_of "$BAKE_DIR/$name")
      if [ "$local_digest" != "$remote_digest" ]; then
        corrupt+=("$name (the publication says $remote_digest, what arrived here hashes to $local_digest)")
        continue
      fi
    fi
    if [ -n "$remote_pub" ]; then
      stamped=$((stamped + 1))
      case " $publications " in *" $remote_pub "*) ;; *) publications="$publications $remote_pub";; esac
    fi
  else
    missing+=("$name")
  fi
done < "$LISTING"

if [ "${#missing[@]}" -gt 0 ]; then
  echo "::error title=Carry-forward failed — refusing to shrink the publication::the sealed publication under $ACCOUNT/$SHARE/$SRC_DIR lists ${#missing[@]} bundle(s) this narrowed bake neither rebuilt nor could fetch. Publishing now would replace the _complete sentinel with a SHORTER list and every portal would silently stop adopting them."
  for m in "${missing[@]}"; do echo "::error::could not carry forward: $m"; done
  echo "::error::Re-run this workflow to take the full-bake path (bake-scope.sh falls back to a full bake whenever the publication cannot be read), or fix access to the target."
  exit 1
fi

if [ "${#corrupt[@]}" -gt 0 ]; then
  echo "::error title=Carry-forward failed — a carried bundle is not the one the publication holds::${#corrupt[@]} of $carried carried file(s) could not be shown to be the bytes the sealed publication under $ACCOUNT/$SHARE/$SRC_DIR records. Publishing them would seal a bundle set nobody produced."
  for c in "${corrupt[@]}"; do echo "::error::$c"; done
  exit 1
fi

# ONE publication, or none at all. Counting the distinct stamps is what tells "carried from the
# publication the listing came from" apart from "carried from two, because it was replaced while
# this ran" — the second is the silent mix, and it is refused.
pubcount=$(printf '%s' "$publications" | tr ' ' '\n' | grep -c '[^[:space:]]' || true)
if [ "$pubcount" -gt 1 ]; then
  echo "::error title=Carry-forward failed — the publication was replaced while this read it::$carried bundle(s) were carried forward from $ACCOUNT/$SHARE/$SRC_DIR and they name $pubcount DIFFERENT publications:$publications. The sealed publication was replaced between the listing this run was given and the downloads it made, so the set about to be sealed would hold bundles from two bakes under one sentinel (MeshWeaver#3461) — one seal, one generation, self-consistent to every consumer and wrong. Re-run this workflow once the publisher has settled; nothing has been written."
  exit 1
fi
# 🚨 The denominator, printed on EVERY path — including the one that proves nothing. A publication
# sealed before publish-bake-bundles.sh stamped its files carries no `publication` metadata at all,
# and this check then has no evidence either way. It says so, loudly and with numbers, rather than
# reporting a consistency it did not establish; the next publication of this source stamps every
# file and the check becomes enforceable with no action from anyone.
if [ "$carried" -gt 0 ] && [ "$stamped" -eq 0 ]; then
  echo "::warning title=Carry-forward consistency NOT established::$carried of $carried carried bundle(s) carry no publication stamp — the sealed publication under $ACCOUNT/$SHARE/$SRC_DIR predates the stamp publish-bake-bundles.sh now writes (MeshWeaver#3461). Whether they all came from ONE publication could not be checked. The next publication of source '$SOURCE' stamps them and this becomes enforceable."
elif [ "$carried" -gt 0 ]; then
  echo "carry-forward consistency: $stamped of $carried carried bundle(s) stamped, all from publication$publications"
fi

# The postcondition, asserted rather than assumed: the set about to be published is a SUPERSET of
# what is published today. A narrowed bake may legitimately ADD bundles (a new module) — it may
# never publish fewer than the sentinel it is about to replace.
listed=$(grep -c '[^[:space:]]' "$LISTING" || true)
present=$(find "$BAKE_DIR" -maxdepth 1 -name '*.zip' | wc -l | tr -d ' ')
if [ "$present" -lt "$listed" ]; then
  echo "::error::after carry-forward the bake dir holds $present bundle(s) but the publication lists $listed — refusing to publish a shorter set."
  exit 1
fi
echo "carry-forward complete: $rebuilt rebuilt, $carried carried forward, $present bundle(s) to publish (publication lists $listed)."
if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  printf -- '- carried forward **%s** unchanged bundle(s); **%s** rebuilt; publishing **%s**\n' \
    "$carried" "$rebuilt" "$present" >> "$GITHUB_STEP_SUMMARY"
fi
