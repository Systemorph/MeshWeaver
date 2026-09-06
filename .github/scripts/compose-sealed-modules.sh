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

# ── re-ask a registry that has not answered yet ───────────────────────────────────────────────
# 🚨 A 5xx is not an answer — it is the ABSENCE of one. Promoting the platform ROLLS the portal
# that serves this registry, so every promotion opens a window in which a dependent repo's gate
# reads a 503 through no fault of its own diff (MeshWeaver#2787: three promotions in one day, six
# repos reading this endpoint). Refusing on it makes the fleet's CI a function of deploy timing.
#
# A DEFINITE answer is untouched and still refuses at once — 404 (unsealed / predates module
# sealing) and 401/403 (no grant) are decisions the registry has already made, and waiting on one
# only delays the message naming it. Same distinction, same reason, as MeshWeaver#2836 on the
# portal side: branch on what the server SAID, never on "the read failed".
#
# 🚨 A retry, NOT a skip-trapdoor: when the attempts are spent the last status is returned and
# every caller still fails RED — a registry that is genuinely down fails the gate exactly as it
# did before, about three minutes later.
#
# 🚨 There is a SECOND copy of this function, in .github/workflows/node-repo-gate.yml (same
# policy, REGISTRY_KEY spelled upper-case there). It is duplicated ON PURPOSE rather than
# factored into a third file: THIS script is fetched from core at a pinned `platform-ref` while
# that workflow is pinned by the caller's `uses:`, so a shared file would be a THIRD
# independently-pinned artefact — the exact skew that once let an old workflow drive a new
# script and seal an empty module set. Change one, change both.
#
# 🚨 <generation> PINS THE PUBLICATION INSTANCE (MeshWeaver#3401). Reading the module-set index
# and then each bundle it names is N+1 reads of a directory the publisher may reseal underneath
# us; sending it as If-Match makes the server answer 412 rather than silently handing back bytes
# from a different publication than the index described. Opt-in: a registry predating #3401
# publishes no generation, the header is then absent, and the behaviour is exactly as before.
registry_get() { # <url> <out-file> [generation] → echoes the final HTTP status
  local url="$1" out="$2" generation="${3:-}" code="" attempt=0
  local -r delays="15 30 60 90"
  # An `if`, not `[ … ] && …`: bash exempts a non-final `&&` operand from `set -e` today, but
  # only because this is not the function's last statement — an `if` cannot become a silent
  # early return when someone moves it.
  local -a precondition=()
  if [ -n "$generation" ]; then precondition=(-H "If-Match: $generation"); fi
  while :; do
    code=$(curl -sS -o "$out" -w '%{http_code}' \
             -H "Authorization: Bearer $registry_key" \
             ${precondition[@]+"${precondition[@]}"} "$url") || code="000"
    case "$code" in
      # Transient: a roll window, a gateway blip, or a connection that never landed (000).
      408|429|5??|000) ;;
      *) printf '%s' "$code"; return 0 ;;
    esac
    attempt=$((attempt+1))
    local delay
    delay=$(printf '%s\n' $delays | sed -n "${attempt}p")
    [ -n "$delay" ] || { printf '%s' "$code"; return 0; }
    echo "registry answered $code for $url — transient (a platform roll looks like this);" \
         "re-asking in ${delay}s, attempt $((attempt+1))" >&2
    sleep "$delay"
  done
}

