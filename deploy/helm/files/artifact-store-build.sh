#!/usr/bin/env bash
# artifact-store-build — builds ONE local source on the environment's control instance and publishes
# its bundles into the control's own artifact store (Doc/Architecture/SharedArtifactStore, increment 5).
#
# "Local" means a repository this environment tracks itself and the upstream build server does not
# build. It runs as the container of the CronJob the chart renders per `artifactStore.localSources`
# entry, IN THE PORTAL IMAGE — so the compiler is the platform the environment's portals run (policy
# "the image is the compiler"), and the bundles are keyed to the identity those portals adopt.
#
#   1. reads the portal image's framework identity (the file the build wrote beside /app);
#   2. clones the repository at its ref (the read token, when declared, travels as an HTTP header,
#      never in a URL or a log line) and names the generation by the commit: `local-<12 hex>`;
#   3. a generation already sealed and current for that commit is the answer — nothing is built;
#   4. composes the GATE HOST — the portal's /app with the tester CLI beside it — with
#      compose-gate-host.sh (a byte-for-byte copy of the platform's own, pinned by the chart gate);
#   5. composes the module surface from the store itself: every `modules/*.module.nupkg` the CURRENT
#      generation of each upstream source lists for this identity (the modules the environment's
#      portals run), the first source listing a name winning;
#   6. runs the tester's `compile` verb against the portal's /app and its implementation frameworks;
#   7. checks the bake is keyed to the portal's identity (a bake under another identity is inert),
#      assembles a sealed publication — the bundles, source-commit.txt, repository.txt,
#      architecture.txt, an empty modules/_index (a local source owns no module), the platform
#      surface when the bake wrote one, and `_complete` LAST — and hands it to
#      artifact-store-publish.sh, which renames it into place and moves `_current` last.
#
# Nothing is deleted. A failed build leaves the generation that was current serving.
#
# Environment (rendered by the chart):
#   BUILD_SOURCE            the publication source name                                   required
#   BUILD_REPOSITORY        the https clone URL                                           required
#   BUILD_REF               the branch or tag                                    default: main
#   BUILD_TOKEN_FILE        a file holding a read token                                   optional
#   BUILD_ROOT              the store's per-architecture bundle root                      required
#   BUILD_ARCHITECTURE      the runtime identifier the store subtree names     default: linux-x64
#   BUILD_UPSTREAM_SOURCES  space-separated sources whose module sets are composed        optional
#   BUILD_TESTER_APP        the tester image's /app, copied out by an init container      required
#   BUILD_PORTAL_APP        the portal's /app                                     default: /app
#   BUILD_WORK              a writable scratch directory                         default: /work
#   BUILD_IDENTITY_FILE     the image's identity file   default: $BUILD_PORTAL_APP/meshweaver-framework.identity
#   COMPOSE / PUBLISH       the two helpers                   default: beside this file
set -euo pipefail

log() { printf 'artifact-store-build: %s\n' "$*"; }
die() { printf 'artifact-store-build: ERROR: %s\n' "$*" >&2; exit 1; }

source_name="${BUILD_SOURCE:-}"
repository="${BUILD_REPOSITORY:-}"
ref="${BUILD_REF:-main}"
token_file="${BUILD_TOKEN_FILE:-}"
root="${BUILD_ROOT:-}"
architecture="${BUILD_ARCHITECTURE:-linux-x64}"
upstreams="${BUILD_UPSTREAM_SOURCES:-}"
tester_app="${BUILD_TESTER_APP:-}"
portal_app="${BUILD_PORTAL_APP:-/app}"
work="${BUILD_WORK:-/work}"
identity_file="${BUILD_IDENTITY_FILE:-$portal_app/meshweaver-framework.identity}"
here="$(cd "$(dirname "$0")" && pwd)"
compose="${COMPOSE:-$here/compose-gate-host.sh}"
publish="${PUBLISH:-$here/artifact-store-publish.sh}"
dotnet_cmd="${DOTNET:-dotnet}"
shared_frameworks="${BUILD_SHARED_FRAMEWORKS:-/usr/share/dotnet/shared}"

[ -n "$source_name" ] || die "BUILD_SOURCE is not set"
case "$source_name" in *[!a-z0-9._-]*|.|..) die "BUILD_SOURCE '$source_name' is not a registry name component" ;; esac
case "$repository" in https://*) ;; *) die "BUILD_REPOSITORY '$repository' is not an https URL" ;; esac
case "$ref" in *[!A-Za-z0-9._/-]*|'') die "BUILD_REF '$ref' is not a branch or tag name" ;; esac
case "$architecture" in *[!a-z0-9-]*|'') die "BUILD_ARCHITECTURE '$architecture' is not a runtime identifier" ;; esac
[ -n "$root" ] || die "BUILD_ROOT is not set"
[ -n "$tester_app" ] && [ -d "$tester_app" ] || die "BUILD_TESTER_APP '$tester_app' is not a directory — the init container copies the tester image's /app there"
[ -r "$identity_file" ] || die "the portal image carries no identity file at '$identity_file' — a bake must be keyed to the identity the portals adopt"
[ -r "$compose" ] || die "compose-gate-host.sh is not readable at '$compose'"
[ -r "$publish" ] || die "artifact-store-publish.sh is not readable at '$publish'"

identity=$(sed -n 's/^[[:space:]]*\([^[:space:]]\{1,\}\).*/\1/p' "$identity_file" | head -n 1)
case "$identity" in *[!A-Za-z0-9]*|'') die "the identity file '$identity_file' holds no identity" ;; esac

