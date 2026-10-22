#!/usr/bin/env bash
#
# Executes the shared artifact store's OWNER job scripts (Doc/Architecture/SharedArtifactStore,
# increments 4 and 5) against scratch directories, and renders the chart's jobs for every shape the
# gate helper must refuse.
#
#   deploy/aks/scripts/test-artifact-store-jobs.sh
#   exit 0 = every case held; exit 1 = a case failed, or fewer cases ran than declared
#
#   deploy/helm/files/artifact-store-publish.sh   the one writer: generation + `_current` LAST
#   deploy/helm/files/artifact-store-sync.sh      one-way replication from an upstream registry
#   deploy/helm/files/artifact-store-modules.sh   the local build's module surface, from the store
#   deploy/helm/files/artifact-store-build.sh     the local-source build
#   deploy/helm/templates/memex-portal/_artifact-store.tpl + artifact-store-jobs.yaml
#
# Why executed rather than read: these scripts WRITE the store every portal of an environment reads,
# and the property that matters — a reader never resolves a half-written publication, and nothing is
# ever deleted — is a property of what they do on a filesystem, not of how they render. `oras`, `git`
# and `dotnet` are stubs recording their calls; the scripts run under the shells the pods run them
# with (sh for the publisher and the sync, bash for the build). Every input is in this repo, so there
# is no condition under which this may decline to run.
set -uo pipefail

SELF_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd -- "$SELF_DIR/../../.." && pwd)"
FILES="$REPO/deploy/helm/files"
PUBLISH="$FILES/artifact-store-publish.sh"
SYNC="$FILES/artifact-store-sync.sh"
BUILD="$FILES/artifact-store-build.sh"
MODULES="$FILES/artifact-store-modules.sh"
for f in "$PUBLISH" "$SYNC" "$BUILD" "$MODULES" "$FILES/compose-gate-host.sh"; do
  [ -f "$f" ] || { echo "::error::$f is missing"; exit 1; }
done

WORK="$(mktemp -d)"
trap 'chmod -R u+w "$WORK" 2>/dev/null; rm -rf "$WORK"' EXIT
fails=0
cases=0
EXPECTED_CASES=23

fail() { echo "::error::artifact-store jobs: $*"; fails=$((fails + 1)); }
pointer() { sed -n 's/^[[:space:]]*\([^[:space:]]\{1,\}\).*/\1/p' "$1/_current" 2>/dev/null | head -n 1; }

# stage <dir> <bundle>... — a sealed staged publication carrying the named bundles.
stage() {
  local dir="$1"; shift
  mkdir -p "$dir"
  : > "$dir/_complete"
  for b in "$@"; do printf 'bytes of %s\n' "$b" > "$dir/$b"; printf '%s\n' "$b" >> "$dir/_complete"; done
}

# ════════════════════════════ the publisher ════════════════════════════
R="$WORK/p/root"

cases=$((cases + 1))   # 1. a sealed staged publication lands as its generation, the pointer names it
stage "$WORK/p/s1" a.zip b.zip
if sh "$PUBLISH" "$WORK/p/s1" "$R" c003e001 plugins gen-1 >"$WORK/out" 2>&1; then
  [ -f "$R/c003e001/plugins/gen-1/_complete" ] || fail "case 1: the generation was not placed"
  [ "$(pointer "$R/c003e001/plugins")" = "gen-1" ] || fail "case 1: _current does not name gen-1"
  [ ! -e "$WORK/p/s1" ] || fail "case 1: the staged directory was copied, not moved (a rename is what makes it atomic)"
else
  fail "case 1: a sound publication was refused: $(cat "$WORK/out")"
fi

cases=$((cases + 1))   # 2. a second generation moves the pointer and DELETES NOTHING
stage "$WORK/p/s2" a.zip
sh "$PUBLISH" "$WORK/p/s2" "$R" c003e001 plugins gen-2 >"$WORK/out" 2>&1 || fail "case 2: refused: $(cat "$WORK/out")"
[ "$(pointer "$R/c003e001/plugins")" = "gen-2" ] || fail "case 2: _current does not name gen-2"
[ -f "$R/c003e001/plugins/gen-1/a.zip" ] || fail "case 2: the previous generation was deleted — retention is the prune job's, never the publisher's"

