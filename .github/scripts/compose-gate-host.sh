#!/usr/bin/env bash
# compose-gate-host.sh <portal-app> <tester-app> <out>
#
# Composes THE GATE HOST: the platform (portal) image's /app with the tester CLI laid beside it —
# the process the NodeType bake and its gate run AS since MeshWeaver#3022.
#
# WHY A COMPOSED HOST. A bake is adopted by the PORTAL through the portal's own code, which reads
# three PROCESS facts: the framework identity beside the entry assembly (meshweaver-surface.manifest),
# the implementation MVIDs of the assemblies beside it, and the TPA — what can be LOADED. The gate
# deliberately runs that same adoption code rather than a gate-only reader, so a gate can only judge
# the platform's bake when its process IS the platform host. Until #3022 the gate ran as the TESTER
# image — whose /app is a strict subset of the portal's (measured on 3.0.0-rc9.ci.7534: 88 vs 219
# assemblies; on that image MeshWeaver.Maps, .AI, .ContentCollections.Indexing — modules since — and the Blazor and hosting halves existed
# only in the portal) — so content binding a portal-shipped assembly could neither compile in the bake
# nor load in the gate, and every such failure read as a CONTENT error on source nobody had changed.
#
# THE RULES, and why each one is what it is:
#   * portal /app first, complete — its manifest IS the host's identity, its assemblies ARE what a
#     portal loads;
#   * the tester's files are ADDED only where the portal has none (the CLI, MeshWeaver.Hosting.Monolith,
#     razor-generators/, sdk-generators/, module-libs/, the console-runtime Microsoft.Extensions.*
#     copies) — a file both carry keeps the PORTAL's bytes, because the process is the portal;
#   * the tester's meshweaver-surface.manifest is NEVER copied — it would make the host resolve the
#     tester's identity, which is the exact confusion this host exists to end;
#   * the tester's mw-plugin-test.deps.json is NEVER copied — 🚨 load-bearing, not tidiness: with an
#     app-local deps.json the dotnet host builds the TPA from THAT file's entries only, and every
#     portal-only assembly (a Blazor half…) would be on disk yet unloadable ("could not load file or assembly").
#     Without it the host probes the application directory and every assembly in it is in the TPA
#     (documented host behaviour: no deps.json ⇒ all assemblies in the app directory are added).
#     The portal's own <Host>.deps.json stays; the runtime reads only <entry>.deps.json.
#
# 🚨 IT FAILS CLOSED. A portal directory without a manifest, a tester directory without the CLI or its
# runtimeconfig, or a result missing any of those, is exit 1 naming the file. A host composed from a
# partial read would boot, adopt nothing and pass — the shape every gate here forbids.
#
# The identity PRECONDITION is deliberately NOT in this script (it is pure file logic): the lane
# asserts, with the tester's own `framework-identity` verb, that the tester and the portal resolve
# ONE identity — the two images are one build — before it trusts the composition.
set -euo pipefail

