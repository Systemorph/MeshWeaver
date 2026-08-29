#!/usr/bin/env bash
# compose-sealed-modules.sh — take a gate's module bundles FROM THE SEALED PUBLICATION it seeds,
# never from the registry's package endpoint.
#
#   compose-sealed-modules.sh --identity <framework-identity> --packages "AI Essentials" \
#       --upstreams "plugins [socialmedia …]" --out <dir> \
#       ( --registry-url <url> --registry-key <mwi_…> | --storage-target <account>/<share>[/<base>] )
#
# 🚨 WHY THIS EXISTS (MeshWeaver#2698, 2026-08-29/30). A publication is sealed against the exact
# module bytes its bake composed (`--module`). The registry's package endpoint
# (`/api/plugins/bundles/<pkg>/<version>`) serves whatever the module's OWN lane published last,
# under a content version that does not move when a rebuild changes the bytes. A gate that seeds
# publication X but composes modules from the registry therefore judges NodeType assemblies built
# against one MeshWeaver.AI while running another — and the boot seeder rightly DECLINES every one
# of them ("dependency record mismatch — built against mvid:…, live is mvid:…"). That decline was
# fleet-wide on 2026-08-29: every satellite red, no diff in any of them.
#
# The publication is the unit of consistency. Since MeshWeaver#2707 a sealed publication carries the
# bundles it composed under `modules/` with its own `_index`, and the registry serves them at
# `…/prebuilt/<identity>/<source>/modules[/<bundle>]`. A gate pinned to identity X takes module
# bytes sealed FOR X — from the first upstream (in the order given) whose seal lists the package.
#
# 🚨 NO FALLBACK. An upstream with no seal for this identity, a seal that predates module sealing
# (no `modules/_index` — republish it under a core that carries #2707), or a package no upstream
# sealed, all fail RED naming the identity. Falling back to the registry would reproduce today's
# decline under a green tick — the exact shape a gate must never take.
set -euo pipefail

identity="" packages="" upstreams="" out="" registry_url="" registry_key="" storage_target=""
while [ $# -gt 0 ]; do
  case "$1" in
    --identity)       identity="${2:-}";       shift 2 ;;
    --packages)       packages="${2:-}";       shift 2 ;;
    --upstreams)      upstreams="${2:-}";      shift 2 ;;
    --out)            out="${2:-}";            shift 2 ;;
    --registry-url)   registry_url="${2:-}";   shift 2 ;;
    --registry-key)   registry_key="${2:-}";   shift 2 ;;
    --storage-target) storage_target="${2:-}"; shift 2 ;;
    *) echo "::error::compose-sealed-modules.sh: unknown argument '$1'"; exit 2 ;;
  esac
done

for required in identity packages upstreams out; do
  [ -n "${!required}" ] || { echo "::error::compose-sealed-modules.sh: --${required} is required"; exit 2; }
done
if [ -n "$registry_url" ]; then
  [ -n "$registry_key" ] || { echo "::error::compose-sealed-modules.sh: --registry-url needs --registry-key"; exit 2; }
elif [ -z "$storage_target" ]; then
  echo "::error::compose-sealed-modules.sh: pass --registry-url/--registry-key or --storage-target"; exit 2
