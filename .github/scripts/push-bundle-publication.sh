#!/usr/bin/env bash
#
# push-bundle-publication.sh --registry <host> --source <name> --identity <id> --dir <publication dir>
#                            [--tag-run <run id>] [--release <version>] [--skip-if-published]
#                            [--registry-config <docker config.json>] [--plain-http] [--oras <binary>]
# push-bundle-publication.sh --registry <host> --source <name> --identity <id> --published-commit
#                            (read only: print "<commit> <layout> <current layout>" for the tag, or nothing)
#
# Publishes ONE sealed publication — the directory publish-bake-bundles.sh writes for one source
# and one framework identity (`<package>.zip` per bundle, `modules/<package>.module.nupkg` beside
# it, `modules/_index`, `source-commit.txt`, `repository.txt`, `architecture.txt`,
# `platform-surface.json`, and `_complete` listing the bundles) — to the fleet registry as OCI
# artifacts, with ORAS, as the publisher account. Design: Doc/Architecture/PluginBundlesInTheRegistry.
#
# What it pushes, in this order, and the order IS the seal:
#   1. every bundle as an image manifest in `plugins/<source>/<package>`: the `.zip` as a layer of
#      media type application/vnd.meshweaver.bundle.v1.zip, the `.module.nupkg` (when present) as
#      application/vnd.meshweaver.module.v1.nupkg, tagged `<identity>` (and `<identity>-<run>`);
#      the same manifest is then copied by digest into the publication repository
#      `plugins/<source>`, untagged, because an index may only reference manifests of its own
#      repository;
#      (the module is matched to its bundle CASE-INSENSITIVELY: the lanes write it lowercased);
#   2. the sidecars — EVERY file of the publication that is not a bundle or a module riding with
#      one (so a module whose package bakes no bundle travels here, under its own title) — as one
#      image manifest in `plugins/<source>`, untagged, each file a layer titled with its
#      publication-relative name (so `oras pull` writes it back under that name, byte for byte),
#      with a config blob of media type application/vnd.meshweaver.publication.v1+json carrying
#      the publication's facts (identity, source, commit, repository, architecture, release, the
#      bundles with their digests, the sidecar names); a sidecar added by a later publisher rides
#      along without a change here or in the consumer;
#   3. the publication INDEX (application/vnd.oci.image.index.v1+json) naming the sidecar manifest
#      and every bundle manifest by digest, untagged — content-addressed, so two lanes publishing
#      the same bytes push the same index and a differing set is a different digest;
#   4. the tags, LAST: `<identity>-<run>` (immutable, when --tag-run is given) and then
#      `<identity>` — the move of that tag is the seal. Until it moves, nothing above is visible
#      under the tag; after it moves, a consumer that resolved the previous digest keeps reading
#      the previous generation, which stays resident by digest.
# With --release <version> it also records the release's framework identity as an image manifest
# with an empty layer in `plugins/releases`, tag `<version>`, config {"identity","version"}.
# (`plugins/_releases`, as first drafted, is not a legal repository name: a path component must
# start with an alphanumeric — measured against distribution 3.1.1 and refused by ORAS itself.)
#
# 🚨 Refuses a publication that is not whole: no `_complete`, an empty `_complete`, a listed bundle
# that is absent, a module listed in `modules/_index` that is absent, a source name the registry
# grammar cannot spell (or the reserved `releases`), a file name the config blob cannot carry.
# A refusal exits 1 before the first push; a failure mid-way exits 1 with the tag unmoved.
#
# With --skip-if-published the registry gets the SAME sealed-skip the share targets have
# (publish-bake-bundles.sh: content × framework): when `plugins/<source>:<identity>` already
# resolves AND its sidecar config's `sourceCommit` equals this publication's source-commit.txt,
# nothing is pushed — the tag already names a complete publication of this content, and a second
# compilation of the same commit would only add a full set of blobs the registry never garbage-
# collects. The release identity (--release) is still recorded, exactly as the share's release
# marker is written on every run. 🚨 The skip needs POSITIVE proof — a resolvable tag, a readable
# index and sidecar config, an equal commit; anything else (a tag that is absent, an unreadable
# index, a different commit) PUSHES, because pushing is idempotent and never leaves the registry
# worse, while a wrong skip would freeze the tag on a publication this run was asked to replace.
# The proof includes the publication LAYOUT: the sidecar config carries `"layout":<n>`
# (PUBLICATION_LAYOUT below), and a published config whose layout differs — or that has none,
# i.e. every publication pushed before modules were matched to their bundles case-insensitively —
# is REPUBLISHED even at the same commit. An unchanged source would otherwise keep its torn
# publication forever, since its commit never moves. Bump PUBLICATION_LAYOUT whenever what this
# script pushes for the same content changes.
#
# Prints facts the caller can record (register-publication): `bundle-publication: index=<digest>
# bundles=<n> …` and one `bundle-publication: bundle=<package> digest=<digest> module=<yes|no>` per
# bundle; also `index_digest` and `bundle_count` into $GITHUB_OUTPUT when set.
#
# Callers: main-cd.yml (`publish-bake`, the platform's own content) and node-repo-publish-bake.yml
# (MeshWeaver.Plugins and every satellite), after the share seal succeeds,
# on the directory publish-bake-bundles.sh materialises (BAKE_PUBLICATION_DIR). Behaviour tests:
# MeshWeaver.Plugins .github/scripts/test-bundle-registry.py (its chart-gate's `Bundle registry scripts (executed)`).
set -euo pipefail