# ── 2. the checkout ─────────────────────────────────────────────────────────────────────────────
repo="$work/repo"
rm -rf "$repo"
git_args=(-c advice.detachedHead=false)
if [ -n "$token_file" ]; then
  [ -r "$token_file" ] || die "BUILD_TOKEN_FILE '$token_file' is not readable"
  token=$(tr -d '[:space:]' < "$token_file")
  [ -n "$token" ] || die "BUILD_TOKEN_FILE '$token_file' is empty"
  basic=$(printf 'x-access-token:%s' "$token" | base64 | tr -d '\n')
  git_args+=(-c "http.extraHeader=Authorization: Basic $basic")
fi
git "${git_args[@]}" clone --quiet --depth 1 --branch "$ref" "$repository" "$repo" \
  || die "could not clone $repository at $ref"
sha=$(git -C "$repo" rev-parse HEAD)
generation="local-${sha:0:12}"
log "'$source_name': $repository@$ref is $sha → generation $generation for identity $identity"

source_dir="$root/$identity/$source_name"
if [ -f "$source_dir/$generation/_complete" ] \
   && [ "$(sed -n 's/^[[:space:]]*\([^[:space:]]\{1,\}\).*/\1/p' "$source_dir/_current" 2>/dev/null | head -n 1)" = "$generation" ]; then
  log "generation $generation is already sealed and current — nothing to build"
  exit 0
fi

# ── 4. the gate host ────────────────────────────────────────────────────────────────────────────
host="$work/host"
rm -rf "$host"
bash "$compose" "$portal_app" "$tester_app" "$host"

# ── 5. the module surface, from the store ───────────────────────────────────────────────────────
module_args=()
seen=" "
for upstream in $upstreams; do
  dir="$root/$identity/$upstream"
  [ -d "$dir" ] || { log "upstream '$upstream' holds no publication for $identity — no modules from it"; continue; }
  pointer=$(sed -n 's/^[[:space:]]*\([^[:space:]]\{1,\}\).*/\1/p' "$dir/_current" 2>/dev/null | head -n 1 || true)
  [ -n "$pointer" ] && dir="$dir/$pointer"
  [ -f "$dir/_complete" ] || { log "upstream '$upstream' is not sealed for $identity — no modules from it"; continue; }
  [ -f "$dir/modules/_index" ] || continue
  while IFS= read -r name || [ -n "$name" ]; do
    name=$(printf '%s' "$name" | tr -d '[:space:]')
    [ -n "$name" ] || continue
    key=$(printf '%s' "$name" | tr '[:upper:]' '[:lower:]')
    case "$seen" in *" $key "*) continue ;; esac
    [ -f "$dir/modules/$name" ] || die "'$upstream' lists module $name for $identity but the store does not hold it — the seal and the store disagree"
    seen="$seen$key "
    module_args+=(--module "$dir/modules/$name")
  done < "$dir/modules/_index"
done
log "module surface: $(( ${#module_args[@]} / 2 )) module bundle(s) from [${upstreams:-none}]"

# ── 6. the compile ──────────────────────────────────────────────────────────────────────────────
bake="$work/bake"
rm -rf "$bake"
mkdir -p "$bake"
allow_args=()
[ -f "$repo/plugin-gate.allow" ] && allow_args=(--allow "$repo/plugin-gate.allow")
"$dotnet_cmd" "$host/mw-plugin-test.dll" compile "$repo" \
  --app "$portal_app" --shared-frameworks "$shared_frameworks" \
  ${allow_args[@]+"${allow_args[@]}"} ${module_args[@]+"${module_args[@]}"} \
  --output "$bake" --source-sha "$sha" \
  || die "the compile of '$source_name' at $sha failed — nothing published; the current generation keeps serving"

# ── 7. the publication ──────────────────────────────────────────────────────────────────────────
[ -s "$bake/framework-mvid.txt" ] || die "the compile wrote no framework-mvid.txt — the bake stage regressed; refusing to publish bytes that were never keyed"
baked=$(tr -d '[:space:]' < "$bake/framework-mvid.txt")
[ "$baked" = "$identity" ] || die "the bake is keyed to '$baked' but the portal image is '$identity' — bundles under an identity no portal asks for are inert; refusing"
shopt -s nullglob
zips=("$bake"/*.zip)
shopt -u nullglob
[ "${#zips[@]}" -gt 0 ] || die "the compile of '$source_name' at $sha emitted no bundle — nothing to publish"

staging="$root/.artifact-store-staging/build-$source_name-$$"
rm -rf "$staging"
mkdir -p "$staging/modules"
for zip in "${zips[@]}"; do cp "$zip" "$staging/"; done
[ -f "$bake/platform-surface.json" ] && cp "$bake/platform-surface.json" "$staging/"
[ -f "$bake/sealed-packages.json" ] && cp "$bake/sealed-packages.json" "$staging/"
printf '%s\n' "$identity" > "$staging/framework-mvid.txt"
printf '%s\n' "$sha" > "$staging/source-commit.txt"
repo_name="${repository#https://}"; repo_name="${repo_name#*/}"; repo_name="${repo_name%.git}"
printf '%s\n' "$repo_name" > "$staging/repository.txt"
printf '%s\n' "$architecture" > "$staging/architecture.txt"
: > "$staging/modules/_index"
# The seal, LAST: the sorted bundle names — the listing every reader checks.
for zip in "${zips[@]}"; do basename "$zip"; done | LC_ALL=C sort > "$staging/_complete"

if sh "$publish" "$staging" "$root" "$identity" "$source_name" "$generation"; then
  rm -rf "$staging"
  log "published '$source_name' at $sha: ${#zips[@]} bundle(s) as $generation for $identity"
else
  rm -rf "$staging"
  die "the publisher refused '$source_name' at $sha"
fi