cases=$((cases + 1))   # 3. republishing a sealed generation writes nothing and is not an error
stage "$WORK/p/s3" other.zip
sh "$PUBLISH" "$WORK/p/s3" "$R" c003e001 plugins gen-2 >"$WORK/out" 2>&1 || fail "case 3: a re-publication of a sealed generation failed: $(cat "$WORK/out")"
[ ! -e "$R/c003e001/plugins/gen-2/other.zip" ] || fail "case 3: a sealed generation was rewritten"

cases=$((cases + 1))   # 4. an unsealed staged publication is refused and nothing becomes visible
mkdir -p "$WORK/p/s4"; printf x > "$WORK/p/s4/a.zip"
if sh "$PUBLISH" "$WORK/p/s4" "$R" c003e001 edu gen-1 >"$WORK/out" 2>&1; then fail "case 4: an unsealed publication was accepted"; fi
[ ! -e "$R/c003e001/edu/gen-1" ] || fail "case 4: an unsealed publication became visible"

cases=$((cases + 1))   # 5. a listed-but-absent bundle is refused before anything moves
stage "$WORK/p/s5" a.zip; printf 'missing.zip\n' >> "$WORK/p/s5/_complete"
if sh "$PUBLISH" "$WORK/p/s5" "$R" c003e001 edu gen-1 >"$WORK/out" 2>&1; then fail "case 5: a torn publication was accepted"; fi
[ ! -e "$R/c003e001/edu" ] || [ ! -e "$R/c003e001/edu/gen-1" ] || fail "case 5: a torn publication became visible"

cases=$((cases + 1))   # 6. an existing UNSEALED generation of that name is refused, the pointer untouched
mkdir -p "$R/c003e001/plugins/gen-3"; printf x > "$R/c003e001/plugins/gen-3/a.zip"
stage "$WORK/p/s6" a.zip
if sh "$PUBLISH" "$WORK/p/s6" "$R" c003e001 plugins gen-3 >"$WORK/out" 2>&1; then fail "case 6: a half-moved generation was adopted"; fi
[ "$(pointer "$R/c003e001/plugins")" = "gen-2" ] || fail "case 6: the pointer moved onto an unsealed generation"

cases=$((cases + 1))   # 7. names that escape the layout are refused
stage "$WORK/p/s7" a.zip
for bad in "../x" "_current" ".hidden" "a b"; do
  if sh "$PUBLISH" "$WORK/p/s7" "$R" c003e001 plugins "$bad" >"$WORK/out" 2>&1; then fail "case 7: generation '$bad' was accepted"; fi
done
printf '../escape\n' >> "$WORK/p/s7/_complete"
if sh "$PUBLISH" "$WORK/p/s7" "$R" c003e001 plugins gen-9 >"$WORK/out" 2>&1; then fail "case 7: a _complete listing an escaping path was accepted"; fi

cases=$((cases + 1))   # 20. a module bundle modules/_index lists but the publication lacks is refused
stage "$WORK/p/s20" a.zip
mkdir -p "$WORK/p/s20/modules"; printf 'MeshWeaver.Lost.module.nupkg\n' > "$WORK/p/s20/modules/_index"
if sh "$PUBLISH" "$WORK/p/s20" "$R" c003e001 edu gen-20 >"$WORK/out" 2>&1; then fail "case 20: a publication missing an indexed module was accepted"; fi
[ ! -e "$R/c003e001/edu/gen-20" ] || fail "case 20: a publication missing an indexed module became visible"

# ════════════════════════════ the sync ════════════════════════════
# A stub oras: `resolve` answers from $STUB/digests/<repo>:<tag> (absent ⇒ "not found"); `pull`
# copies $STUB/content/<digest>/ into -o; every call is appended to $STUB/calls.
STUB="$WORK/oras"
mkdir -p "$STUB/digests" "$STUB/content" "$STUB/bin"
cat > "$STUB/bin/oras" <<'ORAS'
#!/bin/sh
printf '%s\n' "$*" >> "$ORAS_STUB/calls"
verb="$1"; shift
out=""; ref=""
while [ $# -gt 0 ]; do
  case "$1" in
    --registry-config) shift 2 ;;
    --plain-http) shift ;;
    -o) out="$2"; shift 2 ;;
    *) ref="$1"; shift ;;
  esac