fi
case "$identity" in */*|*..*|"") echo "::error::compose-sealed-modules.sh: identity '$identity' is not a bare name"; exit 2 ;; esac

mkdir -p "$out"
work=$(mktemp -d); trap 'rm -rf "$work"' EXIT

# ── one upstream's sealed module set (an index), or a RED reason ──────────────────────────────
# Prints the listed bundle names, one per line. Exit 1 with ::error when the upstream has no seal
# for this identity or the seal predates module sealing — both are stop conditions, not misses.
sealed_modules_of() { # <source>
  local src="$1"
  case "$src" in */*|*..*|"") echo "::error::upstream '$src' is not a bare source name"; return 1 ;; esac
  if [ -n "$registry_url" ]; then
    local base="${registry_url%/}/api/plugins/bundles/prebuilt/$identity/$src"
    local code
    code=$(curl -sS -o "$work/$src.modules.json" -w '%{http_code}' -H "Authorization: Bearer $registry_key" "$base/modules")
    case "$code" in
      200) ;;
      404)
        echo "::error::upstream '$src' answers 404 for the module set of identity $identity at $base/modules: $(tr -d '\n' < "$work/$src.modules.json" | head -c 300)"
        echo "::error::Either '$src' has no SEALED publication for this identity, or its publication predates module sealing (no modules/_index) — republish '$src' under a core that carries MeshWeaver#2707. Not composing from the registry instead: that is the decline this exists to end."
        return 1 ;;
      401|403)
        echo "::error::the registry refused registry-key for '$src' ($code) — the instance needs a whole-source grant '$src/*'."; return 1 ;;
      *)
        echo "::error::registry answered $code for $base/modules — refusing."; return 1 ;;
    esac
    jq -e '.modules | type == "array"' "$work/$src.modules.json" > /dev/null 2>&1 \
      || { echo "::error::$base/modules answered 200 but not a module-set index (starts: $(head -c 80 "$work/$src.modules.json" | tr -d '\n\r')…)"; return 1; }
    jq -r '.modules[]' "$work/$src.modules.json"
  else
    local account="${storage_target%%/*}" rest="${storage_target#*/}"
    local share="${rest%%/*}" base=""
    case "$rest" in */*) base="${rest#*/}" ;; esac
    local dir="${base:+$base/}prebuilt-bundles/$identity/$src"
    local sealed
    sealed=$(az storage file exists --account-name "$account" --share-name "$share" \
      --path "$dir/_complete" --auth-mode login --backup-intent --query exists -o tsv --only-show-errors 2>/dev/null || echo unknown)
    [ "$sealed" = "true" ] || { echo "::error::upstream '$src' has no SEALED publication under $account/$share/$dir for identity $identity (exists=$sealed)."; return 1; }
    if ! az storage file download --account-name "$account" --share-name "$share" \
         --path "$dir/modules/_index" --dest "$work/$src.modules.index" \
         --auth-mode login --backup-intent --only-show-errors > /dev/null 2>&1; then
      echo "::error::upstream '$src' is sealed under $account/$share/$dir but carries no modules/_index — the publication predates module sealing; republish '$src' under a core that carries MeshWeaver#2707."
      return 1
    fi
    grep -v '^[[:space:]]*$' "$work/$src.modules.index" || true
  fi
}

fetch_module() { # <source> <bundle-name> <dest>
  local src="$1" name="$2" dest="$3"
  if [ -n "$registry_url" ]; then
    curl -sSf -H "Authorization: Bearer $registry_key" -o "$dest" \
      "${registry_url%/}/api/plugins/bundles/prebuilt/$identity/$src/modules/$name" \
      || { echo "::error::could not fetch sealed module $name of '$src' for identity $identity"; return 1; }
  else
    local account="${storage_target%%/*}" rest="${storage_target#*/}"
    local share="${rest%%/*}" base=""
    case "$rest" in */*) base="${rest#*/}" ;; esac
    az storage file download --account-name "$account" --share-name "$share" \
      --path "${base:+$base/}prebuilt-bundles/$identity/$src/modules/$name" --dest "$dest" \
      --auth-mode login --backup-intent --only-show-errors > /dev/null \
      || { echo "::error::could not download sealed module $name of '$src' for identity $identity"; return 1; }
  fi
}

# Read every upstream's module set ONCE, in the order given; the first seal listing a package wins.
# A seal that cannot be read stops the run — see sealed_modules_of.
for src in $upstreams; do
  sealed_modules_of "$src" > "$work/$src.list"
  echo "upstream '$src' sealed $(grep -c '[^[:space:]]' "$work/$src.list" || true) module bundle(s) for identity $identity"
done

composed=0
for pkg in $packages; do
  wanted="$(printf '%s' "$pkg" | tr '[:upper:]' '[:lower:]').module.nupkg"
  found=""
  for src in $upstreams; do
    name=$(awk -v w="$wanted" 'tolower($0) == w { print; exit }' "$work/$src.list")
    if [ -n "$name" ]; then
      fetch_module "$src" "$name" "$out/$name"
      echo "composed $pkg from the sealed publication of '$src' for identity $identity ($name)"
      found="$src"; break
    fi
  done
  if [ -z "$found" ]; then
    echo "::error::no sealed upstream publication ($upstreams) for identity $identity carries module package '$pkg' (looked for $wanted). The upstream that owns it must compose it in its bake (module-artifacts / registry-modules) so its seal carries it; composing it from the registry here would be the decline this replaces."
    exit 1
  fi
  composed=$((composed + 1))
done
echo "composed=$composed"