usage() {
  sed -n '2,/^set -euo/p' "${BASH_SOURCE[0]}" | sed '$d' | sed 's/^# \{0,1\}//'
}

REGISTRY=""; SOURCE=""; IDENTITY=""; DIR=""; TAG_RUN=""; RELEASE=""; REGISTRY_CONFIG=""; PLAIN_HTTP=0; SKIP_IF_PUBLISHED=0; PUBLISHED_COMMIT_ONLY=0
ORAS="${ORAS:-oras}"
while [ $# -gt 0 ]; do
  case "$1" in
    --registry) REGISTRY="${2:-}"; shift 2 ;;
    --source) SOURCE="${2:-}"; shift 2 ;;
    --identity) IDENTITY="${2:-}"; shift 2 ;;
    --dir) DIR="${2:-}"; shift 2 ;;
    --tag-run) TAG_RUN="${2:-}"; shift 2 ;;
    --release) RELEASE="${2:-}"; shift 2 ;;
    --skip-if-published) SKIP_IF_PUBLISHED=1; shift ;;
    --published-commit) PUBLISHED_COMMIT_ONLY=1; shift ;;
    --registry-config) REGISTRY_CONFIG="${2:-}"; shift 2 ;;
    --plain-http) PLAIN_HTTP=1; shift ;;
    --oras) ORAS="${2:-}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "::error::unknown argument '$1'"; usage >&2; exit 2 ;;
  esac
done

fail() { echo "::error::$1"; exit 1; }

# The shape of what this script pushes for one publication (see --skip-if-published above).
# 2: modules ride with their bundle case-insensitively; a module-only package travels as a sidecar.
# Overridable ONLY by the behaviour harness, to stage a publication of an older layout.
PUBLICATION_LAYOUT="${MW_TEST_PUBLICATION_LAYOUT:-2}"
case "$PUBLICATION_LAYOUT" in ''|*[!0-9]*) fail "publication layout '$PUBLICATION_LAYOUT' is not a number" ;; esac

[ -n "$REGISTRY" ] || fail "--registry <host> is required"
[ -n "$SOURCE" ] || fail "--source <name> is required"
[ -n "$IDENTITY" ] || fail "--identity <id> is required"
command -v "$ORAS" >/dev/null 2>&1 || fail "oras is not on PATH (or --oras/\$ORAS does not name a binary) — install a pinned release: https://github.com/oras-project/oras/releases"