done
case "$verb" in
  resolve)
    key=$(printf '%s' "${ref#*/}" | tr '/' '_')
    if [ -f "$ORAS_STUB/digests/$key" ]; then cat "$ORAS_STUB/digests/$key"; exit 0; fi
    if [ -f "$ORAS_STUB/denied" ]; then echo "Error: denied" >&2; exit 1; fi
    echo "Error: $ref: not found" >&2; exit 1 ;;
  pull)
    digest="${ref#*@}"
    [ -d "$ORAS_STUB/content/$digest" ] || { echo "Error: pull failed" >&2; exit 1; }
    cp -R "$ORAS_STUB/content/$digest/." "$out/"; exit 0 ;;
esac
exit 2
ORAS
chmod +x "$STUB/bin/oras"
D1="sha256:1111111111111111111111111111111111111111111111111111111111111111"
printf '%s\n' "$D1" > "$STUB/digests/plugins_plugins:c003e001"
stage "$STUB/content/$D1" Store.zip Hosting.zip
printf 'c003e001\n' > "$WORK/identity"
SR="$WORK/s/root"
sync_run() {
  env -i PATH="$PATH" ORAS_STUB="$STUB" ORAS="$STUB/bin/oras" SYNC_REGISTRY="cr.example.com" \
    SYNC_ROOT="$SR" SYNC_IDENTITY_FILE="$WORK/identity" PUBLISH="$PUBLISH" "$@" \
    sh "$SYNC" >"$WORK/out" 2>&1
}

cases=$((cases + 1))   # 8. a sealed upstream publication replicates as `upstream-<digest>`, pointer last
G1="upstream-$(printf '%s' "${D1#sha256:}" | cut -c1-24)"
if sync_run SYNC_SOURCES="plugins"; then
  [ -f "$SR/c003e001/plugins/$G1/Store.zip" ] || fail "case 8: the publication was not replicated: $(cat "$WORK/out")"
  [ "$(pointer "$SR/c003e001/plugins")" = "$G1" ] || fail "case 8: _current does not name $G1"
  [ -z "$(ls -A "$SR/.artifact-store-staging" 2>/dev/null)" ] || fail "case 8: staging was left behind"
else
  fail "case 8: the sync failed: $(cat "$WORK/out")"
fi

cases=$((cases + 1))   # 9. an unchanged upstream is a no-op: no pull at all
: > "$STUB/calls"
sync_run SYNC_SOURCES="plugins" || fail "case 9: a no-op sync failed: $(cat "$WORK/out")"
grep -q '^pull' "$STUB/calls" && fail "case 9: an unchanged publication was pulled again"
grep -q '1 already current' "$WORK/out" || fail "case 9: the summary does not count it current: $(cat "$WORK/out")"

cases=$((cases + 1))   # 10. an absent tag is UNSEALED upstream: nothing written, exit 0
sync_run SYNC_SOURCES="education" || fail "case 10: an unsealed upstream source failed the run: $(cat "$WORK/out")"
[ ! -e "$SR/c003e001/education" ] || fail "case 10: something was written for an unsealed source"

cases=$((cases + 1))   # 11. a new upstream digest adds a generation and KEEPS the old one
D2="sha256:2222222222222222222222222222222222222222222222222222222222222222"
printf '%s\n' "$D2" > "$STUB/digests/plugins_plugins:c003e001"
stage "$STUB/content/$D2" Store.zip
G2="upstream-$(printf '%s' "${D2#sha256:}" | cut -c1-24)"
sync_run SYNC_SOURCES="plugins" || fail "case 11: failed: $(cat "$WORK/out")"
[ "$(pointer "$SR/c003e001/plugins")" = "$G2" ] || fail "case 11: _current did not advance to $G2"
[ -f "$SR/c003e001/plugins/$G1/Hosting.zip" ] || fail "case 11: the superseded generation was deleted"

cases=$((cases + 1))   # 12. a torn upstream publication is refused, the run is red, the store keeps serving
D3="sha256:3333333333333333333333333333333333333333333333333333333333333333"
printf '%s\n' "$D3" > "$STUB/digests/plugins_plugins:c003e001"
stage "$STUB/content/$D3" Store.zip; printf 'Lost.zip\n' >> "$STUB/content/$D3/_complete"
if sync_run SYNC_SOURCES="plugins"; then fail "case 12: a torn publication left the run green"; fi
[ "$(pointer "$SR/c003e001/plugins")" = "$G2" ] || fail "case 12: the pointer moved off the generation that was serving"

cases=$((cases + 1))   # 13. a denied resolve is red and writes nothing; extra identities are replicated too
: > "$STUB/denied"
if sync_run SYNC_SOURCES="education"; then fail "case 13: a denied resolve left the run green"; fi
rm -f "$STUB/denied"
printf '%s\n' "$D1" > "$STUB/digests/plugins_plugins:c003e002"
sync_run SYNC_SOURCES="plugins" SYNC_IDENTITIES="c003e002" >/dev/null 2>&1
[ "$(pointer "$SR/c003e002/plugins")" = "$G1" ] || fail "case 13: the extra identity c003e002 was not replicated: $(cat "$WORK/out")"

# ════════════════════════════ the local-source build ════════════════════════════
# A portal /app and a tester /app the real compose-gate-host.sh accepts, a stub git (a clone is a
# directory and a fixed commit) and a stub dotnet (the compile writes a bake keyed to $BAKE_ID, and
# REFUSES an archive as --module: the real compile loads an entry assembly).
B="$WORK/b"
mkdir -p "$B/portal" "$B/tester" "$B/bin"
printf 'MeshWeaver.Layout=aaaa\n' > "$B/portal/meshweaver-surface.manifest"
printf 'c003e001\n' > "$B/portal/meshweaver-framework.identity"
printf '{}' > "$B/portal/Memex.Portal.Distributed.deps.json"
printf '{}' > "$B/portal/Memex.Portal.Distributed.runtimeconfig.json"
printf 'TESTER' > "$B/tester/mw-plugin-test.dll"
printf '{}' > "$B/tester/mw-plugin-test.runtimeconfig.json"
cat > "$B/bin/git" <<'GIT'
#!/bin/sh
printf '%s\n' "$*" >> "$BUILD_STUB/git.calls"
for a in "$@"; do last="$a"; done
case " $* " in
  *" clone "*) mkdir -p "$last"; exit 0 ;;
  *" rev-parse HEAD"*) echo "abcdef0123456789abcdef0123456789abcdef01"; exit 0 ;;
