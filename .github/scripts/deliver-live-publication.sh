#!/usr/bin/env bash
# deliver-live-publication.sh — make the fleet REGISTRY carry what the SHARES serve, on a lane run
# that bakes nothing of its own (bake-scope.sh decided `scope=none`). Run it under
# bake-publication-lock.sh. MeshWeaver#7323.
#
# WHY. `scope=none` means the shares already hold this content for this identity, and every
# registry step of node-repo-publish-bake.yml used to be gated off on it. But the registry
# (`plugins/<source>:<identity>`, which the `bundle-fetch` init container serves portals from) is a
# SECOND target with its own failure modes: a run can seal the shares, then fail its registry login
# or push. Every later run of that content — its own re-run included — reads `scope=none` too, so
# the tag stayed stale (or absent) until the content next changed.
#
# So, under the prefix lock:
#   1. resolve the LIVE share publication by the reader's rules (`_current`, else the prefix) on
#      the first target in the canonical lock order — bake-scope.sh returns `none` only when every
#      target holds the same sealed commit and listing — and read its source-commit.txt;
#   2. ask check-registry-names-live.sh --probe whether the tag names that commit IN THE CURRENT
#      LAYOUT, or a newer commit containing it. The SAME decision the superseded path's guard makes,
#      so "a same-commit tag of an older layout is not verified" holds here by construction. This is
#      the every-poll path: one pointer read, one marker read, one tag read, nothing downloaded;
#   3. otherwise DELIVER THE LIVE SHARE PUBLICATION ITSELF — never this run's bake (there is none,
#      and it would not be what the shares serve): download every file of it, verify each against
#      the `digest` stamp publish-bake-bundles.sh wrote, require every file to carry a `publication`
#      stamp and ALL of them to name the ONE publication that sealed `_complete` (a missing stamp is
#      NOT an owner — '-' never counts as one), then push that directory with the same publisher
#      and the same flags the publishing path uses;
#   4. re-ask (step 2, not probing): green only when the tag now names the live publication.
#
# A registry that cannot be READ, a share with no sealed publication, an unstamped or damaged file,
# or a set stamped by two publications is RED, naming what — never a green over an unproven tag.
#
# ENVIRONMENT: BAKE_PUBLISH_TARGETS, IDENTITY, SOURCE, BUNDLE_REGISTRY (required); GH_TOKEN +
# BAKE_CONTENT_REPOSITORY (to order two commits, as the publisher does); GITHUB_RUN_ID (the
# immutable `<identity>-<run>` tag); PUSH_BUNDLE_PUBLICATION (the publisher; defaults to the one
# beside this file — the harness substitutes a recorder). AUTH: `az login` already happened.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PUSH="${PUSH_BUNDLE_PUBLICATION:-$HERE/push-bundle-publication.sh}"
CHECK="$HERE/check-registry-names-live.sh"
: "${BAKE_PUBLISH_TARGETS:?BAKE_PUBLISH_TARGETS is required}"
: "${IDENTITY:?IDENTITY is required}"
: "${SOURCE:?SOURCE is required}"
: "${BUNDLE_REGISTRY:?BUNDLE_REGISTRY is required}"
SENTINEL="_complete"
POINTER="_current"
# The publication's sidecars beside `_complete`, its bundles and its modules — the files
# publish-bake-bundles.sh plans (plan_add) and materialises for the registry. Absent ones are
# skipped (an older publisher wrote fewer); source-commit.txt is required below. The harness
# delivers a REAL publication and compares the result with the share byte for byte, so a sidecar
# the publisher starts writing and this list does not name is red there, not a silent omission.
# modules/_index is NOT optional: the publisher always writes it (an empty set included) and treats a
# publication without it as incomplete, so it is fetched as required below.
SIDECARS="source-commit.txt repository.txt architecture.txt platform-surface.json sealed-packages.json platform-set.json"

# shellcheck disable=SC2086 # whitespace-separated targets, split on purpose (bake-publication-lock.sh does the same)
target=$(printf '%s\n' $BAKE_PUBLISH_TARGETS | sed '/^[[:space:]]*$/d' | LC_ALL=C sort -u | head -n 1)
account="${target%%/*}"; rest="${target#*/}"; share="${rest%%/*}"; base=""
case "$rest" in */*) base="${rest#*/}";; esac
if [ -z "$account" ] || [ -z "$share" ] || [ "$account" = "$target" ]; then
  echo "::error::malformed BAKE_PUBLISH_TARGETS entry '$target' — expected <account>/<share>[/<base-path>]"
  exit 1
fi
prefix="${base:+$base/}prebuilt-bundles/$IDENTITY/$SOURCE"
AZ=(--account-name "$account" --share-name "$share" --auth-mode login --backup-intent --only-show-errors)

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

