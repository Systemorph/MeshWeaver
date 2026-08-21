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
  cat > "$ST/bin/az" <<'AZ'
#!/usr/bin/env bash
account=""; share=""; path=""; dest=""
while [ $# -gt 0 ]; do
  case "$1" in
    --account-name) account="$2"; shift 2;; --share-name) share="$2"; shift 2;;
    --path) path="$2"; shift 2;; --dest) dest="$2"; shift 2;; *) shift;;
  esac
done
file="$MOCK_AZ_ROOT/$account/$share/$path"
[ -f "$file" ] || exit 1
mkdir -p "$(dirname "$dest")"; cp "$file" "$dest"
AZ
  chmod +x "$ST/bin/az"; PATH="$ST/bin:$PATH"; export MOCK_AZ_ROOT="$ST/remote"
  REMOTE="$ST/remote/acct/share/prebuilt-bundles/ID1/plugins"
  FAILED=0
  ok()   { printf '  OK   %s\n' "$1"; }
  bad()  { printf '  FAIL %s\n' "$1"; FAILED=$((FAILED + 1)); }

  setup() {  # <listing lines…> — the sealed publication holds a bundle for each
    rm -rf "$ST/bake" "$ST/remote" "$ST/listing"
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

  if [ "$FAILED" -gt 0 ]; then
    echo "::error title=carry-forward self-test failed::$FAILED case(s) — this is the only thing preventing a narrowed bake from shrinking the publication."
    exit 1
  fi
  echo ""
  echo "carry-forward self-test: 2 hydration cases, 4 refusals — all green."
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
SRC_DIR="${BASE:+$BASE/}prebuilt-bundles/$IDENTITY/$SOURCE"

carried=0
rebuilt=0
missing=()
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
