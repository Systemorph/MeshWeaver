#!/usr/bin/env bash
# deliver-live-publication.sh — make the REGISTRY carry what the SHARES serve, when this run
# publishes nothing of its own. Run it under bake-publication-lock.sh.
#
# WHY (Copilot's review of #6878). A lane run that publishes nothing — its content already sealed
# (`scope=none`), or superseded under the lock (`publish=false`) — used to skip the registry too,
# reasoning from the SHARES alone. But the registry is a second target with its own failure modes: a
# sibling can seal the shares, fail its registry login or push, and release the lock, and then every
# later run of that content — a re-run of the sibling included, since it too reads `scope=none` —
# skipped the registry as well. The identity tag stayed stale (or absent) until the content next
# changed, while bundle-fetch serves portals from it.
#
# So, under the prefix lock:
#   1. ask the registry which commit <source>:<identity> names (push-bundle-publication.sh
#      --published-commit — read only, nothing downloaded);
#   2. the commit the LIVE share publication records — or a newer one containing it — ⇒ verified,
#      done (the every-poll path costs one tag read);
#   3. otherwise deliver the LIVE SHARE PUBLICATION ITSELF: download it (following `_current` exactly
#      as every reader does), verify every byte against the digest stamp publish-bake-bundles.sh
#      wrote and that all of it is ONE publication, and push THAT with --skip-if-published. Never
#      this run's local bake: it is not what the shares serve, and in the superseded case it is older.
#
# ENVIRONMENT: BAKE_PUBLISH_TARGETS, IDENTITY, SOURCE, BUNDLE_REGISTRY (required); GH_TOKEN +
# BAKE_CONTENT_REPOSITORY (for the ordering the publisher applies); PUSH_BUNDLE_PUBLICATION (the
# publisher to run; defaults to the one beside this file — the harness substitutes a recorder).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PUSH="${PUSH_BUNDLE_PUBLICATION:-$HERE/push-bundle-publication.sh}"
: "${BAKE_PUBLISH_TARGETS:?BAKE_PUBLISH_TARGETS is required}"
: "${IDENTITY:?IDENTITY is required}"
: "${SOURCE:?SOURCE is required}"
: "${BUNDLE_REGISTRY:?BUNDLE_REGISTRY is required}"
SENTINEL="_complete"
POINTER="_current"

# The FIRST target in the canonical lock order — the carry's source and the lock's home alike.
target=$(printf '%s\n' $BAKE_PUBLISH_TARGETS | sed '/^[[:space:]]*$/d' | LC_ALL=C sort -u | head -n 1)
account="${target%%/*}"; rest="${target#*/}"; share="${rest%%/*}"; base=""
case "$rest" in */*) base="${rest#*/}";; esac
prefix="${base:+$base/}prebuilt-bundles/$IDENTITY/$SOURCE"
AZ=(--account-name "$account" --share-name "$share" --auth-mode login --backup-intent --only-show-errors)

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

exists_file() { # <path> → true|false; anything else is a refusal
  local answer
  answer=$(az storage file exists "${AZ[@]}" --path "$1" --query exists -o tsv)
  case "$answer" in true|false) printf '%s' "$answer" ;; *) echo "::error::az could not say whether $account/$share/$1 exists (answered '$answer')" >&2; return 1 ;; esac
}