sha256_of() { # <file> — hex digest, portable across the CI runner and a developer's macOS
  if command -v sha256sum > /dev/null 2>&1; then sha256sum "$1" | awk '{print $1}'; else shasum -a 256 "$1" | awk '{print $1}'; fi
}
exists_file() { # <path> → true|false; any other answer is a refusal, never a "false"
  local answer
  answer=$(az storage file exists "${AZ[@]}" --path "$1" --query exists -o tsv) || answer="<the call failed>"
  case "$answer" in
    true|false) printf '%s' "$answer" ;;
    *) echo "::error::az could not say whether $account/$share/$1 exists (answered '$answer') — the live publication cannot be read." >&2; return 1 ;;
  esac
}

# ── 1. the live share publication — the READER's rules, exactly (carry-forward-bundles.sh,
#      ShippedPrebuiltBundles.PublicationDirectoryOf): a `_current` naming one existing
#      subdirectory, else the prefix itself. ─────────────────────────────────────────────────────
# Every existence answer is ASSIGNED before it is tested, so errexit reaches a failed call: inside
# `[ "$(…)" = true ]` a refusal would read as "absent" — and an unreadable pointer would then
# silently send this script to the flat prefix, possibly an OLDER publication than the one served.
live="$prefix"
pointer_exists=$(exists_file "$prefix/$POINTER")
if [ "$pointer_exists" = "true" ]; then
  az storage file download "${AZ[@]}" --path "$prefix/$POINTER" --dest "$WORK/pointer" > /dev/null
  named=$(sed -e 's/[[:space:]]*$//' -e 's/^[[:space:]]*//' "$WORK/pointer" | sed -n '/./{p;q;}')
  case "$named" in
    ''|.|..|*/*|*\\*) echo "::warning::$prefix/$POINTER names '$named', which is not a single directory name — reading $prefix as its own publication directory." ;;
    *) dir_exists=$(az storage directory exists "${AZ[@]}" --name "$prefix/$named" --query exists -o tsv) || dir_exists="<the call failed>"
       # Only a confirmed `false` falls back: a refused answer read as "absent" would send this
       # script to the flat prefix, possibly an OLDER publication than the generation served.
       case "$dir_exists" in
         true) live="$prefix/$named" ;;
         false) echo "::warning::$prefix/$POINTER names generation '$named', which is not on the share — reading $prefix as its own publication directory." ;;
         *) echo "::error::az could not say whether generation $account/$share/$prefix/$named exists (answered '$dir_exists') — the live publication cannot be resolved."; exit 1 ;;
       esac ;;
  esac
fi
sealed=$(exists_file "$live/$SENTINEL")
[ "$sealed" = "true" ] \
  || { echo "::error::$account/$share holds no SEALED publication under $live — yet this run's scope decision read one. Nothing to deliver, and the registry cannot be shown to carry what the shares serve. Refusing to go green."; exit 1; }
az storage file download "${AZ[@]}" --path "$live/source-commit.txt" --dest "$WORK/live-commit" > /dev/null \
  || { echo "::error::$account/$share/$live/source-commit.txt could not be read — the live publication's content cannot be named."; exit 1; }
live_commit=$(tr -d '[:space:]' < "$WORK/live-commit")
case "$live_commit" in
  ''|unknown) echo "::error::$live/source-commit.txt records '${live_commit:-<nothing>}' — the live publication's content cannot be named, so neither the tag nor a delivery can be checked against it."; exit 1 ;;
esac
tag="$BUNDLE_REGISTRY/plugins/$SOURCE:$IDENTITY"
echo "live share publication: $account/$share/$live (source $live_commit)"

# ── 2. what the registry names — read only; the probe's exit code is the verdict ───────────────
rc=0
probe=$(LIVE_COMMIT="$live_commit" PUSH_BUNDLE_PUBLICATION="$PUSH" "$CHECK" --probe) || rc=$?
printf '%s\n' "$probe"
case "$rc" in
  0) exit 0 ;;
  3) ;;
  *) echo "::error::whether $tag carries the live publication could not be established (the registry read failed, see above) — not green, and nothing is pushed over a registry this run cannot read."; exit 1 ;;
esac
echo "::notice title=Delivering the live share publication to the registry::$tag is not current (${probe#stale: }). Delivering the SHARES' publication $live ($live_commit) — this run baked nothing, and a tag left behind by a failed registry push is repaired here rather than waiting for the content to change (MeshWeaver#7323)."

# ── 3. the live share publication, byte for byte, verified against its OWN stamps ──────────────
pub="$WORK/publication"
mkdir -p "$pub/modules"
owners=""
fetched=0
fetch() { # <relative path> <required: yes|no>
  local rel="$1" required="$2" props fields digest owner actual present
  if [ "$required" = "no" ]; then
    present=$(exists_file "$live/$rel") || return 1
    [ "$present" = "true" ] || return 0
  fi
  az storage file download "${AZ[@]}" --path "$live/$rel" --dest "$pub/$rel" > /dev/null \
    || { echo "::error::$live/$rel could not be downloaded — refusing to deliver a partial publication."; return 1; }
  # The list-of-ONE-ROW query with '-' guards, as carry-forward-bundles.sh reads it (knack renders
  # a top-level list as rows and a null as 'None'); the field count is asserted, never assumed.
  props=$(az storage file show "${AZ[@]}" --path "$live/$rel" \
    --query "[[metadata.digest || '-', metadata.publication || '-']]" -o tsv) \
    || { echo "::error::the stamps of $live/$rel could not be read — an unreadable stamp is not an absent one. Refusing."; return 1; }
  fields=$(printf '%s' "$props" | awk -F'\t' 'NR == 1 { print NF }')
  [ "${fields:-0}" -eq 2 ] \
    || { echo "::error::az answered '$props' for the stamps of $live/$rel — one row of TWO tab-separated fields was expected. Refusing."; return 1; }
  digest=$(printf '%s' "$props" | awk -F'\t' 'NR == 1 { print $1 }')
  owner=$(printf '%s' "$props" | awk -F'\t' 'NR == 1 { print $2 }')
  case "$digest" in
    ''|-) echo "::error::$live/$rel carries no digest stamp — its bytes cannot be shown to be the publication's. Refusing to deliver an unverifiable set."; return 1 ;;
  esac
  # 🚨 A MISSING publication stamp is NOT an owner (Copilot on the first draft, #6878): counting '-'
  # as one would let a set written entirely by publishers that stamp nothing pass as "one publication".
  case "$owner" in
    ''|-) echo "::error::$live/$rel carries no publication stamp — nothing establishes that it belongs to the publication that sealed $SENTINEL. Refusing to deliver it."; return 1 ;;
  esac
  actual=$(sha256_of "$pub/$rel")
  [ "$actual" = "$digest" ] \
    || { echo "::error::$live/$rel hashes to $actual but its stamp records $digest — the publication changed while it was read, or is damaged. Refusing to deliver it."; return 1; }
  case " $owners " in *" $owner "*) ;; *) owners="$owners $owner" ;; esac
  fetched=$((fetched + 1))
}
fetch "$SENTINEL" yes
sealed_by="${owners# }"
# (`if`, never `&&`: errexit does not apply inside an `&&` list, so a refused file would be skipped.)
while IFS= read -r name || [ -n "$name" ]; do
  name="${name#"${name%%[![:space:]]*}"}"; name="${name%"${name##*[![:space:]]}"}"
  if [ -n "$name" ]; then
    case "$name" in */*|.|..|*\\*) echo "::error::$SENTINEL lists '$name', which is not a plain file name"; exit 1 ;; esac
    fetch "$name" yes < /dev/null
  fi