# ---- --published-commit: READ ONLY — what does <source>:<identity> name? ----------------------
# Asked by a lane run that was SUPERSEDED under the publication lock (MeshWeaver.Plugins#1785): it
# publishes nothing, so it must at least prove the registry names the live share publication
# (check-registry-names-live.sh) rather than turn green over a stale tag. Prints
# "<commit> <layout> <the layout this publisher writes>", or nothing when the tag is absent; exit 0
# either way. Any other failure to read is exit 1 — an unreadable registry is never "absent". Reads
# exactly what the sealed-skip below reads, in the same shapes.
if [ "$PUBLISHED_COMMIT_ONLY" -eq 1 ]; then
  SOURCE_LC_RO=$(printf '%s' "$SOURCE" | tr '[:upper:]' '[:lower:]')
  ROFLAGS=()
  [ "$PLAIN_HTTP" -eq 1 ] && ROFLAGS+=(--plain-http)
  [ -z "$REGISTRY_CONFIG" ] || ROFLAGS+=(--registry-config "$REGISTRY_CONFIG")
  RO=$(mktemp -d); trap 'rm -rf "$RO"' EXIT
  repo="$REGISTRY/plugins/$SOURCE_LC_RO"
  if ! digest=$("$ORAS" resolve "${ROFLAGS[@]}" "$repo:$IDENTITY" 2>"$RO/resolve.err"); then
    if grep -q ': not found$' "$RO/resolve.err"; then exit 0; fi
    cat "$RO/resolve.err" >&2
    fail "could not resolve $repo:$IDENTITY (see above) — whether the registry carries the publication cannot be established"
  fi
  "$ORAS" manifest fetch "${ROFLAGS[@]}" "$repo@$digest" > "$RO/index.json" \
    || fail "could not fetch the index $repo@$digest"
  sidecar=$(tr -d '\n' < "$RO/index.json" \
    | sed -n 's/.*"digest":"\(sha256:[0-9a-f]\{64\}\)","size":[0-9]\{1,\},"artifactType":"application\/vnd\.meshweaver\.publication\.v1+json","annotations":{"io\.meshweaver\.role":"sidecars"}.*/\1/p')
  [ -n "$sidecar" ] || fail "$repo:$IDENTITY names an index with no sidecar manifest — its source commit cannot be read"
  "$ORAS" manifest fetch-config "${ROFLAGS[@]}" "$repo@$sidecar" > "$RO/config.json" \
    || fail "could not fetch the sidecar config of $repo@$sidecar"
  ro_commit=$(tr -d '\n' < "$RO/config.json" | sed -n 's/.*"sourceCommit":"\([^"]*\)".*/\1/p')
  ro_layout=$(tr -d '\n' < "$RO/config.json" | sed -n 's/.*"layout":\([0-9]\{1,\}\).*/\1/p')
  # "<commit> <layout> <the layout this publisher writes>" — a same-commit tag of an OLDER layout is
  # not the current publication (the sealed-skip below republishes it), and a reader must see that.
  printf '%s %s %s\n' "${ro_commit:--}" "${ro_layout:--}" "$PUBLICATION_LAYOUT"
  exit 0
fi

[ -n "$DIR" ] || fail "--dir <publication dir> is required"
[ -d "$DIR" ] || fail "--dir '$DIR' is not a directory"

# ---- names: the registry's grammar is lowercase, and it is enforced here so a refusal is a
#      sentence rather than a MANIFEST_INVALID / NAME_INVALID from the registry --------------------
lower() { printf '%s' "$1" | tr '[:upper:]' '[:lower:]'; }
valid_component() { case "$1" in ''|*[!a-z0-9._-]*|[._-]*) return 1 ;; *) return 0 ;; esac; }
valid_tag() { case "$1" in ''|*[!A-Za-z0-9._-]*|[.-]*) return 1 ;; *) return 0 ;; esac; }

SOURCE_LC=$(lower "$SOURCE")
valid_component "$SOURCE_LC" || fail "source '$SOURCE' is not a valid repository name component ([a-z0-9._-], starting alphanumeric)"
[ "$SOURCE_LC" != "releases" ] || fail "source name 'releases' is reserved for plugins/releases (release identities)"
valid_tag "$IDENTITY" || fail "identity '$IDENTITY' is not a valid tag ([A-Za-z0-9._-], not starting with . or -)"
if [ -n "$TAG_RUN" ]; then
  valid_tag "$IDENTITY-$TAG_RUN" || fail "run id '$TAG_RUN' does not make a valid tag"
fi
if [ -n "$RELEASE" ]; then
  valid_tag "$RELEASE" || fail "release version '$RELEASE' is not a valid tag"
fi

# ---- the publication must be WHOLE before anything is pushed ------------------------------------
SENTINEL="_complete"
MODULES_DIR="modules"
MODULES_INDEX="modules/_index"
[ -f "$DIR/$SENTINEL" ] || fail "$DIR carries no $SENTINEL — an unsealed publication is never pushed"
BUNDLES=()
while IFS= read -r line || [ -n "$line" ]; do
  name="${line#"${line%%[![:space:]]*}"}"; name="${name%"${name##*[![:space:]]}"}"
  [ -n "$name" ] || continue
  case "$name" in
    *.zip) ;;
    *) fail "$SENTINEL lists '$name', which is not a bundle (.zip)" ;;
  esac
  case "$name" in
    */*|*'"'*|*'\'*) fail "$SENTINEL lists '$name', which is not a bare file name" ;;
  esac
  [ -f "$DIR/$name" ] || fail "$SENTINEL lists '$name' but $DIR/$name is absent — a torn publication is never pushed"
  BUNDLES+=("$name")
done < "$DIR/$SENTINEL"
[ "${#BUNDLES[@]}" -gt 0 ] || fail "$SENTINEL lists no bundles — an empty publication is never pushed"

MODULES=()
if [ -f "$DIR/$MODULES_INDEX" ]; then
  while IFS= read -r line || [ -n "$line" ]; do
    name="${line#"${line%%[![:space:]]*}"}"; name="${name%"${name##*[![:space:]]}"}"
    [ -n "$name" ] || continue
    case "$name" in
      */*|*'"'*|*'\'*) fail "$MODULES_INDEX lists '$name', which is not a bare file name" ;;
    esac
    [ -f "$DIR/$MODULES_DIR/$name" ] || fail "$MODULES_INDEX lists '$name' but $DIR/$MODULES_DIR/$name is absent — a torn module set is never pushed"
    MODULES+=("$name")
  done < "$DIR/$MODULES_INDEX"