esac
exit 0
GIT
cat > "$B/bin/dotnet" <<'DOTNET'
#!/bin/sh
printf '%s\n' "$*" >> "$BUILD_STUB/dotnet.calls"
out=""
while [ $# -gt 0 ]; do
  case "$1" in
    --output) out="$2" ;;
    --module) case "$2" in *.dll) [ -f "$2" ] || { echo "no such module $2" >&2; exit 4; } ;; *) echo "--module expects an assembly, got $2" >&2; exit 4 ;; esac ;;
  esac
  shift
done
[ -n "$out" ] || exit 3
printf '%s\n' "$BAKE_ID" > "$out/framework-mvid.txt"
printf 'zip' > "$out/Fabrikam.Models.zip"
exit "${DOTNET_EXIT:-0}"
DOTNET
chmod +x "$B/bin/git" "$B/bin/dotnet"
BR="$WORK/b/root"
# The upstream's module set, as the sync leaves it: a real module bundle (a zip carrying
# meshweaver/manifest.json and meshweaver/modules/<entry>.dll) under plugins' current generation.
mkpkg() { # <dir> <entry> <out.nupkg>
  rm -rf "$1"; mkdir -p "$1/meshweaver/modules"
  printf '{"plugin":"Store","module":{"assemblyName":"%s","version":"1.0.0"}}' "$2" > "$1/meshweaver/manifest.json"
  printf 'IL' > "$1/meshweaver/modules/$2.dll"; printf 'dep' > "$1/meshweaver/modules/Dep.dll"
  (cd "$1" && zip -q -r "$3" meshweaver)
}
mkdir -p "$BR/c003e001/plugins/gen-u/modules"
printf 'Store.zip\n' > "$BR/c003e001/plugins/gen-u/_complete"; printf z > "$BR/c003e001/plugins/gen-u/Store.zip"
printf 'MeshWeaver.Store.module.nupkg\n' > "$BR/c003e001/plugins/gen-u/modules/_index"
mkpkg "$WORK/pkg" MeshWeaver.Store "$BR/c003e001/plugins/gen-u/modules/MeshWeaver.Store.module.nupkg"
printf 'gen-u\n' > "$BR/c003e001/plugins/_current"
printf 'secret-token-value\n' > "$B/token"
printf 'c003e001\n' > "$B/identity"
build_run() {
  : > "$B/git.calls"; : > "$B/dotnet.calls"
  rm -rf "$B/work"; mkdir -p "$B/work"
  env -i PATH="$PATH" MODULES_ROOT="$BR" MODULES_SOURCES="plugins" MODULES_IDENTITY_FILE="$B/identity" \
    MODULES_OUT="$B/work/ext" sh "$MODULES" >"$WORK/out" 2>&1 || return 9
  env -i PATH="$B/bin:$PATH" BUILD_STUB="$B" BAKE_ID="${BAKE_ID:-c003e001}" \
    BUILD_SOURCE="fabrikam-models" BUILD_REPOSITORY="https://git.example.com/fabrikam/models.git" \
    BUILD_ROOT="$BR" BUILD_TESTER_APP="$B/tester" BUILD_PORTAL_APP="$B/portal" BUILD_WORK="$B/work" \
    BUILD_MODULES_DIR="$B/work/ext" BUILD_TOKEN_FILE="$B/token" \
    bash "$BUILD" >>"$WORK/out" 2>&1
}
P="$BR/c003e001/fabrikam-models"