done < "$pub/$SENTINEL"
for rel in $SIDECARS; do fetch "$rel" no < /dev/null; done
[ -f "$pub/source-commit.txt" ] && [ "$(tr -d '[:space:]' < "$pub/source-commit.txt")" = "$live_commit" ] \
  || { echo "::error::the downloaded source-commit.txt does not record $live_commit — the live publication moved while it was read (the prefix lock should exclude that). Refusing."; exit 1; }
index_present=$(exists_file "$live/modules/_index")
[ "$index_present" = "true" ] \
  || { echo "::error::$live carries no modules/_index — the publisher always writes one, so this publication is INCOMPLETE and delivering it would push a module-free set. Refusing."; exit 1; }
fetch "modules/_index" yes
while IFS= read -r name || [ -n "$name" ]; do
  name="${name#"${name%%[![:space:]]*}"}"; name="${name%"${name##*[![:space:]]}"}"
  if [ -n "$name" ]; then
    case "$name" in */*|.|..|*\\*) echo "::error::modules/_index lists '$name', which is not a plain file name"; exit 1 ;; esac
    fetch "modules/$name" yes < /dev/null
  fi
done < "$pub/modules/_index"
rmdir "$pub/modules" 2>/dev/null || true
# shellcheck disable=SC2086 # one token per owner, split on purpose
count=$(printf '%s\n' $owners | grep -c '[^[:space:]]' || true)
if [ "$count" -ne 1 ] || [ "${owners# }" != "$sealed_by" ]; then
  echo "::error::the live publication under $live is stamped by $count publication(s):$owners (its $SENTINEL by '$sealed_by') — not ONE publication. Refusing to deliver a mix."
  exit 1
fi
echo "verified: $fetched file(s) of $live, every one hashing to its digest stamp and stamped by publication '$sealed_by' (which sealed $SENTINEL)"

# The SAME publisher and flags as the publishing path's registry step: `--skip-if-published` is
# its sealed-skip AND its never-move-the-tag-backwards guard (GH_TOKEN + BAKE_CONTENT_REPOSITORY).
"$PUSH" --registry "$BUNDLE_REGISTRY" --source "$SOURCE" --identity "$IDENTITY" \
  --dir "$pub" ${GITHUB_RUN_ID:+--tag-run "$GITHUB_RUN_ID"} --skip-if-published

# ── 4. the postcondition — the tag now names the live publication, or this is RED ───────────────
LIVE_COMMIT="$live_commit" PUSH_BUNDLE_PUBLICATION="$PUSH" "$CHECK"
echo "::notice title=Registry repaired::$tag now carries the live share publication $live_commit (delivered from $account/$share/$live, publication '$sealed_by')."