# ── self-test ────────────────────────────────────────────────────────────────────────────────
#     .github/scripts/compose-gate-host.sh --self-test
# Proves the rules above FIRE: portal wins a shared file, tester-only files ride, the two files
# that must never ride do not, and every refusal is a non-zero exit. Run on every platform PR
# (dotnet-test.yml preflight) beside the other lane scripts' self-tests.
if [ "${1:-}" = "--self-test" ]; then
  set +e
  self="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"
  tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' EXIT
  fail() { echo "SELF-TEST FAILED: $1"; exit 1; }
  portal="$tmp/portal"; tester="$tmp/tester"; out="$tmp/host"
  mkdir -p "$portal/modules/X" "$tester/razor-generators/linux-x64" "$tester/cs"
  printf 'MeshWeaver.Layout=aaaa\nMeshWeaver.PortalOnly=bbbb\n' > "$portal/meshweaver-surface.manifest"
  printf 'PORTAL' > "$portal/MeshWeaver.Layout.dll"
  printf 'PORTAL' > "$portal/MeshWeaver.PortalOnly.dll"
  printf 'PORTAL' > "$portal/Newtonsoft.Json.dll"
  printf '{}' > "$portal/Memex.Portal.Distributed.deps.json"
  printf '{}' > "$portal/Memex.Portal.Distributed.runtimeconfig.json"
  printf 'X' > "$portal/modules/X/X.dll"
  printf 'MeshWeaver.Layout=aaaa\n' > "$tester/meshweaver-surface.manifest"
  printf 'PORTAL' > "$tester/MeshWeaver.Layout.dll"
  printf 'TESTER' > "$tester/Newtonsoft.Json.dll"
  printf 'CLI' > "$tester/mw-plugin-test.dll"
  printf '{}' > "$tester/mw-plugin-test.deps.json"
  printf '{}' > "$tester/mw-plugin-test.runtimeconfig.json"
  printf 'MONO' > "$tester/MeshWeaver.Hosting.Monolith.dll"
  printf 'RZ' > "$tester/razor-generators/linux-x64/Razor.dll"
  printf 'RES' > "$tester/cs/mw-plugin-test.resources.dll"

  "$self" "$portal" "$tester" "$out" > "$tmp/log" || fail "the happy path exited non-zero: $(cat "$tmp/log")"
  [ "$(cat "$out/Newtonsoft.Json.dll")" = "PORTAL" ]      || fail "a file both images carry must keep the PORTAL's bytes"
  [ "$(cat "$out/MeshWeaver.PortalOnly.dll")" = "PORTAL" ]      || fail "portal-only assemblies must be in the host"
  [ -f "$out/mw-plugin-test.dll" ]                        || fail "the tester CLI must be in the host"
  [ -f "$out/MeshWeaver.Hosting.Monolith.dll" ]           || fail "tester-only assemblies must ride"
  [ -f "$out/razor-generators/linux-x64/Razor.dll" ]      || fail "tester-only subdirectories must ride"
  [ -f "$out/cs/mw-plugin-test.resources.dll" ]           || fail "tester-only satellite resources must ride"
  [ -f "$out/mw-plugin-test.runtimeconfig.json" ]         || fail "the CLI's runtimeconfig must ride"
  [ ! -e "$out/mw-plugin-test.deps.json" ]                || fail "the tester's deps.json must NOT ride (it would shrink the TPA to the tester's closure)"
  cmp -s "$portal/meshweaver-surface.manifest" "$out/meshweaver-surface.manifest" || fail "the host's manifest must be the PORTAL's"
  [ -f "$out/Memex.Portal.Distributed.deps.json" ]        || fail "the portal's own deps.json must stay"
  [ -f "$out/modules/X/X.dll" ]                           || fail "the portal's modules/ tree must be in the host"
  grep -q "1 shared file(s) differ" "$tmp/log"           || fail "the summary must count the shared files that differ (got: $(cat "$tmp/log"))"

  # Re-running over an existing output must not accumulate stale files.
  printf 'STALE' > "$out/Stale.dll"
  "$self" "$portal" "$tester" "$out" > /dev/null || fail "the second run exited non-zero"
  [ ! -e "$out/Stale.dll" ] || fail "a re-run must start from an empty host, not layer over the previous one"

  # Refusals — each names its file.
  noman="$tmp/portal-nomanifest"; cp -R "$portal" "$noman"; rm "$noman/meshweaver-surface.manifest"
  "$self" "$noman" "$tester" "$tmp/h1" > "$tmp/e1" 2>&1 && fail "a portal directory without a surface manifest must be refused"
  grep -q "meshweaver-surface.manifest" "$tmp/e1" || fail "the manifest refusal must name the file (got: $(cat "$tmp/e1"))"
  nocli="$tmp/tester-nocli"; cp -R "$tester" "$nocli"; rm "$nocli/mw-plugin-test.dll"
  "$self" "$portal" "$nocli" "$tmp/h2" > "$tmp/e2" 2>&1 && fail "a tester directory without the CLI must be refused"
  grep -q "mw-plugin-test.dll" "$tmp/e2" || fail "the CLI refusal must name the file (got: $(cat "$tmp/e2"))"
  norc="$tmp/tester-norc"; cp -R "$tester" "$norc"; rm "$norc/mw-plugin-test.runtimeconfig.json"
  "$self" "$portal" "$norc" "$tmp/h3" > "$tmp/e3" 2>&1 && fail "a tester directory without the runtimeconfig must be refused"
  grep -q "runtimeconfig" "$tmp/e3" || fail "the runtimeconfig refusal must name the file (got: $(cat "$tmp/e3"))"
  "$self" "$tmp/does-not-exist" "$tester" "$tmp/h4" > "$tmp/e4" 2>&1 && fail "a missing portal directory must be refused"
  "$self" "$portal" "$tester" > "$tmp/e5" 2>&1 && fail "a missing <out> argument must be refused"
  echo "compose-gate-host.sh self-test: OK"
  exit 0