# The live publication directory — the reader's rules: a `_current` naming one existing
# subdirectory, else the prefix itself.
live="$prefix"
pointer_exists=$(exists_file "$prefix/$POINTER")
if [ "$pointer_exists" = "true" ]; then
  az storage file download "${AZ[@]}" --path "$prefix/$POINTER" --dest "$WORK/pointer" > /dev/null
  named=$(sed -e 's/[[:space:]]*$//' -e 's/^[[:space:]]*//' "$WORK/pointer" | sed -n '/./{p;q;}')
  case "$named" in
    ''|.|..|*/*|*\\*) ;;
    *) dir_exists=$(az storage directory exists "${AZ[@]}" --name "$prefix/$named" --query exists -o tsv)
       [ "$dir_exists" != "true" ] || live="$prefix/$named" ;;
  esac
fi
sealed=$(exists_file "$live/$SENTINEL")
[ "$sealed" = "true" ] \
  || { echo "::error::$account/$share holds no SEALED publication under $live — there is nothing to deliver, and this run published nothing either. Refusing to report the registry as delivered."; exit 1; }
az storage file download "${AZ[@]}" --path "$live/source-commit.txt" --dest "$WORK/live-commit" > /dev/null
live_commit=$(tr -d '[:space:]' < "$WORK/live-commit")
[ -n "$live_commit" ] || { echo "::error::$live/source-commit.txt is empty — the live publication's content cannot be named."; exit 1; }

# ── 1–2. what the registry names, read only ──────────────────────────────────────────────────────
named_commit=$("$PUSH" --registry "$BUNDLE_REGISTRY" --source "$SOURCE" --identity "$IDENTITY" --published-commit)
if [ "$named_commit" = "$live_commit" ]; then
  echo "registry: $BUNDLE_REGISTRY/plugins/$SOURCE:$IDENTITY names $live_commit — the publication the shares serve. Verified; nothing to deliver."
  exit 0
fi
if [ -n "$named_commit" ] && [ -n "${GH_TOKEN:-}" ] && [ -n "${BAKE_CONTENT_REPOSITORY:-}" ]; then
  if order=$(gh api "repos/$BAKE_CONTENT_REPOSITORY/compare/$live_commit...$named_commit" --jq .status 2>"$WORK/compare.err") \
     && [ "$order" = "ahead" ]; then
    echo "registry: $BUNDLE_REGISTRY/plugins/$SOURCE:$IDENTITY names $named_commit, NEWER than the shares' $live_commit and containing it. Nothing to deliver."
    exit 0
  fi
fi
echo "::notice title=Delivering the live share publication to the registry::the registry names '${named_commit:-<nothing>}' for $SOURCE:$IDENTITY while the shares serve $live_commit — delivering the SHARES' publication (not this run's bake)."

# ── 3. the live share publication, byte for byte, verified against its own stamps ───────────────
pub="$WORK/publication"
mkdir -p "$pub/modules"
publications=""
fetch() { # <relative path> <required: yes|no>
  local rel="$1" required="$2" props digest owner present
  if [ "$required" = "no" ]; then
    present=$(exists_file "$live/$rel")
    [ "$present" = "true" ] || return 0
  fi
  az storage file download "${AZ[@]}" --path "$live/$rel" --dest "$pub/$rel" > /dev/null
  props=$(az storage file show "${AZ[@]}" --path "$live/$rel" \
    --query "[[metadata.digest || '-', metadata.publication || '-']]" -o tsv)
  digest=$(printf '%s' "$props" | awk -F'\t' 'NR == 1 { print $1 }')
  owner=$(printf '%s' "$props" | awk -F'\t' 'NR == 1 { print $2 }')
  if [ "$digest" = "-" ] || [ -z "$digest" ]; then
    echo "::error::$live/$rel carries no digest stamp — its bytes cannot be shown to be the publication's. Refusing to deliver an unverifiable set."
    return 1
  fi
  if command -v sha256sum > /dev/null 2>&1; then actual=$(sha256sum "$pub/$rel" | awk '{print $1}'); else actual=$(shasum -a 256 "$pub/$rel" | awk '{print $1}'); fi
  if [ "$actual" != "$digest" ]; then
    echo "::error::$live/$rel hashes to $actual but its stamp records $digest — the publication changed while it was read, or is damaged. Refusing to deliver it."
    return 1
  fi
  case " $publications " in *" $owner "*) ;; *) publications="$publications $owner" ;; esac
}
fetch "$SENTINEL" yes
# (`if`, never `&&`: errexit does not apply inside an `&&` list, so a refused file would be skipped.)
while IFS= read -r name; do if [ -n "$name" ]; then fetch "$name" yes < /dev/null; fi; done < "$pub/$SENTINEL"
for rel in source-commit.txt repository.txt architecture.txt platform-surface.json sealed-packages.json modules/_index; do
  fetch "$rel" no
done
if [ -f "$pub/modules/_index" ]; then
  while IFS= read -r name; do if [ -n "$name" ]; then fetch "modules/$name" yes < /dev/null; fi; done < "$pub/modules/_index"
fi
count=$(printf '%s' "$publications" | tr ' ' '\n' | grep -c '[^[:space:]]' || true)
if [ "$count" -ne 1 ]; then
  echo "::error::the live publication under $live is stamped by $count publication(s):$publications — not ONE publication. Refusing to deliver a mix."
  exit 1
fi
echo "delivering $live (publication$publications, source $live_commit) to $BUNDLE_REGISTRY"
"$PUSH" --registry "$BUNDLE_REGISTRY" --source "$SOURCE" --identity "$IDENTITY" --dir "$pub" --skip-if-published