cases=$((cases + 1))   # 14. a local source builds and publishes as `local-<commit>-<inputs>`, sealed, pointer last
LG=""
if BAKE_ID=c003e001 build_run; then
  LG=$(pointer "$P")
  case "$LG" in local-abcdef012345-????????) ;; *) fail "case 14: _current names '$LG', not local-<commit>-<inputs>: $(cat "$WORK/out")" ;; esac
  [ "$(cat "$P/$LG/_complete" 2>/dev/null)" = "Fabrikam.Models.zip" ] || fail "case 14: _complete does not list the bundle"
  for m in source-commit.txt repository.txt architecture.txt modules/_index framework-mvid.txt; do
    [ -f "$P/$LG/$m" ] || fail "case 14: the publication carries no $m"
  done
  [ "$(cat "$P/$LG/repository.txt" 2>/dev/null)" = "fabrikam/models" ] || fail "case 14: repository.txt is '$(cat "$P/$LG/repository.txt" 2>/dev/null)'"
else
  fail "case 14: the build failed: $(cat "$WORK/out")"
fi

cases=$((cases + 1))   # 15. the compile ran on the COMPOSED host, against the portal's /app, with the module's ENTRY ASSEMBLY
grep -q "$B/work/host/mw-plugin-test.dll compile" "$B/dotnet.calls" || fail "case 15: the compile did not run from the composed host: $(cat "$B/dotnet.calls")"
grep -q -- "--app $B/portal" "$B/dotnet.calls" || fail "case 15: the compile was not pointed at the portal's /app"
grep -q -- "--module $B/work/ext/modules/MeshWeaver.Store/MeshWeaver.Store.dll" "$B/dotnet.calls" || fail "case 15: the upstream module's entry assembly was not composed: $(cat "$B/dotnet.calls")"
[ -f "$B/work/ext/modules/MeshWeaver.Store/Dep.dll" ] || fail "case 15: the module's closure was not laid beside its entry assembly"
grep -q -- "--source-sha abcdef0123456789abcdef0123456789abcdef01" "$B/dotnet.calls" || fail "case 15: the commit was not recorded"

cases=$((cases + 1))   # 16. the read token travels as a header, never in the URL or the log
grep -q 'secret-token-value' "$WORK/out" && fail "case 16: the token reached the log"
grep -q 'https://[^ ]*@' "$B/git.calls" && fail "case 16: the token was put into the clone URL"
grep -q 'http.extraHeader=Authorization: Basic' "$B/git.calls" || fail "case 16: the token was not sent as a header: $(cat "$B/git.calls")"

cases=$((cases + 1))   # 17. unchanged inputs build nothing
BAKE_ID=c003e001 build_run || fail "case 17: a no-op build failed: $(cat "$WORK/out")"
[ -s "$B/dotnet.calls" ] && fail "case 17: an already-current commit was compiled again"