fi

# ---- which module rides with which bundle — matched CASE-INSENSITIVELY ------------------------
# 🚨 The lanes write a module package under its LOWERCASED name (`modules/essentials.module.nupkg`)
# while the bundle keeps the package's own case (`Essentials.zip`), so an exact-case lookup of
# `$package.module.nupkg` attached NO module to ANY bundle: measured on MeshWeaver.Plugins run
# 38215076376 (`plugins/plugins:c003e001`), 43 bundles all `module=no` beside 8 listed modules.
# A module whose package bakes no bundle (a module-only package) has no manifest to ride in, and it
# used to be skipped as a "module" AND as a sidecar, so it never reached the registry at all while
# the `modules/_index` sidecar still listed it — a torn module set, the very shape the checks above
# refuse locally. It now travels in the sidecar manifest under its publication-relative title, so
# what `oras pull` writes back is the whole publication, byte for byte.
MODULE_OF_BUNDLE=()   # parallel to BUNDLES: the module file attached to that bundle, or empty
ATTACHED=()           # module file names attached to some bundle
for zip in "${BUNDLES[@]}"; do
  want=$(lower "${zip%.zip}.module.nupkg")
  match=""
  for m in ${MODULES[@]+"${MODULES[@]}"}; do
    [ "$(lower "$m")" = "$want" ] || continue
    [ -z "$match" ] || fail "$MODULES_INDEX lists both '$match' and '$m' for bundle '$zip' — two module packages differing only in case cannot both ride with it"
    match="$m"
  done
  if [ -n "$match" ]; then
    for a in ${ATTACHED[@]+"${ATTACHED[@]}"}; do
      [ "$a" != "$match" ] || fail "module '$match' matches more than one bundle (bundle names differing only in case) — refusing to guess which it rides with"
    done
    ATTACHED+=("$match")
  fi
  MODULE_OF_BUNDLE+=("$match")
done

# Every OTHER file is a sidecar and travels as a titled layer. Names must survive a JSON string
# and an OCI title unescaped, so the config blob can be composed without a JSON library.
SIDECARS=()
while IFS= read -r rel; do
  rel="${rel#./}"
  skip=0
  for b in "${BUNDLES[@]}"; do [ "$rel" = "$b" ] && skip=1; done
  for m in ${ATTACHED[@]+"${ATTACHED[@]}"}; do [ "$rel" = "$MODULES_DIR/$m" ] && skip=1; done
  [ "$skip" -eq 1 ] && continue
  case "$rel" in
    *[!A-Za-z0-9._/@+-]*) fail "sidecar '$rel' carries a character outside [A-Za-z0-9._/@+-]; the config blob cannot carry it" ;;
  esac
  SIDECARS+=("$rel")
done < <(cd "$DIR" && find . -type f | LC_ALL=C sort)
[ "${#SIDECARS[@]}" -gt 0 ] || fail "the publication carries no sidecar at all (not even $SENTINEL was found by find) — refusing"

# ---- ORAS flags shared by every call ----------------------------------------------------------
# 🚨 ORAS stamps `org.opencontainers.image.created` with the wall clock on every push unless the
# annotation is supplied, and that stamp is INSIDE the manifest bytes — so two pushes of the same
# publication would mint two digests and the design's "same bytes, same index" would be false.
# A publication is content-addressed; when it was pushed is what the `<identity>-<run>` tag and
# the registry's own log record. The epoch is the honest "no creation stamp" value.
CREATED="org.opencontainers.image.created=1970-01-01T00:00:00Z"
OFLAGS=()
[ "$PLAIN_HTTP" -eq 1 ] && OFLAGS+=(--plain-http)
if [ -n "$REGISTRY_CONFIG" ]; then
  [ -r "$REGISTRY_CONFIG" ] || fail "--registry-config '$REGISTRY_CONFIG' is not readable"
  OFLAGS+=(--registry-config "$REGISTRY_CONFIG")
fi
CPFLAGS=()
if [ "$PLAIN_HTTP" -eq 1 ]; then CPFLAGS+=(--from-plain-http --to-plain-http); fi
if [ -n "$REGISTRY_CONFIG" ]; then CPFLAGS+=(--from-registry-config "$REGISTRY_CONFIG" --to-registry-config "$REGISTRY_CONFIG"); fi