fi

PORTAL="${1:?usage: compose-gate-host.sh <portal-app> <tester-app> <out>}"
TESTER="${2:?usage: compose-gate-host.sh <portal-app> <tester-app> <out>}"
OUT="${3:?usage: compose-gate-host.sh <portal-app> <tester-app> <out>}"

die() { echo "::error::compose-gate-host: $1"; exit 1; }

[ -d "$PORTAL" ] || die "portal app directory '$PORTAL' does not exist"
[ -d "$TESTER" ] || die "tester app directory '$TESTER' does not exist"
[ -s "$PORTAL/meshweaver-surface.manifest" ] \
  || die "'$PORTAL' has no meshweaver-surface.manifest — a host without one resolves the fallback identity no bake may be published under, so it is not a platform host (is this the portal image's /app?)"
[ -f "$TESTER/mw-plugin-test.dll" ] \
  || die "'$TESTER' has no mw-plugin-test.dll — not a tester image's /app"
[ -f "$TESTER/mw-plugin-test.runtimeconfig.json" ] \
  || die "'$TESTER' has no mw-plugin-test.runtimeconfig.json — the CLI cannot be started without its runtimeconfig"

rm -rf "$OUT"
mkdir -p "$OUT"
cp -R "$PORTAL/." "$OUT/"

added=0; identical=0; differing=0; difflist=""
while IFS= read -r -d '' f; do
  rel="${f#"$TESTER"/}"
  case "$rel" in
    mw-plugin-test.deps.json)    continue ;;   # see the header: the TPA must be the directory, not the tester's closure
    meshweaver-surface.manifest) continue ;;   # the host's identity is the PORTAL's
  esac
  if [ -e "$OUT/$rel" ]; then
    if cmp -s "$f" "$OUT/$rel"; then identical=$((identical+1)); else differing=$((differing+1)); difflist="$difflist $rel"; fi
  else
    mkdir -p "$OUT/$(dirname "$rel")"
    cp "$f" "$OUT/$rel"
    added=$((added+1))
  fi
done < <(find "$TESTER" -type f -print0)

# Postconditions — the composition is what the lane then RUNS, so each is asserted, not assumed.
[ -f "$OUT/mw-plugin-test.dll" ]                 || die "postcondition: the CLI did not land in '$OUT'"
[ -f "$OUT/mw-plugin-test.runtimeconfig.json" ]  || die "postcondition: the CLI's runtimeconfig did not land in '$OUT'"
[ ! -e "$OUT/mw-plugin-test.deps.json" ]         || die "postcondition: the tester's deps.json is in '$OUT' — the TPA would be the tester's closure and every portal-only assembly unloadable"
cmp -s "$PORTAL/meshweaver-surface.manifest" "$OUT/meshweaver-surface.manifest" \
  || die "postcondition: the host's meshweaver-surface.manifest is not the portal's"

portal_dlls=$(find "$OUT" -maxdepth 1 -name '*.dll' | wc -l | tr -d ' ')
echo "gate host: '$OUT' = the portal's /app ($portal_dlls assemblies at the root, its manifest, its modules/) + $added tester-only file(s); $identical shared file(s) byte-identical, $differing shared file(s) differ (the portal's kept):${difflist:- none}"