# ── one upstream's sealed module set (an index), or a RED reason ──────────────────────────────
# Writes the listed bundle names, one per line, into <list-file>, and the publication GENERATION
# the index carried into <generation-file> (empty on a registry that predates MeshWeaver#3401, or
# on the storage path, which reads the share directly). Exit 1 with ::error when the upstream has
# no seal for this identity or the seal predates module sealing — both are stop conditions, not
# misses. 🚨 The list goes to a FILE, never stdout: the first version returned it on stdout and
# the caller captured it, so every ::error this function printed vanished into the list file and
# a red gate showed nothing but "exit code 1" (Manufacturing run 33283588300).
sealed_modules_of() { # <source> <list-file> <generation-file>
  local src="$1" list="$2" genfile="$3"
  : > "$genfile"
  case "$src" in */*|*..*|"") echo "::error::upstream '$src' is not a bare source name"; return 1 ;; esac
  if [ -n "$registry_url" ]; then
    local base="${registry_url%/}/api/plugins/bundles/prebuilt/$identity/$src"
    local code
    code=$(registry_get "$base/modules" "$work/$src.modules.json")
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
    jq -r '.modules[]' "$work/$src.modules.json" > "$list"
    jq -r '.generation // empty' "$work/$src.modules.json" > "$genfile"
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
    grep -v '^[[:space:]]*$' "$work/$src.modules.index" > "$list" || true
  fi
}

# Returns 0 on success, 2 when the publication MOVED under this fetch (412 — the caller re-reads
# the module set that now applies), 1 for every other refusal.
fetch_module() { # <source> <bundle-name> <dest> [generation]
  local src="$1" name="$2" dest="$3" generation="${4:-}"
  if [ -n "$registry_url" ]; then
    local code
    code=$(registry_get \
      "${registry_url%/}/api/plugins/bundles/prebuilt/$identity/$src/modules/$name" "$dest" "$generation")
    case "$code" in
      200) return 0 ;;
      # 🚨 The publication was resealed between the index read and this one (MeshWeaver#3401).
      # Composing what we have would mix module bytes from two publications — the mvid mismatch
      # that DECLINES every NodeType assembly at adoption, silently. Say so and let the caller
      # re-read; never continue on the half we hold.
      412) echo "::notice::the '$src' publication was resealed while its module set was being composed (we held generation ${generation:-<none>}) — re-reading the publication that now applies."; return 2 ;;
      404) echo "::error::'$name' is listed in the '$src' sealed module set for identity $identity but the registry will not serve it — the seal and the store disagree, and that needs a republish. This is NOT the republish window: that answers 503 and is waited out above."; return 1 ;;
      *) echo "::error::could not fetch sealed module $name of '$src' for identity $identity — registry answered $code."; return 1 ;;
    esac
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

# ── one whole composition, against ONE publication instance per upstream ──────────────────────
# Reads every upstream's module set (in the order given; the first seal listing a package wins),
# then fetches each wanted package PINNED to the generation that listing came from. Returns 0 on
# success, 2 when a publication moved mid-composition (the caller re-reads), 1 on any refusal.
compose_once() {
  local src pkg wanted name frc
  for src in $upstreams; do
    sealed_modules_of "$src" "$work/$src.list" "$work/$src.generation" || return 1
    echo "upstream '$src' sealed $(grep -c '[^[:space:]]' "$work/$src.list" || true) module bundle(s) for identity $identity"
  done

  composed=0
  : > "$work/composed.list"
  for pkg in $packages; do
    wanted="$(printf '%s' "$pkg" | tr '[:upper:]' '[:lower:]').module.nupkg"
    found=""
    for src in $upstreams; do
      name=$(awk -v w="$wanted" 'tolower($0) == w { print; exit }' "$work/$src.list")
      if [ -n "$name" ]; then
        frc=0
        fetch_module "$src" "$name" "$out/$name" "$(cat "$work/$src.generation")" || frc=$?
        if [ "$frc" = 2 ]; then
          # 🚨 DISCARD THE HALF WE HOLD. A module bundle taken from the publication that has now
          # been superseded must not survive into the compile surface: mixing two publications'
          # module bytes is exactly the mvid mismatch that DECLINES every NodeType assembly at
          # adoption, and a stale file in $out would be composed as a `--module` regardless.
          rm -f "$out/$name"
          while IFS= read -r stale; do rm -f "$stale"; done < "$work/composed.list"
          return 2
        fi
        [ "$frc" = 0 ] || return "$frc"
        printf '%s\n' "$out/$name" >> "$work/composed.list"
        echo "composed $pkg from the sealed publication of '$src' for identity $identity ($name)"
        found="$src"; break
      fi
    done
    if [ -z "$found" ]; then
      echo "::error::no sealed upstream publication ($upstreams) for identity $identity carries module package '$pkg' (looked for $wanted). The upstream that owns it must compose it in its bake (module-artifacts / registry-modules) so its seal carries it; composing it from the registry here would be the decline this replaces."
      return 1
    fi
    composed=$((composed + 1))
  done
}

# 🚨 A COMPOSITION READS ONE PUBLICATION, OR NONE (MeshWeaver#3401). The index and the N bundle
# fetches are N+1 reads of a directory the publisher unseals, rewrites and re-seals — roughly a
# minute and a half per target, per publish, and both core CD and the Plugins satellite publish
# the `plugins` prefix. When that lands mid-composition the server answers 412 and this restarts
# against the publication that now applies; it never keeps the half it holds, and it never
# retries the read that failed. The bound is what makes it a read rather than a poll: a publisher
# resealing faster than a composition completes is a real condition, and it goes RED naming
# itself instead of spinning.
RESTARTS=2
composed=0
attempt=0
while :; do
  rc=0
  compose_once || rc=$?
  [ "$rc" = 2 ] || break
  attempt=$((attempt + 1))
  if [ "$attempt" -gt "$RESTARTS" ]; then
    echo "::error::the upstream publication(s) ($upstreams) for identity $identity were resealed under every one of $((RESTARTS + 1)) composition attempts — the publisher is republishing faster than a module set can be read. This is MeshWeaver#3401, not a fault of this repo's diff: re-run once the upstream's publish-bake has settled."
    exit 1
  fi
done
[ "$rc" = 0 ] || exit "$rc"
echo "composed=$composed"