PUB_REPO="$REGISTRY/plugins/$SOURCE_LC"
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

digest_of_descriptor() {
  # `oras manifest fetch --descriptor` / `oras manifest push --descriptor` print one compact JSON
  # descriptor; take the digest and size off it without a JSON library.
  sed -n 's/.*"digest":"\(sha256:[0-9a-f]\{64\}\)".*/\1/p' | head -n 1
}
size_of_descriptor() {
  sed -n 's/.*"size":\([0-9]\{1,\}\).*/\1/p' | head -n 1
}
descriptor_of() {
  "$ORAS" manifest fetch "${OFLAGS[@]}" --descriptor "$1"
}

read_fact() {
  # First non-blank line of a sidecar, or empty when absent — the config blob's scalar facts.
  if [ -f "$DIR/$1" ]; then
    sed -n 's/^[[:space:]]*//;s/[[:space:]]*$//;/./{p;q;}' "$DIR/$1"
  fi
}
json_escape() {
  # The facts are tokens (a sha, a repository slug, an architecture, an identity) — refuse
  # anything a JSON string would need escaping for rather than escaping it wrong.
  case "$1" in
    *'"'*|*'\'*|*[[:cntrl:]]*) fail "value '$1' cannot be carried in the config blob" ;;
  esac
  printf '%s' "$1"
}

# ---- the release's identity (optional) — written on EVERY run, the skip below included ----------
# The share's `_releases/<version>` marker is written outside its sealed-skip for the same reason
# (publish-bake-bundles.sh): the version → identity mapping must land on the run that arms the
# release, and that run is usually one whose bundles are already published.
publish_release_identity() {
  [ -n "$RELEASE" ] || return 0
  # ExpectedDbVersion (#4764 (b3)) rides the same config when the caller knows it — an integer or
  # nothing, never a guess; absent means UNKNOWN to every reader.
  local schema="" expected="${EXPECTED_DB_VERSION:-}"
  # 🚨 A caller that does not KNOW the number (a bake-only reconcile) must not ERASE one an earlier
  # run published: this push REWRITES plugins/releases:<version>, so the existing config's
  # expectedDbVersion is carried over when the input is empty. Absent there too stays absent.
  if [ -z "$expected" ] \
      && "$ORAS" manifest fetch-config "${OFLAGS[@]}" "$REGISTRY/plugins/releases:$RELEASE" > "$WORK/existing-release.json" 2>/dev/null; then
    expected=$(tr -d '\n' < "$WORK/existing-release.json" | sed -n 's/.*"expectedDbVersion":\([0-9]\{1,\}\).*/\1/p')
    [ -z "$expected" ] || echo "bundle-publication: release=$RELEASE keeps the published expectedDbVersion=$expected (this run was given none)"
  fi
  if [ -n "$expected" ]; then
    case "$expected" in *[!0-9]*) fail "EXPECTED_DB_VERSION '$expected' is not an integer" ;; esac
    schema=",\"expectedDbVersion\":$expected"
  fi
  printf '{"identity":"%s","version":"%s"%s}' "$(json_escape "$IDENTITY")" "$(json_escape "$RELEASE")" "$schema" > "$WORK/release.json"
  local release_digest
  release_digest=$("$ORAS" push "${OFLAGS[@]}" "$REGISTRY/plugins/releases:$RELEASE" \
      --artifact-type application/vnd.meshweaver.release.v1+json \
      --config "$WORK/release.json:application/vnd.meshweaver.release.v1+json" \
      --annotation "io.meshweaver.identity=$IDENTITY" --annotation "$CREATED" \
      --format 'go-template={{.digest}}')
  case "$release_digest" in sha256:*) ;; *) fail "push of plugins/releases:$RELEASE answered '$release_digest', not a digest" ;; esac
  echo "bundle-publication: release=$RELEASE identity=$IDENTITY digest=$release_digest repository=plugins/releases"
}

record_outputs() { # <index digest> <bundle count>
  if [ -n "${GITHUB_OUTPUT:-}" ]; then
    {
      echo "index_digest=$1"
      echo "bundle_count=$2"
    } >> "$GITHUB_OUTPUT"
  fi
}

echo "── publishing $SOURCE ($SOURCE_LC) for identity $IDENTITY from $DIR to $PUB_REPO: ${#BUNDLES[@]} bundle(s), ${#MODULES[@]} module(s), ${#SIDECARS[@]} sidecar(s)"