cases=$((cases + 1))   # 21. a new upstream module set — same commit — names a NEW generation and rebuilds
mkdir -p "$BR/c003e001/plugins/gen-v/modules"
printf 'Store.zip\n' > "$BR/c003e001/plugins/gen-v/_complete"; printf z > "$BR/c003e001/plugins/gen-v/Store.zip"
printf 'MeshWeaver.Store.module.nupkg\n' > "$BR/c003e001/plugins/gen-v/modules/_index"
mkpkg "$WORK/pkg2" MeshWeaver.Store "$BR/c003e001/plugins/gen-v/modules/MeshWeaver.Store.module.nupkg"
printf 'gen-v\n' > "$BR/c003e001/plugins/_current"
BAKE_ID=c003e001 build_run || fail "case 21: the rebuild failed: $(cat "$WORK/out")"
[ -s "$B/dotnet.calls" ] || fail "case 21: a changed module surface did not rebuild"
LG2=$(pointer "$P")
[ -n "$LG" ] && [ "$LG2" != "$LG" ] || fail "case 21: the generation name did not change with the module surface ($LG → $LG2)"
case "$LG2" in local-abcdef012345-*) ;; *) fail "case 21: '$LG2' does not keep the commit" ;; esac

cases=$((cases + 1))   # 22. the module surface fails CLOSED: a listed bundle the store lacks stops the build
mv "$BR/c003e001/plugins/gen-v/modules/MeshWeaver.Store.module.nupkg" "$WORK/held.nupkg"
if BAKE_ID=c003e001 build_run; then fail "case 22: a short module surface was compiled against"; fi
mv "$WORK/held.nupkg" "$BR/c003e001/plugins/gen-v/modules/MeshWeaver.Store.module.nupkg"

cases=$((cases + 1))   # 18. a bake keyed to ANOTHER identity is refused — inert bundles are never published
rm -rf "$BR/c003e001/fabrikam-models"
if BAKE_ID=c003e999 build_run; then fail "case 18: a bake under a foreign identity was published"; fi
[ ! -e "$BR/c003e001/fabrikam-models/_current" ] || fail "case 18: the foreign-identity bake became visible"
[ ! -e "$BR/c003e999" ] || fail "case 18: the bake was published under its foreign identity"