# ---- 0. the registry's sealed-skip (--skip-if-published) ---------------------------------------
# The tag names ONE publication (the index digest IS the generation); its sidecar manifest is the
# index entry annotated `io.meshweaver.role=sidecars`, and that manifest's config blob carries
# `sourceCommit` — both written by this very script, in the shapes read below. Every read must
# succeed and agree for the skip to fire; a failed read is stated and falls through to the push.
if [ "$SKIP_IF_PUBLISHED" -eq 1 ]; then
  local_commit=$(read_fact source-commit.txt)
  case "$local_commit" in
    ''|unknown) fail "--skip-if-published needs a recorded source commit in $DIR/source-commit.txt (found '${local_commit:-<none>}') — without a content identity the skip could freeze the tag on a stale publication" ;;
  esac
  existing=""
  : > "$WORK/fetch.err"
  if existing=$("$ORAS" resolve "${OFLAGS[@]}" "$PUB_REPO:$IDENTITY" 2>"$WORK/resolve.err"); then
    case "$existing" in sha256:*) ;; *) fail "resolve of $PUB_REPO:$IDENTITY answered '$existing', not a digest" ;; esac
    published_commit=""
    published_layout=""
    if "$ORAS" manifest fetch "${OFLAGS[@]}" "$PUB_REPO@$existing" > "$WORK/existing-index.json" 2>"$WORK/fetch.err"; then
      existing_sidecar=$(tr -d '\n' < "$WORK/existing-index.json" \
        | sed -n 's/.*"digest":"\(sha256:[0-9a-f]\{64\}\)","size":[0-9]\{1,\},"artifactType":"application\/vnd\.meshweaver\.publication\.v1+json","annotations":{"io\.meshweaver\.role":"sidecars"}.*/\1/p')
      if [ -n "$existing_sidecar" ] \
          && "$ORAS" manifest fetch-config "${OFLAGS[@]}" "$PUB_REPO@$existing_sidecar" > "$WORK/existing-config.json" 2>"$WORK/fetch.err"; then
        published_commit=$(tr -d '\n' < "$WORK/existing-config.json" | sed -n 's/.*"sourceCommit":"\([^"]*\)".*/\1/p')
        published_layout=$(tr -d '\n' < "$WORK/existing-config.json" | sed -n 's/.*"layout":\([0-9]\{1,\}\).*/\1/p')
      fi
    fi
    if [ -n "$published_commit" ] && [ "$published_commit" = "$local_commit" ] \
        && [ "$published_layout" = "$PUBLICATION_LAYOUT" ]; then
      echo "::notice::$PUB_REPO:$IDENTITY already names a complete publication of THIS content ($existing, source $published_commit) — already published; skipping the push. The registry, like the shares, seals one publication per content × framework identity."
      publish_release_identity
      echo "bundle-publication: index=$existing bundles=${#BUNDLES[@]} tag=$IDENTITY registry=$REGISTRY source=$SOURCE repository=plugins/$SOURCE_LC skipped=already-published"
      record_outputs "$existing" "${#BUNDLES[@]}"
      exit 0
    fi
    if [ -n "$published_commit" ] && [ "$published_commit" = "$local_commit" ]; then
      echo "$PUB_REPO:$IDENTITY names a publication of THIS content but of layout '${published_layout:-none}', not $PUBLICATION_LAYOUT — republishing it in the current layout (the tag moves last; the previous generation stays resident by digest)"
    elif [ -z "$published_commit" ]; then
      echo "$PUB_REPO:$IDENTITY resolves to $existing but its source commit could not be read ($(tr '\n' ' ' < "$WORK/fetch.err")) — publishing, since a push is idempotent and a skip without proof is not"
    else
      # 🚨 NEVER MOVE THE TAG BACKWARDS (Copilot's review of #6878). The share publisher refuses to
      # seal an OLDER commit over a NEWER one (publish-bake-bundles.sh, `superseded`) — but it still
      # materialises this run's publication for the registry, so without the same question here the
      # shares stayed newer while the registry tag rolled back. Asked of the compare API exactly as
      # the share side asks it (GH_TOKEN + BAKE_CONTENT_REPOSITORY); `ahead` = the published commit
      # is newer and contains this one. The release identity is still recorded — it describes the
      # RELEASE, not which content the tag names. An unorderable pair keeps today's behaviour
      # (republish) and says why.
      order=""
      if [ -n "${GH_TOKEN:-}" ] && [ -n "${BAKE_CONTENT_REPOSITORY:-}" ]; then
        if ! order=$(gh api "repos/$BAKE_CONTENT_REPOSITORY/compare/$local_commit...$published_commit" --jq .status 2>"$WORK/compare.err"); then
          echo "::warning::the compare API refused to order $local_commit against $published_commit: $(tail -c 400 "$WORK/compare.err" | tr '\n' ' ')"
          order=""
        fi
      fi
      if [ "$order" = "ahead" ]; then
        echo "::notice title=Not moving the registry tag backwards::$PUB_REPO:$IDENTITY names a publication from $published_commit, which is NEWER than this one's $local_commit and contains it — leaving the tag where it is."
        publish_release_identity
        echo "bundle-publication: index=$existing bundles=${#BUNDLES[@]} tag=$IDENTITY registry=$REGISTRY source=$SOURCE repository=plugins/$SOURCE_LC skipped=superseded"
        record_outputs "$existing" "${#BUNDLES[@]}"
        exit 0
      fi
      [ -n "$order" ] || echo "::warning::could not order the published $published_commit against this publication's $local_commit (no GH_TOKEN / BAKE_CONTENT_REPOSITORY, or the compare API refused) — republishing as before."
      echo "$PUB_REPO:$IDENTITY names a publication from source '$published_commit' but this one is from '$local_commit' — republishing (the tag moves last; the previous generation stays resident by digest)"
    fi
  else
    if grep -q ': not found$' "$WORK/resolve.err"; then
      echo "$PUB_REPO:$IDENTITY is not published yet (tag absent) — publishing"
    else
      cat "$WORK/resolve.err" >&2
      echo "$PUB_REPO:$IDENTITY could not be resolved (see above) — publishing anyway; if the registry is refusing this credential the push below fails with the real reason"
    fi
  fi
fi

# ---- 1. the bundles ----------------------------------------------------------------------------
BUNDLE_FACTS=()
INDEX_ENTRIES=()
for i in "${!BUNDLES[@]}"; do
  zip="${BUNDLES[$i]}"
  package="${zip%.zip}"
  package_lc=$(lower "$package")
  valid_component "$package_lc" || fail "package '$package' is not a valid repository name component"
  repo="$REGISTRY/plugins/$SOURCE_LC/$package_lc"
  files=("$zip:application/vnd.meshweaver.bundle.v1.zip")
  has_module=no
  module="${MODULE_OF_BUNDLE[$i]}"
  if [ -n "$module" ]; then
    files+=("$MODULES_DIR/$module:application/vnd.meshweaver.module.v1.nupkg")
    has_module=yes
  fi
  digest=$(cd "$DIR" && "$ORAS" push "${OFLAGS[@]}" "$repo:$IDENTITY" \
      --artifact-type application/vnd.meshweaver.bundle.v1+json \
      --annotation "io.meshweaver.package=$package" \
      --annotation "io.meshweaver.source=$SOURCE" \
      --annotation "io.meshweaver.identity=$IDENTITY" --annotation "$CREATED" \
      --format 'go-template={{.digest}}' "${files[@]}")
  case "$digest" in sha256:*) ;; *) fail "push of $repo:$IDENTITY answered '$digest', not a digest" ;; esac
  if [ -n "$TAG_RUN" ]; then
    "$ORAS" tag "${OFLAGS[@]}" "$repo@$digest" "$IDENTITY-$TAG_RUN" >/dev/null
  fi
  # The index may only name manifests of its own repository: copy the bundle manifest (same
  # bytes, same digest; blobs are mounted or re-uploaded, never duplicated on disk) into it.
  "$ORAS" cp "${CPFLAGS[@]}" "$repo@$digest" "$PUB_REPO" >/dev/null
  size=$(descriptor_of "$PUB_REPO@$digest" | size_of_descriptor)
  [ -n "$size" ] || fail "could not read the size of $PUB_REPO@$digest after the copy"
  INDEX_ENTRIES+=("{\"mediaType\":\"application/vnd.oci.image.manifest.v1+json\",\"digest\":\"$digest\",\"size\":$size,\"artifactType\":\"application/vnd.meshweaver.bundle.v1+json\",\"annotations\":{\"io.meshweaver.package\":\"$(json_escape "$package")\"}}")
  BUNDLE_FACTS+=("{\"package\":\"$(json_escape "$package")\",\"digest\":\"$digest\",\"module\":$([ "$has_module" = yes ] && echo true || echo false)}")
  echo "bundle-publication: bundle=$package digest=$digest module=$has_module repository=plugins/$SOURCE_LC/$package_lc"
done