# ════════════════════════════ the chart's gate ════════════════════════════
cases=$((cases + 1))   # 19. the helper renders nothing by default and refuses every half-declared shape
if command -v helm >/dev/null 2>&1; then
  CHART="$REPO/deploy/helm"
  out=$(helm template t "$CHART" 2>&1) || fail "case 19: the default render failed: $out"
  printf '%s' "$out" | grep -q 'memex-artifact-store' && fail "case 19: the default render carries an artifact-store job"
  base=(--set portal.image=cr.example.com/memex-portal-ai:3.1.0-ci.100 --set artifactStore.claimName=control-artifacts)
  up=(--set artifactStore.upstream.registry=cr.example.com --set 'artifactStore.upstream.sources={plugins}' --set artifactStore.upstream.pullSecret=upstream-pull)
  out=$(helm template t "$CHART" "${base[@]}" "${up[@]}" 2>&1) || fail "case 19: a sound upstream did not render: $out"
  printf '%s' "$out" | grep -q 'name: "memex-artifact-store-sync"' || fail "case 19: the sync CronJob did not render"
  printf '%s' "$out" | grep -q '/artifacts/linux-x64/prebuilt-bundles' || fail "case 19: the sync does not write the linux-x64 store"
  refuse() { local why="$1"; shift; if helm template t "$CHART" "$@" >/dev/null 2>&1; then fail "case 19: rendered although $why"; fi; }
  refuse "the pull secret is missing" "${base[@]}" --set artifactStore.upstream.registry=cr.example.com --set 'artifactStore.upstream.sources={plugins}'
  refuse "no source is named" "${base[@]}" --set artifactStore.upstream.registry=cr.example.com --set artifactStore.upstream.pullSecret=p
  refuse "the registry carries a scheme" "${base[@]}" --set artifactStore.upstream.registry=https://cr.example.com --set 'artifactStore.upstream.sources={plugins}' --set artifactStore.upstream.pullSecret=p
  refuse "the store claim is missing" --set portal.image=cr.example.com/memex-portal-ai:3.1.0 "${up[@]}"
  lb=(--set artifactStore.localBuild.testerImageRepository=cr.example.com/mw-plugin-test --set 'artifactStore.localBuild.sources[0].repository=https://git.example.com/fabrikam/models.git')
  out=$(helm template t "$CHART" "${base[@]}" "${lb[@]}" --set 'artifactStore.localBuild.sources[0].source=fabrikam-models' 2>&1) || fail "case 19: a sound local source did not render: $out"
  printf '%s' "$out" | grep -q 'image: "cr.example.com/mw-plugin-test:3.1.0-ci.100"' || fail "case 19: the tester does not follow the portal's tag"
  refuse "a local source shadows an upstream one" "${base[@]}" "${up[@]}" "${lb[@]}" --set 'artifactStore.localBuild.sources[0].source=plugins'
  refuse "the portal image is pinned by digest" --set portal.image=cr.example.com/memex-portal-ai@sha256:abc --set artifactStore.claimName=c "${lb[@]}" --set 'artifactStore.localBuild.sources[0].source=x'
  refuse "the tester repository is missing" "${base[@]}" --set 'artifactStore.localBuild.sources[0].source=x' --set 'artifactStore.localBuild.sources[0].repository=https://git.example.com/x.git'
  refuse "the tester repository carries a tag" "${base[@]}" --set artifactStore.localBuild.testerImageRepository=cr.example.com/mw-plugin-test:3.1 --set 'artifactStore.localBuild.sources[0].source=x' --set 'artifactStore.localBuild.sources[0].repository=https://git.example.com/x.git'
  refuse "upstream sources and a secret are stated with no registry" "${base[@]}" --set 'artifactStore.upstream.sources={plugins}' --set artifactStore.upstream.pullSecret=p
  refuse "a module source is not a registry name" "${base[@]}" "${lb[@]}" --set 'artifactStore.localBuild.sources[0].source=x' --set 'artifactStore.localBuild.moduleSources={Bad/Name}'
  refuse "the repository is not https" "${base[@]}" --set artifactStore.localBuild.testerImageRepository=t --set 'artifactStore.localBuild.sources[0].source=x' --set 'artifactStore.localBuild.sources[0].repository=git@git.example.com:x.git'
else
  fail "case 19: helm is not on PATH — the chart half cannot be checked"
fi

cases=$((cases + 1))   # 23. a control with NO upstream composes its build's modules from the DECLARED module sources
if command -v helm >/dev/null 2>&1; then
  out=$(helm template t "$CHART" --set portal.image=cr.example.com/memex-portal-ai:3.1.0-ci.100 --set artifactStore.claimName=c \
    --set artifactStore.localBuild.testerImageRepository=cr.example.com/mw-plugin-test \
    --set 'artifactStore.localBuild.sources[0].source=x' --set 'artifactStore.localBuild.sources[0].repository=https://git.example.com/x.git' \
    --set 'artifactStore.localBuild.moduleSources={plugins,education}' 2>&1) || fail "case 23: render failed: $out"
  printf '%s' "$out" | grep -A1 'name: "MODULES_SOURCES"' | grep -q 'value: "plugins education"' \
    || fail "case 23: the declared module sources did not reach the modules init container"
  printf '%s' "$out" | grep -q 'memex-artifact-store-sync' && fail "case 23: a control with no upstream rendered a sync job"
fi

# The copy of compose-gate-host.sh the build runs must BE the platform's — one implementation.
cmp -s "$FILES/compose-gate-host.sh" "$REPO/.github/scripts/compose-gate-host.sh" \
  || fail "deploy/helm/files/compose-gate-host.sh differs from .github/scripts/compose-gate-host.sh — copy it byte for byte"

if [ "$cases" -ne "$EXPECTED_CASES" ]; then
  fail "ran $cases case(s), expected $EXPECTED_CASES — a case was added or dropped without updating the count"
fi
if [ "$fails" -ne 0 ]; then
  echo "artifact-store jobs: $fails failure(s) over $cases case(s)"
  exit 1
fi
echo "artifact-store jobs: all $cases case(s) held"
exit 0