# ---- 2. the sidecars ---------------------------------------------------------------------------
SIDECAR_ARGS=()
SIDECAR_NAMES=""
for rel in "${SIDECARS[@]}"; do
  case "$rel" in
    *.json) mt="application/json" ;;
    *.txt|*/_index|_complete|*/_current|_current) mt="text/plain" ;;
    "$MODULES_DIR"/*.module.nupkg) mt="application/vnd.meshweaver.module.v1.nupkg" ;;   # a module with no bundle to ride in
    *) mt="application/octet-stream" ;;
  esac
  SIDECAR_ARGS+=("$rel:$mt")
  SIDECAR_NAMES="$SIDECAR_NAMES\"$rel\","
done
{
  printf '{"schemaVersion":1,"identity":"%s","source":"%s"' "$(json_escape "$IDENTITY")" "$(json_escape "$SOURCE")"
  printf ',"sourceCommit":"%s"' "$(json_escape "$(read_fact source-commit.txt)")"
  printf ',"layout":%s' "$PUBLICATION_LAYOUT"
  printf ',"repository":"%s"' "$(json_escape "$(read_fact repository.txt)")"
  printf ',"architecture":"%s"' "$(json_escape "$(read_fact architecture.txt)")"
  printf ',"release":"%s"' "$(json_escape "$RELEASE")"
  printf ',"bundles":['; (IFS=,; printf '%s' "${BUNDLE_FACTS[*]}"); printf ']'
  printf ',"files":[%s]}' "${SIDECAR_NAMES%,}"
} > "$WORK/config.json"
sidecar_digest=$(cd "$DIR" && "$ORAS" push "${OFLAGS[@]}" "$PUB_REPO" \
    --artifact-type application/vnd.meshweaver.publication.v1+json \
    --config "$WORK/config.json:application/vnd.meshweaver.publication.v1+json" \
    --annotation "io.meshweaver.source=$SOURCE" \
    --annotation "io.meshweaver.identity=$IDENTITY" --annotation "$CREATED" \
    --format 'go-template={{.digest}}' "${SIDECAR_ARGS[@]}")
case "$sidecar_digest" in sha256:*) ;; *) fail "push of the sidecar manifest answered '$sidecar_digest', not a digest" ;; esac
sidecar_size=$(descriptor_of "$PUB_REPO@$sidecar_digest" | size_of_descriptor)
[ -n "$sidecar_size" ] || fail "could not read the size of the sidecar manifest $sidecar_digest"
echo "bundle-publication: sidecars=${#SIDECARS[@]} digest=$sidecar_digest"

# ---- 3. the index, untagged --------------------------------------------------------------------
{
  printf '{"schemaVersion":2,"mediaType":"application/vnd.oci.image.index.v1+json","artifactType":"application/vnd.meshweaver.publication.v1+json","manifests":['
  printf '{"mediaType":"application/vnd.oci.image.manifest.v1+json","digest":"%s","size":%s,"artifactType":"application/vnd.meshweaver.publication.v1+json","annotations":{"io.meshweaver.role":"sidecars"}}' "$sidecar_digest" "$sidecar_size"
  for entry in "${INDEX_ENTRIES[@]}"; do printf ',%s' "$entry"; done
  printf '],"annotations":{"io.meshweaver.identity":"%s","io.meshweaver.source":"%s"}}' "$(json_escape "$IDENTITY")" "$(json_escape "$SOURCE")"
} > "$WORK/index.json"
index_digest=$("$ORAS" manifest push "${OFLAGS[@]}" --descriptor "$PUB_REPO" "$WORK/index.json" | digest_of_descriptor)
case "$index_digest" in sha256:*) ;; *) fail "push of the index answered no digest" ;; esac

# ---- 4. the tags, last -------------------------------------------------------------------------
if [ -n "$TAG_RUN" ]; then
  "$ORAS" tag "${OFLAGS[@]}" "$PUB_REPO@$index_digest" "$IDENTITY-$TAG_RUN" >/dev/null
fi
"$ORAS" tag "${OFLAGS[@]}" "$PUB_REPO@$index_digest" "$IDENTITY" >/dev/null
resolved=$("$ORAS" resolve "${OFLAGS[@]}" "$PUB_REPO:$IDENTITY")
[ "$resolved" = "$index_digest" ] || fail "after tagging, $PUB_REPO:$IDENTITY resolves to $resolved, not to the index $index_digest just pushed"

# ---- the release's identity (optional) --------------------------------------------------------
publish_release_identity

echo "bundle-publication: index=$index_digest bundles=${#BUNDLES[@]} tag=$IDENTITY${TAG_RUN:+ run-tag=$IDENTITY-$TAG_RUN} registry=$REGISTRY source=$SOURCE repository=plugins/$SOURCE_LC"
echo "::notice::published plugins/$SOURCE_LC:$IDENTITY = $index_digest (${#BUNDLES[@]} bundle(s), ${#MODULES[@]} module(s), ${#SIDECARS[@]} sidecar(s)) to $REGISTRY"
record_outputs "$index_digest" "${#BUNDLES[@]}"
