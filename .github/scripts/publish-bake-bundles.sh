#!/usr/bin/env bash
# publish-bake-bundles.sh <bake-dir> <source-name> [<source-sha>] [<release-version>]
#
# Publishes a CI NodeType bake (the directory mw-plugin-test's --bake-output wrote: one
# <package>.zip per package + framework-mvid.txt + platform-surface.json) to the shared storage the portals read at boot
# (#1660 WS3). The portal side is ShippedPrebuiltBundles.SeedPublishedRoot: each pod seeds
# <PreWarm:PrebuiltBundleRoot>/<its-own-framework-identity>/**/*.zip, so the key layout here is
#
#     <base>/prebuilt-bundles/<framework-identity>/<source-name>/<bundle>.zip
#
# where <framework-identity> comes from the bake's framework-mvid.txt. 🚨 This used to say "for CI
# builds: the commit identity g<sha>", and that went stale when the surface identity landed: the
# bake host IS mw-plugin-test, which opts into the surface manifest
# (MeshWeaver.PluginTester.csproj) and therefore resolves the SURFACE identity s<hash>. g<sha> is
# now only the fallback for manifest-LESS processes (FrameworkBuildIdentity). The distinction is
# not cosmetic — s<hash> is architecture-SENSITIVE (four reference assemblies differ between the
# amd64 and arm64 variants of one image) while g<sha> is not, which is exactly what decides
# whether two architectures can publish side by side or collide. See the guard below.
# <source-name> is the
# producing repo's segment (e.g. meshweaver-content, plugins, education) so multiple independent
# producers publish without clobbering; the bundle manifests inside carry the exact source SHA.
#
# <source-sha> is the CONTENT identity — the producing repo's commit the bake was taken from
# (what the caller passed to mw-plugin-test --source-sha). The publication key is content ×
# framework: a sealed directory is skipped only when BOTH match (see the sealed-skip below).
# Omitting it degrades the skip to framework-identity-only — correct for a producer whose content
# lives in the framework repo itself, but a NODE repo must pass it: its content changes while the
# framework identity stays put, and a framework-only skip would freeze its first publication for
# the whole framework release.
#
# ENVIRONMENT
#   BAKE_PUBLISH_TARGETS   whitespace-separated targets, each  <storage-account>/<file-share>
#                          (optionally <storage-account>/<file-share>/<base-path>). These are the
#                          Azure Files shares the portals mount (on AKS: the share behind the
#                          memex-data PVC, mounted at /data — so the portal reads
#                          /data/prebuilt-bundles/... when PreWarm__PrebuiltBundleRoot is
#                          /data/prebuilt-bundles).
#   BAKE_CONTENT_REPOSITORY  owner/name of the repository the BAKED CONTENT came from — recorded in
#                          repository.txt so an instance can attribute the seal (see the marker
#                          below). Defaults to $GITHUB_REPOSITORY, which is the LANE's repository
#                          and therefore right only when the lane bakes its own content.
#
# AUTH: `az login` must already have happened (the CD jobs use OIDC). Data-plane access uses
# --auth-mode login with --backup-intent, which requires the identity to hold the
# "Storage File Data Privileged Contributor" role on the target storage accounts.
#
# <release-version> is the PLATFORM VERSION this publication belongs to (main-cd passes the
# promoted `memex-portal-ai:<version>`; node repos pass nothing — they do not define a platform
# release). When given, the script also records the version → framework-identity mapping at
#
#     <base>/prebuilt-bundles/_releases/<release-version>
#
# 🚨 That marker is the ONLY way anything outside the image can learn a release's framework
# identity: the identity is a property of the BINARIES (#1725), resolved by the image itself, so it
# cannot be computed from a tag or a commit. The release gates (#1754 deployment, #1755 build) read
# it to answer "is every deployed package available for the release we are about to roll to"; a
# missing marker therefore means one precise thing — that release published no platform content
# bake — and both gates HOLD on it rather than guessing (fail safe).
#
# 🚨 The marker is written on EVERY run, deliberately OUTSIDE the already-sealed skip below. The
# skip keys on content × framework, and the API-surface identity is breaking-change-keyed, so an
# ordinary release re-resolves the SAME identity and skips the upload — if the marker rode along
# with the bundles, the second and every later release of a surface generation would have no marker
# at all and every environment would be held forever on a release that is in fact perfectly fine.
#
# Loud by design: a missing/empty target list, identity file, or bundle set FAILS — a publish
# step that silently ships nothing is exactly the regression (#1347 → #1660) this lane fixes.
set -euo pipefail

USAGE="usage: publish-bake-bundles.sh <bake-dir> <source-name> [<source-sha>] [<release-version>]"
BAKE_DIR="${1:?$USAGE}"
SOURCE="${2:?$USAGE}"
SOURCE_SHA="${3:-}"
RELEASE_VERSION="${4:-}"

# Whitespace-only counts as unset: `for target in $BAKE_PUBLISH_TARGETS` would iterate zero
# times and the script would report success having published nowhere — the silent-nothing
# outcome this script exists to make impossible.
if [ -z "${BAKE_PUBLISH_TARGETS:-}" ] || ! grep -q '[^[:space:]]' <<<"${BAKE_PUBLISH_TARGETS:-}"; then
  echo "::error::BAKE_PUBLISH_TARGETS is not set (or holds no targets) — provision the repo variable with the portals' storage targets (<account>/<share>[/<base-path>], whitespace-separated). The CI bake cannot reach any portal without it."
  exit 1
fi

IDENTITY_FILE="$BAKE_DIR/framework-mvid.txt"
if [ ! -s "$IDENTITY_FILE" ]; then
  echo "::error::$IDENTITY_FILE is missing or empty — the bake carries no framework identity, so nothing can adopt it."
  exit 1
fi
IDENTITY="$(tr -d '[:space:]' < "$IDENTITY_FILE")"

# 🚨 ARCHITECTURE IS PART OF THE COMPATIBILITY CLAIM, and the identity does not always carry it.
# FrameworkBuildIdentity resolves surface identity (s<hash>) -> stamped commit identity (g<sha>)
# -> MVID set. The FIRST is architecture-sensitive: four reference assemblies genuinely differ
# between the amd64 and arm64 variants of one multi-arch image, so the two resolve different
# s<hash> values and cannot collide. The SECOND is NOT: g<sha> is the same string for every CI
# build of a commit, whatever it was built on.
#
# So the moment a second architecture publishes, a g<sha> lane has two producers writing ONE
# directory, and both outcomes are silent and wrong: same source commit => the sealed-skip below
# fires and the second architecture NEVER publishes while its pods adopt the first one's bytes;
# different source => it unseals and OVERWRITES. Adopting bytes from an identity you did not
# resolve is the exact shape CiContentBake.md forbids ("never publish a bake under several
# identities, never let a pod scan for a nearest one") and it surfaces as a TypeLoadException
# inside a collectible ALC at activation — no overlay, no compile error, nothing to grep.
#
# Recording it makes the collision DETECTABLE, and the guard below makes it LOUD.
BAKE_ARCHITECTURE="${BAKE_ARCHITECTURE:-linux-x64}"
case "$BAKE_ARCHITECTURE" in
  linux-x64|linux-arm64) ;;
  *)
    echo "::error::BAKE_ARCHITECTURE='$BAKE_ARCHITECTURE' is not a known architecture (linux-x64|linux-arm64). It keys a compatibility claim, so an unrecognised value must never be published."
    exit 1 ;;
esac
# -s passes a whitespace-only file; an empty identity would silently publish under
# 'prebuilt-bundles//<source>' — a directory no pod's identity ever resolves to.
if [ -z "$IDENTITY" ]; then
  echo "::error::$IDENTITY_FILE holds only whitespace — the bake carries no framework identity, so nothing can adopt it."
  exit 1
fi

shopt -s nullglob
BUNDLES=("$BAKE_DIR"/*.zip)
if [ "${#BUNDLES[@]}" -eq 0 ]; then
  echo "::error::no bundle zips under $BAKE_DIR — a bake that produced nothing must not reach the publish step."
  exit 1
fi

# The completeness sentinel (must match ShippedPrebuiltBundles.CompletionSentinelFileName): its
# PRESENCE means "every bundle of this publication landed", because it is uploaded strictly LAST.
# Its content lists the bundle set, so the reader can detect a listed-but-missing bundle too.
#
# 🚨 The local copy lives in a temp DIRECTORY under its REAL name, and the upload below targets
# the destination DIRECTORY (not "$dest/$SENTINEL"): `az storage file upload` silently treats an
# EXTENSIONLESS --path as a directory and appends the source basename — so uploading a mktemp
# file to "$dest/_complete" actually attempts "$dest/_complete/tmp.XXXX" and fails
# `ParentNotFound` every time, while every ".zip" beside it lands fine. Verified live against the
# portals' Azure Files share 2026-08-17; with the naive shape the seal step can never succeed and
# every publication stays torn (unreadable to portals) forever.
SENTINEL="_complete"
SENTINEL_LOCAL_DIR=$(mktemp -d)
trap 'rm -rf "$SENTINEL_LOCAL_DIR"' EXIT
SENTINEL_LOCAL="$SENTINEL_LOCAL_DIR/$SENTINEL"
for zip in "${BUNDLES[@]}"; do basename "$zip"; done | sort > "$SENTINEL_LOCAL"
# 🚨 THE MODULE SET (MeshWeaver#2698). A publication is sealed against the exact module bytes its
# bake composed; a consumer pinned to this identity must compose THOSE, not the registry's
# package endpoint (which serves the module's own lane's last build under an unmoved version). So
# the composed bundles travel with the publication under modules/, listed in modules/_index —
# written before the top-level sentinel, so "sealed" implies "module set present". A bake dir
# with no modules/ composed nothing and seals an EMPTY index: a reader can then tell "nothing
# composed" from "predates module sealing" (no index at all), and the latter is republished.
MODULES_DIR_NAME="modules"
MODULES_INDEX="_index"
MODULES=("$BAKE_DIR/$MODULES_DIR_NAME"/*.module.nupkg)
# 🚨 A workflow that COMPOSED modules must have STAGED them. EXT_MODULES_DIR is exported by the
# reusable bake's assemble step (before and after #2707 alike); an empty staging dir beside it
# means the CALLING WORKFLOW predates module sealing while this script does not — a version skew
# (the workflow pinned by `uses:`, the script fetched at platform-ref). Plugins run 33284306805
# (2026-08-30) sealed exactly that: an EMPTY set that the new contract reads as "composed nothing",
# so every consumer was told the seal carries no AI. RED, naming the pin to bump — never a seal
# that claims completeness for bytes it did not carry.
if [ -n "${EXT_MODULES_DIR:-}" ] && [ "${#MODULES[@]}" -eq 0 ]; then
  echo "::error::this bake composed external modules ($(ls "$EXT_MODULES_DIR" 2>/dev/null | tr '\n' ' ')) but staged none under $BAKE_DIR/$MODULES_DIR_NAME/ — the calling workflow predates module sealing (MeshWeaver#2707) while this script does not. Bump the caller's node-repo-publish-bake.yml pin to a core commit at or after 4584ca3c5. Refusing to seal an empty module set that would claim completeness."
  exit 1
fi
MODULES_INDEX_LOCAL="$SENTINEL_LOCAL_DIR/$MODULES_INDEX"
: > "$MODULES_INDEX_LOCAL"
for m in ${MODULES[@]+"${MODULES[@]}"}; do basename "$m"; done | sort > "$MODULES_INDEX_LOCAL"

# The CONTENT identity marker: which source commit this publication was baked from. It is NOT
# part of the reader's contract (SeedPublishedRoot seeds only what the sentinel lists; extra
# files are ignored) — it exists solely so the sealed-skip below can compare content, not just
# framework identity. Written BEFORE the sentinel, so a sealed directory always carries a
# consistent marker.
SOURCE_MARKER="source-commit.txt"
SOURCE_MARKER_LOCAL="$SENTINEL_LOCAL_DIR/$SOURCE_MARKER"
printf '%s\n' "${SOURCE_SHA:-unknown}" > "$SOURCE_MARKER_LOCAL"

# The ARCHITECTURE marker — same contract as the content marker: not part of the reader's
# contract (SeedPublishedRoot seeds only what the sentinel lists), written BEFORE the sentinel so
# a sealed directory always carries one, and read by the cross-architecture guard below.
# The PRODUCING REPOSITORY marker (MeshWeaver.Plugins#1430) — same contract as the content marker:
# not part of the reader's contract, written BEFORE the sentinel. It is what lets an instance
# attribute a sealed source to the repository whose green builds it receives, so its sync sources
# advance only to the commit sealed for its own identity (SealedPublicationIndex / SealedSyncGate
# in core). A local run records nothing rather than a guess; the gate attributes such a seal by
# commit instead. Listed and uploaded BEFORE architecture.txt, which stays the LAST upload before
# the postcondition — the overlap harness hooks its second publisher onto that file.
#
# 🚨 It is the CONTENT repository, NEVER $GITHUB_REPOSITORY (MeshWeaver#3583). The two differ in
# exactly the case the marker exists for: core CD's `plugins-bake` bakes MeshWeaver.Plugins content
# from a run whose $GITHUB_REPOSITORY is Systemorph/MeshWeaver, so the lane's own name stamped a
# `plugins` seal as the PLATFORM's. SealedSyncGate.BelongsTo takes the marker branch whenever it is
# non-empty and never falls back to commit attribution, so a Plugins green build then found no seal
# attributable to Plugins, `mine` was empty, and the gate returned Go — inert for the one repository
# it was written for, while the satellite's own publish-bake stamped the SAME prefix correctly. The
# same trap one field over from the content sha, which node-repo-publish-bake.yml already warns
# about ("The CONTENT commit, not $GITHUB_SHA. They differ exactly when content-repository is set").
#
# The fallback to $GITHUB_REPOSITORY is correct ONLY for a lane baking its OWN repository's content
# — which is every node repo and core's `meshweaver-content` bake. Both live callers now pass the
# value explicitly, so nothing in the fleet relies on the fallback; it is here for an older
# workflow copy that has not been re-pinned yet, where the lane's own name is the best available
# answer and was the whole answer before this variable existed.
REPO_MARKER="repository.txt"
REPO_MARKER_LOCAL="$SENTINEL_LOCAL_DIR/$REPO_MARKER"
printf '%s\n' "${BAKE_CONTENT_REPOSITORY:-${GITHUB_REPOSITORY:-}}" > "$REPO_MARKER_LOCAL"

ARCH_MARKER="architecture.txt"
ARCH_MARKER_LOCAL="$SENTINEL_LOCAL_DIR/$ARCH_MARKER"
printf '%s\n' "$BAKE_ARCHITECTURE" > "$ARCH_MARKER_LOCAL"

# 🚨 THE PLATFORM SURFACE (MeshWeaver#3651) — `platform-surface.json`, written by the bake beside
# framework-mvid.txt (BakeOutput.PlatformSurfaceFile; the name is ModulePlatformSurface.PublishedFileName
# and PublishedBundleCatalogue.PlatformSurfaceFileName reads it back): the assemblies the platform
# carries and the full type names each exports. The bake runs INSIDE the platform image, so it is
# the one process that can say what that platform has; the release gate reads this document to
# link an instance's LANDED module generation against the target — "would this module load there"
# — which is the ONLY thing that holds a platform roll since #3651. A missing content bake no
# longer holds (the instance compiles at boot, as every PR of that content already proved green),
# and a declared floor never did anything a string could get right.
#
# Published beside _complete like the other markers — not part of the seeder's contract (it seeds
# what the sentinel lists), written BEFORE the sentinel so a sealed directory always carries a
# consistent one, and verified by the postcondition below like every other file.
#
# 🚨 OPTIONAL, and loudly so. A bake taken with an image that predates #3651 writes none, and the
# gate's answer for such a publication is "Indeterminate for the link check" — REPORTED on the
# verdict, neither clearance nor a hold (the boot-time probe and the keep-the-previous-generation
# fallback are the safety net). So its absence is a ::warning:: naming what the gate will not be
# able to measure, never a refusal to publish: refusing here would hold every satellite on an older
# tester pin from publishing at all, which is a worse outage than an unmeasured link. It is NOT a
# skip-trapdoor — nothing is skipped, and the consumer names the absence on every verdict.
SURFACE_FILE="platform-surface.json"
SURFACE_LOCAL="$BAKE_DIR/$SURFACE_FILE"
if [ -s "$SURFACE_LOCAL" ]; then
  HAS_SURFACE=true
else
  HAS_SURFACE=false
  echo "::warning::$BAKE_DIR carries no $SURFACE_FILE — the bake was taken with a platform image that predates MeshWeaver#3651, so the release gate cannot link a landed module against this identity and reports every such module as 'could not be determined' (reported, not a hold). Move the bake image forward to publish the surface."
fi

# ══════════════════════ THE TWO-WRITER POSTCONDITION (MeshWeaver#3461) ══════════════════════
#
# 🚨 `<identity>/plugins` HAS SEVERAL WRITERS. Core CD's `plugins-bake` job publishes `bake-source:
# plugins` at `gate.outputs.plugins_sha`, and the MeshWeaver.Plugins satellite's own `publish-bake`
# publishes the same source at its own head. Both call this script; both write one prefix. They
# resolve the SAME framework identity whenever the core surface has not changed between core's tip
# and the satellite's `MW_PLATFORM_REF` pin — which is the ordinary case, because the identity is
# breaking-change-keyed by construction.
#
# Measured on 2026-09-06 (25 core-CD publish jobs, 19 satellite ones): core CD wrote 20 distinct
# identities, the satellite 2, and BOTH of the satellite's were also written by core CD — each time
# the satellite unsealed core's publication and republished it from a different source sha
# (`sf09fa2c9…`: 3e81b03960 → 9717e13e49; `sa55f8190…`: bbb893d673 → 88f1d44a19). Closest
# cross-lane approach 24 minutes; no strict cross-lane time overlap in that window.
#
# 🚨 AND THE SAME LANE OVERLAPS WITH ITSELF, more often than the two lanes overlap with each other.
# The same measurement found FOUR same-identity window overlaps inside the satellite's own CI on
# that one day — three concurrent bakes on `sa55f8190…` at 08:03Z, two of which sealed 24 minutes
# apart — against zero cross-lane ones. That is why "give the prefix a single owner" does not
# close this: the single owner races itself. The postcondition below is publisher-agnostic on
# purpose; it asks "are these MY bytes", never "which repo is the other one".
#
# Everything below `publish_one_target` is single-writer-safe and nothing more. Interleave two runs
# on one prefix and the sealed-skip's answer is stale for whichever loses the race: both unseal,
# both upload, and the last to write `_complete` seals a sentinel over a directory holding SOME OF
# EACH one's bytes. That is one seal with one generation — self-consistent to every consumer, and
# wrong. #3460's read-side generation cannot see it (there is nothing stale to refuse), the boot
# seeder cannot see it (the sentinel is present and every listed bundle exists), and the first
# symptom is `dependency record mismatch — built against mvid:…, live is mvid:…` on a portal that
# renders nothing.
#
# 🚨 A CLAIM TOKEN CANNOT FIX THIS, and it is the obvious thing to reach for. "Stamp a per-run
# marker before unsealing, re-read it before sealing" is check-then-act on ONE MUTABLE CELL, and
# the cell has the wrong asymmetry: if A stamps after B, A re-reads its OWN stamp and seals
# happily over B's bytes. The loser detects the winner; the WINNER detects nothing. There is no
# arrangement of a single marker that fixes that, because the marker records who wrote LAST, not
# whose bytes are on the shelf.
#
# So the postcondition is asserted on THE BYTES, which is what "a mix" actually means. Every file
# this run uploads carries two metadata values:
#
#     digest       the SHA-256 of the exact local bytes uploaded
#     publication  a token unique to this run (and the run that wrote it, by name)
#
# and `verify_publication` re-reads all of them immediately before the seal. A foreign publisher
# that overwrote ANY file leaves ITS digest there, so:
#
#   * every file's digest matches ⇒ the sealed set is byte-for-byte what this run uploaded. Not a
#     mix, by definition — there is nothing else it could be.
#   * any file's digest differs   ⇒ REFUSE TO SEAL. The directory stays sentinel-less, which is the
#     state every reader already skips, and `publication` NAMES the run that overwrote it.
#
# This is symmetric, which the claim token is not: in a genuine overlap BOTH runs find foreign
# bytes and BOTH refuse, so no mix is ever sealed. It needs no coordination between the lanes, no
# lease, no lock, and no staleness heuristic that could deadlock the prefix behind a cancelled run.
#
# 🚨 What it does NOT close, stated so the next session does not re-derive it: the interval between
# the last verification read and the `_complete` upload. A publisher that overwrites a file inside
# that one-upload window still lands under this run's seal. The window shrinks from the whole
# ~90-second publication to a single file upload, and it is why #3461 stays open for the layout
# question (a generation directory plus an atomic pointer swap, which removes republication in
# place entirely). The refusal below is a postcondition, not mutual exclusion.
#
# 🚨 THAT LAYOUT IS DESIGNED AND ITS READER HALF IS LANDED — do not re-derive it either:
# Doc/Architecture/SealedPublicationGenerations. Each publication goes into its OWN directory named
# by $PUBLICATION (already unique per run, already a legal bare name), and a one-line `_current`
# pointer is moved LAST; disjoint directories mean two publishers cannot interleave at all, so a mix
# stops being detectable and becomes unrepresentable. Every READER now resolves that pointer and
# falls back to this flat layout when there is none (ShippedPrebuiltBundles.PublicationDirectoryOf),
# so the reader side is already deployed and inert.
#
# 🚨 WHAT MUST NOT HAPPEN BEFORE THE WRITER MOVES, and it is an ORDERING rule, not a code one: a new
# writer and an old writer on one prefix is the half-migration to avoid. The new one moves the
# pointer; the old one replaces the flat copy in place and never touches it — so a pointer-following
# reader keeps serving its generation and never sees the old writer's NEWER publication. A stale
# serve, silent, with nothing red anywhere. (bake-scope.sh and carry-forward-bundles.sh are fetched
# at the SAME platform-ref as this file, so those three move together and no pin can carry half of
# it.)
#
# 🚨 "PAST THE READER PHASE" IS NOT THE PRECONDITION — measured 2026-09-07, and this comment used to
# say it was. The reader phase (a4109d422) changed `src/` and documentation only; a producer whose
# `platform-ref` moves past it runs THIS FILE byte-identically, so that condition can be satisfied
# fleet-wide without moving the migration one step. What a producer must be past is the WRITER
# commit — which is unreachable for a writer that is on by default, because it takes effect the
# moment a pin reaches it, and core CD's `plugins-bake` pins nothing at all (it checks the platform
# out at its own gate sha, so it would flip the day the writer merged, against a MeshWeaver.Plugins
# 231 commits behind). Hence the writer lands behind a per-caller `publication-layout` selector
# defaulting to `flat`, every producer's pin reaches it, and only then does each prefix flip — the
# `plugins` prefix in ONE change set because it is the only one with two producers.
# Doc/Architecture/SealedPublicationGenerations carries the table and the ordered phases.
#
# Cost: one `az storage file show` per published file per target, at seal time — measured against
# the ~43-file publication these lanes produce, ~1s each. That is the price of the assertion and it
# is deliberately paid in full: verifying a SAMPLE would be a guard that passes on the files nobody
# overwrote.
sha256_of() { # <file> — hex digest, portable across the CI runner and a developer's macOS
  if command -v sha256sum > /dev/null 2>&1; then
    sha256sum "$1" | awk '{print $1}'
  else
    shasum -a 256 "$1" | awk '{print $1}'
  fi
}

# 🚨 THE RENDERING IS MEASURED, NOT ASSUMED, and getting it wrong is silent. knack's `format_tsv`
# treats a TOP-LEVEL list as ROWS: `--query "[a, b]" -o tsv` prints a's value and b's value on TWO
# LINES, not as two columns — so `awk '{print $2}'` would read EMPTY for every file, every owner
# would compare unequal to $PUBLICATION, `ours` would be 0 on every publish and the postcondition
# would refuse EVERY publication in the fleet as "superseded". Hence a list-of-ONE-ROW: `[[a, b]]`
# renders one tab-separated line. The `|| '-'` guards exist because a null field renders as the
# LITERAL STRING 'None', not as empty. Both measured against azure-cli 2.90.0's own jmespath +
# knack formatter, and re-asserted with the real jmespath engine by test-publish-bake-overlap.py.
STAMP_QUERY="[[metadata.digest || '-', metadata.publication || '-']]"

# The ONE read-back, in one place. Both the per-file verification sweep and the sentinel check the
# convergence verdict makes parse the SAME rendering, so a query change cannot leave one of them
# reading a shape the other no longer produces. Answers through globals rather than stdout: an
# `::error::` written inside a command substitution would be CAPTURED instead of reaching the log.
#
#   rc 0  STAMP_DIGEST / STAMP_OWNER hold the values ('' where az rendered '-')
#   rc 1  unreadable — a transient fault, an expired credential, or the file is simply not there
#   rc 2  the CLI's rendering changed; STAMP_RAW/STAMP_FIELDS say what came back
#
# 🚨 `< /dev/null` because the sweep below runs this INSIDE a `while read` loop fed by the
# manifest: a command that consumed stdin would swallow the remaining lines and the loop would end
# early, having verified a PREFIX of the publication while reporting no foreign bytes at all.
STAMP_DIGEST=""
STAMP_OWNER=""
STAMP_RAW=""
STAMP_FIELDS=0
read_stamp() { # <account> <share> <path>
  local account="$1" share="$2" path="$3" props fields
  STAMP_DIGEST=""; STAMP_OWNER=""; STAMP_RAW=""; STAMP_FIELDS=0
  if ! props=$(az storage file show --account-name "$account" --share-name "$share" \
      --path "$path" --auth-mode login --backup-intent \
      --query "$STAMP_QUERY" -o tsv --only-show-errors < /dev/null); then
    return 1
  fi
  STAMP_RAW="$props"
  fields=$(printf '%s' "$props" | awk -F'\t' 'NR == 1 { print NF }')
  STAMP_FIELDS="${fields:-0}"
  # The field count is ASSERTED rather than trusted: if that rendering ever changes, this is a loud
  # refusal on the next publish instead of a fleet-wide false verdict.
  if [ "$STAMP_FIELDS" -ne 2 ]; then
    return 2
  fi
  STAMP_DIGEST=$(printf '%s' "$props" | awk -F'\t' 'NR == 1 { print $1 }')
  STAMP_OWNER=$(printf '%s' "$props" | awk -F'\t' 'NR == 1 { print $2 }')
  if [ "$STAMP_DIGEST" = "-" ]; then STAMP_DIGEST=""; fi
  if [ "$STAMP_OWNER" = "-" ]; then STAMP_OWNER=""; fi
  return 0
}

# The publication token: unique to THIS invocation, and readable as the run that made it. Metadata
# values are ASCII, and `-o tsv` splits on tabs, so it is restricted to [A-Za-z0-9._-] — a value
# carrying a tab or a space would silently split into two fields and compare equal to a truncation.
PUBLICATION="$(printf '%s-%s-%s' \
  "${GITHUB_REPOSITORY:-local}" "${GITHUB_RUN_ID:-$$}" "${GITHUB_RUN_ATTEMPT:-$(date -u +%s)}" \
  | tr -c 'A-Za-z0-9._-' '-')"

# Quoted by every refusal. It names both shapes because BOTH were measured on 2026-09-06, and the
# more frequent one is the one the issue title does not mention.
OVERLAP_WRITERS="One prefix, several publishers. ACROSS lanes: core CD's 'plugins-bake' job and the MeshWeaver.Plugins satellite's own 'publish-bake' both publish bake-source 'plugins' and resolve the same identity whenever the core surface has not moved between core's tip and the satellite's pin. WITHIN one lane: several main pushes bake concurrently — four same-identity window overlaps were measured in the satellite's own CI on 2026-09-06 alone, against zero cross-lane ones, so 'let the other repo finish' is not the whole answer. Find the publication named above, let it settle, and re-run this one."

# The manifest of what this run publishes: "<path-under-dest><TAB><sha256>", one line per file.
# Built ONCE — the local bytes are identical for every target — and consumed by both the upload
# loop and the verification below, so the two can never disagree about the set.
MANIFEST="$SENTINEL_LOCAL_DIR/manifest"
: > "$MANIFEST"
manifest_add() { # <path-under-dest> <local-file>
  printf '%s\t%s\n' "$1" "$(sha256_of "$2")" >> "$MANIFEST"
}
for zip in "${BUNDLES[@]}"; do manifest_add "$(basename "$zip")" "$zip"; done
for m in ${MODULES[@]+"${MODULES[@]}"}; do
  manifest_add "$MODULES_DIR_NAME/$(basename "$m")" "$m"
done
manifest_add "$MODULES_DIR_NAME/$MODULES_INDEX" "$MODULES_INDEX_LOCAL"
manifest_add "$SOURCE_MARKER" "$SOURCE_MARKER_LOCAL"
if [ "$HAS_SURFACE" = "true" ]; then manifest_add "$SURFACE_FILE" "$SURFACE_LOCAL"; fi
manifest_add "$REPO_MARKER" "$REPO_MARKER_LOCAL"
manifest_add "$ARCH_MARKER" "$ARCH_MARKER_LOCAL"
# The denominator every verification prints and every refusal quotes. A publication with nothing
# in it cannot be verified into existence, and a zero here would make the sweep below pass having
# read nothing — the exact vacuity a guard must never render as green.
MANIFEST_COUNT=$(grep -c '[^[:space:]]' "$MANIFEST" || true)
if [ "${MANIFEST_COUNT:-0}" -lt 1 ]; then
  echo "::error::the publication manifest is EMPTY — nothing would be verified before the seal, so the seal would claim completeness for an unchecked directory. Refusing."
  exit 1
fi
MARKER_COUNT=4
if [ "$HAS_SURFACE" = "true" ]; then MARKER_COUNT=5; fi
# The NON-PAYLOAD half of the manifest: the markers, the module index and the platform surface —
# everything that describes WHAT was baked rather than the compiled bytes. Two bakes of the same
# content produce byte-identical files here and DIFFERENT bundle zips (the compile is not
# reproducible byte-for-byte), so this split is what lets the convergence verdict below tell "a
# second publication of MY content beat me to it" from "somebody published something else".
# One name per line, so the membership test cannot match a substring.
MARKER_RELS="$SOURCE_MARKER
$REPO_MARKER
$ARCH_MARKER
$MODULES_DIR_NAME/$MODULES_INDEX"
if [ "$HAS_SURFACE" = "true" ]; then
  MARKER_RELS="$MARKER_RELS
$SURFACE_FILE"
fi
is_marker() { # <path-under-dest>
  case "
$MARKER_RELS
" in
    *"
$1
"*) return 0 ;;
  esac
  return 1
}
echo "publication $PUBLICATION: $MANIFEST_COUNT file(s) to publish and verify per target (${#BUNDLES[@]} bundle(s), ${#MODULES[@]} module(s), $MARKER_COUNT marker/index file(s)$([ "$HAS_SURFACE" = "true" ] && echo ", surface published" || echo ", NO platform surface"))"

# Uploads ONE file of the publication, stamped with the two metadata values the postcondition
# reads back. The digest is LOOKED UP from the manifest rather than recomputed: a file uploaded
# that the manifest does not describe would be published and never verified, so that is fatal
# rather than a fresh digest.
upload_published_file() { # <account> <share> <dest> <path-under-dest> <local-file> [<upload-path>]
  local account="$1" share="$2" dest="$3" rel="$4" src="$5" upload_path="${6:-$3/$4}" digest
  digest=$(awk -F'\t' -v k="$rel" '$1 == k { print $2; found = 1 } END { exit !found }' "$MANIFEST") || {
    echo "::error::'$rel' is being uploaded but the publication manifest does not describe it — it would be published and never verified. This is a bug in this script, not in the bake."
    exit 1
  }
  az storage file upload --account-name "$account" --share-name "$share" \
    --path "$upload_path" --source "$src" \
    --metadata "digest=$digest" "publication=$PUBLICATION" \
    --auth-mode login --backup-intent --only-show-errors > /dev/null
}

# Removes a seal that is known to cover a MIX. Called only from the mixed verdict below, and it
# is the other half of the fix: a foreign publisher can finish and seal INSIDE a gap in this run's
# uploads, so refusing to write our own sentinel is not enough — the sentinel already there covers
# a directory we have since partly overwritten. Deleting it returns the prefix to the state every
# reader skips.
#
# 🚨 It is NEVER called on the fully-superseded verdict. A publication whose files carry none of
# our bytes is somebody else's, complete and consistent; deleting its seal would un-publish a good
# publication and strand the identity unsealed until that lane's content next changes. "Some of
# ours, some of theirs" is the only state in which the seal is provably wrong.
unseal_mixed_publication() { # <account> <share> <dest>
  local account="$1" share="$2" dest="$3" present
  present=$(az storage file exists --account-name "$account" --share-name "$share" \
    --path "$dest/$SENTINEL" --auth-mode login --backup-intent --query exists -o tsv \
    --only-show-errors) || present="unknown"
  if [ "$present" != "true" ]; then
    echo "::notice::$dest carries no $SENTINEL (exists=$present) — the mix is already unreadable to every consumer; nothing to remove."
    return 0
  fi
  if az storage file delete --account-name "$account" --share-name "$share" \
      --path "$dest/$SENTINEL" --auth-mode login --backup-intent --only-show-errors > /dev/null; then
    echo "::warning::removed $dest/$SENTINEL — it sealed a directory holding bytes from two publications. The prefix is now unsealed (consumers skip it) rather than serving a mix; the next publication of either lane republishes it wholesale."
    return 0
  fi
  echo "::error::$dest/$SENTINEL seals a MIX and could not be removed — consumers will keep adopting bytes from two publications until a publication of either lane replaces it. Remove it by hand."
  return 1
}

# ══════════════ THE CONVERGENCE VERDICT — the one supersession that is not a failure ══════════════
#
# 🚨 The postcondition below and the sealed-skip in `publish_to_target` are answering the SAME
# question — "is the publication this run was asked to make on the shelf?" — and until this
# function existed they disagreed, because the skip keys on CONTENT × FRAMEWORK while the
# postcondition can only ask "are these MY bytes". A run that finds the shelf unsealed, uploads,
# and is then overwritten by a sibling publishing the SAME content therefore went RED for a
# publication that is present, whole and correct.
#
# Measured on the incident that made this loud: core CD runs 34205409381 and 34206854855 both
# published `plugins` at source cfac152ef023bc8e16203511aa60b50f581d3161 for identity
# s057b1e7785fba6c6ba079e9d84a0c00d on 2026-09-08. Each won one of the two targets and each went
# red on the other, with the same verdict — `40 of 45 file(s) were overwritten … the remaining 5
# are byte-identical`. The 40 are bundle zips and module packages (the compile is not reproducible
# byte-for-byte); the 5 are source-commit.txt, repository.txt, architecture.txt, modules/_index and
# platform-surface.json — byte-identical BECAUSE it is the same content. Both targets ended sealed
# with exactly the right bytes, and both CD runs failed. That is a false red, not a caught defect.
#
# So a supersession converges only on POSITIVE, byte-level proof of all three:
#
#   1. every non-payload file is byte-identical to ours (foreign markers 0, and the neutral markers
#      count reaches MARKER_COUNT — the denominator, so it cannot pass having checked nothing);
#   2. every foreign file names ONE publication, and that publication is stamped (an unstamped file
#      can never satisfy this: '<unstamped>' is not a legal token);
#   3. `_complete` is on the shelf, its digest is the sha256 of OUR listing (so the sealed bundle
#      SET is ours, name for name) and it was sealed by that same publication (so the seal belongs
#      to the bytes, not to some earlier publication a third writer left behind).
#
# Anything less is the superseded RED, unchanged, with the reason named. 🚨 This never touches the
# MIX verdict (ours > 0), never seals, never deletes, and never reports a publication that is not
# there: it reports that SOMEONE ELSE made the exact one this run was asked for. The residual it
# does NOT cover is a sibling that has not sealed yet when this run's sweep ends — 20 seconds, in
# the incident above. Shrinking that further is a bound, not a fix; the fix is the generation
# layout (Doc/Architecture/SealedPublicationGenerations), where the two runs never share a
# directory and neither has to lose.
CONVERGENCE_REASON=""
converged_on_equivalent() { # <account> <share> <dest> <foreign-markers> <neutral-markers> <foreign-owners>
  local account="$1" share="$2" dest="$3" fmark="$4" nmark="$5" owners="$6"
  local distinct count owner expected rc=0
  CONVERGENCE_REASON=""
  if [ "$fmark" -ne 0 ] || [ "$nmark" -ne "$MARKER_COUNT" ]; then
    CONVERGENCE_REASON="the shelf describes DIFFERENT content: $fmark of the $MARKER_COUNT marker/index file(s) differ from this bake's and only $nmark were byte-identical. source-commit.txt, modules/_index and platform-surface.json are byte-identical between two bakes of the same content, so a difference here means the publication on the shelf is not the one this run was asked to make."
    return 1
  fi
  distinct=$(printf '%s\n' $owners | sed '/^[[:space:]]*$/d' | sort -u)
  count=$(printf '%s\n' "$distinct" | grep -c '[^[:space:]]' || true)
  if [ "${count:-0}" -ne 1 ]; then
    CONVERGENCE_REASON="the foreign bytes name ${count:-0} publication(s) ($(printf '%s' "$distinct" | tr '\n' ' ')) — convergence needs exactly one, whole and identifiable."
    return 1
  fi
  owner="$distinct"
  case "$owner" in
    *"<"*|*">"*)
      CONVERGENCE_REASON="the foreign bytes carry no publication stamp, so nothing can establish that they are one publication rather than several."
      return 1 ;;
  esac
  expected=$(sha256_of "$SENTINEL_LOCAL")
  read_stamp "$account" "$share" "$dest/$SENTINEL" || rc=$?
  if [ "$rc" -ne 0 ]; then
    CONVERGENCE_REASON="publication '$owner' has not sealed $dest/$SENTINEL yet (or it could not be read) — an unsealed directory is not a publication, and this run must not report one that does not exist."
    return 1
  fi
  if [ "$STAMP_DIGEST" != "$expected" ]; then
    CONVERGENCE_REASON="$dest/$SENTINEL lists a different bundle set (it holds ${STAMP_DIGEST:-<no digest>}, this bake's listing hashes to $expected) — the sealed publication is not this bake's set, name for name."
    return 1
  fi
  if [ "$STAMP_OWNER" != "$owner" ]; then
    CONVERGENCE_REASON="$dest/$SENTINEL was sealed by '${STAMP_OWNER:-<unstamped>}' but the bytes under it were written by '$owner' — the seal does not belong to the publication on the shelf."
    return 1
  fi
  echo "::warning title=Superseded by an equivalent publication — this content IS published::$account/$share/$dest was published by '$owner' while this run was in flight, and it is byte-for-byte the publication this run was asked to make: all $MARKER_COUNT marker/index file(s) are identical (same source ${SOURCE_SHA:-unknown}, same module set, same platform surface), the sealed listing is this bake's bundle set, and '$owner' sealed it. Only the compiled bundle bytes differ, which two bakes of one commit always do. Nothing of this run reached this target and nothing needed to (MeshWeaver#3461)."
  return 0
}

# THE POSTCONDITION. Runs immediately before the seal. Every file is sorted into three buckets,
# and the buckets are what decide the verdict:
#
#   OURS      the digest matches what we uploaded AND our publication token wrote it. Only this run
#             could have put those bytes there.
#   NEUTRAL   the digest matches ours but another publication wrote it. The bytes are the ones we
#             uploaded, so this file is compatible with BOTH publications and distinguishes
#             nothing. This bucket is not a curiosity: `architecture.txt` is 'linux-x64' in every
#             bake, `modules/_index` is the same list whenever the module set is unchanged, and a
#             bundle a narrowed bake did not touch is byte-identical across two source shas.
#   FOREIGN   the digest differs, or there is none. Somebody else's bytes are on the shelf.
#
#   FOREIGN = 0                → SEAL. Every file is byte-for-byte this run's publication.
#   OURS = 0 and FOREIGN > 0   → SUPERSEDED. Nothing that distinguishes this run survived: the
#                                shelf is another publication, whole and self-consistent. LEAVE ITS
#                                SEAL ALONE and go RED — a run that reports "published" having
#                                shipped nothing is the silent-nothing outcome this script exists
#                                to make impossible, and deleting a good seal would strand the
#                                identity unsealed until that lane's content next changes.
#   OURS > 0 and FOREIGN > 0   → MIX. Refuse, and remove any sentinel over it.
#
# 🚨 Counting NEUTRAL as OURS is the mistake that turns a clean supersession into a false "mix",
# and a false mix DELETES a publication that was never wrong. Measured while building this: with
# the satellite's publication whole on the shelf, `architecture.txt` alone made OURS = 1 and the
# core lane deleted the satellite's seal.
verify_publication() { # <account> <share> <dest>
  local account="$1" share="$2" dest="$3" rel expected actual owner rc ours=0 neutral=0
  local foreign_markers=0 neutral_markers=0 foreign_owners=""
  local -a foreign=() unreadable=() overlapped=()
  while IFS=$'\t' read -r rel expected; do
    [ -n "$rel" ] || continue
    # 🚨 FAIL CLOSED. `|| echo ""` on the read would turn a transient fault, an expired credential
    # or a CLI shape change into "no digest recorded" — the one answer that is indistinguishable
    # from "another publisher wrote this", and the errors below would then name the wrong cause.
    # An unreadable file is refused as loudly as a foreign one; it is simply refused by name. The
    # accounting assertion after the loop is the belt to `read_stamp`'s `< /dev/null` brace.
    rc=0
    read_stamp "$account" "$share" "$dest/$rel" || rc=$?
    if [ "$rc" -eq 1 ]; then
      unreadable+=("$rel")
      continue
    fi
    if [ "$rc" -ne 0 ]; then
      echo "::error::az answered '$STAMP_RAW' for $dest/$rel — one tab-separated row of TWO fields was expected from --query \"$STAMP_QUERY\" -o tsv. The CLI's rendering has changed; this verification cannot be trusted until the parse is updated to match."
      unreadable+=("$rel (unexpected --query rendering: $STAMP_FIELDS field(s))")
      continue
    fi
    actual="$STAMP_DIGEST"
    owner="$STAMP_OWNER"
    if [ -z "$actual" ]; then
      # Present, but carrying no digest at all: written by a publisher that predates this stamp
      # (a repo still pinned to an older copy of this script) or by something else entirely.
      # Either way its bytes cannot be established, and the seal must not claim them.
      foreign+=("$rel (no digest recorded — written by a publisher that does not stamp one)")
      # '<' and '>' can never occur in a publication token, so an unstamped file can never be
      # counted towards "one foreign publication wrote all of this" below. Fail-closed by shape.
      foreign_owners="$foreign_owners <unstamped>"
      if is_marker "$rel"; then foreign_markers=$((foreign_markers + 1)); fi
      continue
    fi
    if [ "$actual" != "$expected" ]; then
      foreign+=("$rel (we uploaded $expected, the shelf holds $actual, written by publication '${owner:-<unstamped>}')")
      foreign_owners="$foreign_owners ${owner:-<unstamped>}"
      if is_marker "$rel"; then foreign_markers=$((foreign_markers + 1)); fi
      continue
    fi
    if [ "$owner" = "$PUBLICATION" ]; then
      ours=$((ours + 1))
    else
      neutral=$((neutral + 1))
      if is_marker "$rel"; then neutral_markers=$((neutral_markers + 1)); fi
      overlapped+=("$rel ← '${owner:-<unstamped>}'")
    fi
  done < "$MANIFEST"

  # Each element is "<file> ← '<publication>'" and carries spaces, so it is iterated quoted, behind
  # an emptiness test — the same shape as the `foreign` and `unreadable` loops below. (The
  # `${a[@]+"${a[@]}"}` idiom used elsewhere in this file does preserve per-element quoting; this
  # is written the long way because a reader should not have to know that to be sure.)
  if [ "${#overlapped[@]}" -gt 0 ]; then
    for rel in "${overlapped[@]}"; do
      echo "::notice::under $dest: $rel — that publication wrote the very bytes this run uploaded, so two publications overlapped on this prefix (MeshWeaver#3461). Identical bytes distinguish nothing, so this file is not a mix."
    done
  fi

  # 🚨 THE DENOMINATOR, ASSERTED. Every manifest line must have landed in exactly one bucket. It
  # cannot fail by inspection — which is precisely why it is checked: the loop is fed by a
  # redirect, so anything inside it that consumed stdin would end it EARLY, and a verification that
  # covered the first few files would then report "no foreign bytes" and SEAL. A guard that can
  # quietly check less than it claims is the vacuity every gate here is written to avoid.
  local accounted=$((ours + neutral + ${#foreign[@]} + ${#unreadable[@]}))
  if [ "$accounted" -ne "$MANIFEST_COUNT" ]; then
    echo "::error title=Refusing to seal — the verification did not cover the publication::$account/$share/$dest: $accounted of $MANIFEST_COUNT file(s) were accounted for ($ours ours, $neutral byte-identical, ${#foreign[@]} foreign, ${#unreadable[@]} unreadable). The read-back loop ended early, so this publication has NOT been shown to be free of another publisher's bytes. Refusing."
    return 1
  fi

  # An unreadable file leaves the verdict UNDECIDABLE, so it takes precedence over all three and
  # never touches an existing sentinel: we cannot tell a mix from a clean supersession.
  if [ "${#unreadable[@]}" -gt 0 ]; then
    echo "::error title=Refusing to seal — the publication could not be read back::$account/$share/$dest: ${#unreadable[@]} of $MANIFEST_COUNT file(s) could not be read after being uploaded, so whether this directory holds one publication or two cannot be established. Refusing rather than assuming — that assumption is what would seal a mix."
    local entry
    for entry in "${unreadable[@]}"; do echo "::error::could not read back after uploading it: $entry"; done
    return 1
  fi

  if [ "${#foreign[@]}" -eq 0 ]; then
    echo "verified: $account/$share/$dest — $((ours + neutral))/$MANIFEST_COUNT file(s) hold this run's bytes ($ours written by publication $PUBLICATION, $neutral byte-identical from another)"
    return 0
  fi

  local entry
  if [ "$ours" -eq 0 ]; then
    # 🚨 BEFORE the refusal, and it is the only path that can turn a supersession green: the shelf
    # may hold the publication this run was asked to make, sealed by a sibling that raced it. That
    # is PROVED on the bytes or not concluded at all — see converged_on_equivalent.
    if converged_on_equivalent "$account" "$share" "$dest" \
        "$foreign_markers" "$neutral_markers" "$foreign_owners"; then
      return 2
    fi
    echo "::error title=Refusing to seal — this run's publication was entirely superseded::$account/$share/$dest holds nothing that distinguishes this run: ${#foreign[@]} of $MANIFEST_COUNT file(s) were overwritten by another publication while this one was in flight, and the remaining $neutral are byte-identical in both (MeshWeaver#3461). That publication is whole, and is left exactly as it is — sealing OUR sentinel over it would claim a bundle set that is not there. Nothing of this run reached this target."
    echo "::error::this is NOT the same publication as the one this run baked: $CONVERGENCE_REASON"
    for entry in "${foreign[@]}"; do echo "::error::superseded: $entry"; done
    echo "::error::$OVERLAP_WRITERS"
    return 1
  fi

  echo "::error title=Refusing to seal — this directory holds a MIX of two publications::$account/$share/$dest holds $ours file(s) only this run could have written and ${#foreign[@]} written by another publication ($neutral more are byte-identical in both; $MANIFEST_COUNT in total). Sealing now would write a sentinel over bytes from two bakes — one seal, one generation, self-consistent to every consumer and wrong (MeshWeaver#3461). Its first symptom is 'dependency record mismatch — built against mvid:…' on a portal that renders nothing."
  for entry in "${foreign[@]}"; do echo "::error::overwritten during this publication: $entry"; done
  echo "::error::$OVERLAP_WRITERS"
  unseal_mixed_publication "$account" "$share" "$dest" || true
  return 1
}

# The release-marker directory (must match PublishedBundleCatalogue.ReleaseMarkerDirectoryName).
# Leading underscore so it can never collide with a framework-identity directory (s… / g…).
RELEASES_DIR="_releases"
RELEASE_MARKER_LOCAL=""
if [ -n "${RELEASE_VERSION:-}" ]; then
  # A version containing a path separator would escape the directory; the platform's versions are
  # semver tags, so anything else is a caller bug and must be loud, not silently rewritten.
  case "$RELEASE_VERSION" in
    */*|..|.) echo "::error::release-version '$RELEASE_VERSION' is not a plain version string"; exit 1;;
  esac
  RELEASE_MARKER_LOCAL="$SENTINEL_LOCAL_DIR/$RELEASE_VERSION"
  printf '%s\n' "$IDENTITY" > "$RELEASE_MARKER_LOCAL"
fi

ensure_directory() { # <account> <share> <dir-path>
  local account="$1" share="$2" dest="$3" path="" part
  # az storage directory create is not recursive and errors on an existing directory on some CLI
  # versions — create each level only when absent.
  local IFS='/'
  for part in $dest; do
    path="${path:+$path/}$part"
    local exists
    exists=$(az storage directory exists --account-name "$account" --share-name "$share" \
      --name "$path" --auth-mode login --backup-intent --query exists -o tsv --only-show-errors)
    if [ "$exists" != "true" ]; then
      az storage directory create --account-name "$account" --share-name "$share" \
        --name "$path" --auth-mode login --backup-intent --only-show-errors > /dev/null
    fi
  done
}

# The version → framework-identity mapping the release gates read (#1754/#1755). Written on every
# run for every target, never gated on the sealed-skip — see the header. The file NAME is the
# platform version and its CONTENT is the identity, so a reader needs one stat plus one read and
# no listing.
publish_release_marker() { # <account> <share> <base>
  local account="$1" share="$2" base="$3"
  local dir="${base:+$base/}prebuilt-bundles/$RELEASES_DIR"
  ensure_directory "$account" "$share" "$dir"
  # Same directory-as---path trick as the sentinel below: the CLI appends the source basename, and
  # the local file is already named after the version.
  az storage file upload --account-name "$account" --share-name "$share" \
    --path "$dir" --source "$RELEASE_MARKER_LOCAL" \
    --auth-mode login --backup-intent --only-show-errors > /dev/null
  echo "release marker: $account/$share/$dir/$RELEASE_VERSION → $IDENTITY"
}

publish_one_target() { # <account> <share> <dest-dir> <resealing>
  local account="$1" share="$2" dest="$3" resealing="$4"
  ensure_directory "$account" "$share" "$dest"
  # Republishing OVER a sealed directory: UNSEAL first. Readers must never seed a mid-replace
  # mix of old and new bundles under a stale sentinel — deleting the sentinel returns the
  # directory to the not-yet-complete state readers skip, and the re-seal below closes it again.
  if [ "$resealing" = "true" ]; then
    az storage file delete --account-name "$account" --share-name "$share" \
      --path "$dest/$SENTINEL" --auth-mode login --backup-intent --only-show-errors > /dev/null
    echo "unsealed: $account/$share/$dest ($SENTINEL removed — content changed, republishing)"
  fi
  local zip
  for zip in "${BUNDLES[@]}"; do
    upload_published_file "$account" "$share" "$dest" "$(basename "$zip")" "$zip"
    echo "published: $account/$share/$dest/$(basename "$zip")"
  done
  # The module set: every bundle, then its index — both strictly before the sentinel.
  ensure_directory "$account" "$share" "$dest/$MODULES_DIR_NAME"
  local m
  for m in ${MODULES[@]+"${MODULES[@]}"}; do
    upload_published_file "$account" "$share" "$dest" \
      "$MODULES_DIR_NAME/$(basename "$m")" "$m"
    echo "published module: $account/$share/$dest/$MODULES_DIR_NAME/$(basename "$m")"
  done
  upload_published_file "$account" "$share" "$dest" \
    "$MODULES_DIR_NAME/$MODULES_INDEX" "$MODULES_INDEX_LOCAL" "$dest/$MODULES_DIR_NAME"
  echo "module set: $account/$share/$dest/$MODULES_DIR_NAME/$MODULES_INDEX (${#MODULES[@]} bundle(s))"
  # 🚨 Both marker uploads pass the DIRECTORY as --path on purpose — the CLI appends the source
  # basename. An extensionless "$dest/$SENTINEL" --path would be silently re-interpreted as a
  # DIRECTORY and fail ParentNotFound (see the SENTINEL_LOCAL comment above).
  upload_published_file "$account" "$share" "$dest" "$SOURCE_MARKER" "$SOURCE_MARKER_LOCAL" "$dest"
  # The platform surface (#3651) — before repository.txt and architecture.txt, which stay the last
  # uploads before the postcondition (the overlap harness hooks its second publisher onto them).
  # A ".json" --path is a FILE to the CLI, so the full path is passed; the directory trick the
  # extensionless markers need does not apply here.
  if [ "$HAS_SURFACE" = "true" ]; then
    upload_published_file "$account" "$share" "$dest" "$SURFACE_FILE" "$SURFACE_LOCAL"
    echo "published surface: $account/$share/$dest/$SURFACE_FILE"
  fi
  upload_published_file "$account" "$share" "$dest" "$REPO_MARKER" "$REPO_MARKER_LOCAL" "$dest"
  upload_published_file "$account" "$share" "$dest" "$ARCH_MARKER" "$ARCH_MARKER_LOCAL" "$dest"
  # 🚨 THE POSTCONDITION, between the last content upload and the seal (MeshWeaver#3461). Every
  # file above is read back and must still carry THIS run's digest; a foreign publisher that
  # overwrote any of them makes this refuse, and the directory stays sentinel-less rather than
  # sealed over a mix. See the long note beside verify_publication.
  #
  # rc 2 is the CONVERGENCE verdict: a sibling published this exact content and sealed it while
  # this run was in flight. The target is satisfied and MUST NOT be sealed again — writing our own
  # sentinel over their files would stamp their publication with this run's token and make the
  # next carry-forward read two publications where there is one.
  local verdict=0
  verify_publication "$account" "$share" "$dest" || verdict=$?
  if [ "$verdict" -eq 2 ]; then
    echo converged >> "$OUTCOMES"
    return 0
  fi
  if [ "$verdict" -ne 0 ]; then
    exit 1
  fi
  # LAST write — the atomic completeness marker. Anything that dies before this line leaves the
  # directory sentinel-less: unreadable to portals, re-published wholesale by the next run.
  az storage file upload --account-name "$account" --share-name "$share" \
    --path "$dest" --source "$SENTINEL_LOCAL" \
    --metadata "digest=$(sha256_of "$SENTINEL_LOCAL")" "publication=$PUBLICATION" \
    --auth-mode login --backup-intent --only-show-errors > /dev/null
  echo "sealed: $account/$share/$dest/$SENTINEL (${#BUNDLES[@]} bundle(s), source ${SOURCE_SHA:-unknown}, platform surface: $HAS_SURFACE)"
  # 🚨 Recorded HERE, beside the seal, and not at the call sites: since the convergence verdict
  # returns 0 without sealing, a caller counting "publish_one_target returned" as "published"
  # would report a publication this run did not make.
  echo published >> "$OUTCOMES"
}

# 🚨 PER-TARGET ISOLATION (Plugins #2682, 2026-08-30). Each target is published in its OWN subshell:
# a failure — an unreadable marker, a share that no longer exists, an upload that dies — fails THAT
# target and the loop moves on, so a dead entry in BAKE_PUBLISH_TARGETS can no longer starve the
# live ones. It is NOT a skip: every failed target is named at the end and the script exits 1. For
# three days every Plugins main bake sealed the two live shares and then died on the third (a torn-
# down instance's share, gone from the storage account) — whether anything sealed was ordering luck.
# The refusals inside stay exactly as strict (an unreadable marker is still not an absent one); only
# their blast radius shrinks from "the whole publication" to "this target".
OUTCOMES="$SENTINEL_LOCAL_DIR/outcomes"
: > "$OUTCOMES"
publish_to_target() { # <target> — called in a SUBSHELL by the loop below: `exit 1` fails this target only
  local target="$1"
  ACCOUNT="${target%%/*}"
  REST="${target#*/}"
  SHARE="${REST%%/*}"
  BASE=""
  case "$REST" in */*) BASE="${REST#*/}";; esac
  if [ -z "$ACCOUNT" ] || [ -z "$SHARE" ] || [ "$ACCOUNT" = "$target" ]; then
    echo "::error::malformed BAKE_PUBLISH_TARGETS entry '$target' — expected <account>/<share>[/<base-path>]"
    exit 1
  fi
  # 🚨 BEFORE the sealed-skip below, which `continue`s past everything that follows it. The
  # version → identity mapping must land on EVERY run — including the (common) run whose bundles
  # are already published — or the release gates would hold every environment on a release that is
  # perfectly fine, and an environment frozen for weeks is its own outage. A failure here is fatal
  # by `set -e`, deliberately: a silently missing marker holds everything.
  if [ -n "${RELEASE_MARKER_LOCAL:-}" ]; then
    publish_release_marker "$ACCOUNT" "$SHARE" "$BASE"
    echo marker >> "$OUTCOMES"
  fi
  DEST="${BASE:+$BASE/}prebuilt-bundles/$IDENTITY/$SOURCE"
  # "Rebuild only when we need to" applies to the publish too (#1660 WS3), but the key is
  # CONTENT × FRAMEWORK: a sealed directory is already-published only when the framework
  # identity (the directory) AND the source commit (the marker) both match. A framework-only
  # skip would freeze a node repo's FIRST publication for the whole framework release — every
  # later content merge resolves the same framework identity, finds the seal, and ships nothing.
  #
  # 🚨 The skip keys on the _complete SENTINEL, never on "any file exists": the sentinel is
  # written LAST, after every bundle uploaded, so a publish that died mid-way (cancelled run,
  # network fault) leaves a directory WITHOUT it — and the next publish re-uploads everything
  # (idempotent overwrites) instead of freezing the identity incomplete forever. The read side
  # (ShippedPrebuiltBundles.SeedPublishedRoot) honours the same contract: a source directory
  # without its sentinel is never seeded.
  # 🚨 CROSS-ARCHITECTURE GUARD — runs BEFORE the skip/reseal decision below, because the SKIP is
  # itself one of the two silent failures: a second architecture publishing the same source commit
  # under a g<sha> identity would be told "already published" and ship nothing, leaving its pods to
  # adopt the other architecture's bytes. Refusing is always safe (the lane fails, an operator
  # reads why); overwriting or skipping is not.
  #
  # Deliberately NOT a skip and NOT a warning. A warning here would be read as noise by the one
  # run that most needs to stop, and a skip is the defect.
  # 🚨 EXISTENCE FIRST, and fail CLOSED. Reading the marker with `download … || echo ""` would
  # turn every failure — a transient fault, an expired credential, a CLI error — into "no marker
  # recorded", which is the one answer that lets the publish proceed. A guard whose error path is
  # indistinguishable from its permissive path is not a guard.
  arch_exists=$(az storage file exists --account-name "$ACCOUNT" --share-name "$SHARE" \
    --path "$DEST/$ARCH_MARKER" --auth-mode login --backup-intent --query exists -o tsv \
    --only-show-errors 2>/dev/null || echo "unknown")
  if [ "$arch_exists" != "true" ] && [ "$arch_exists" != "false" ]; then
    echo "::error::could not determine whether $ACCOUNT/$SHARE holds $DEST/$ARCH_MARKER (az returned no usable answer). Refusing rather than assuming the marker is absent — that assumption is what would let one architecture overwrite another's publication."
    exit 1
  fi
  published_arch=""
  if [ "$arch_exists" = "true" ]; then
    if ! az storage file download --account-name "$ACCOUNT" --share-name "$SHARE" \
        --path "$DEST/$ARCH_MARKER" --dest "$SENTINEL_LOCAL_DIR/remote-$ARCH_MARKER" \
        --auth-mode login --backup-intent --only-show-errors > /dev/null 2>&1; then
      echo "::error::$DEST/$ARCH_MARKER EXISTS under $ACCOUNT/$SHARE but could not be read. Refusing: an unreadable marker is not an absent one."
      exit 1
    fi
    published_arch="$(tr -d '[:space:]' < "$SENTINEL_LOCAL_DIR/remote-$ARCH_MARKER")"
    rm -f "$SENTINEL_LOCAL_DIR/remote-$ARCH_MARKER"
    if [ -z "$published_arch" ]; then
      echo "::error::$DEST/$ARCH_MARKER under $ACCOUNT/$SHARE is present but EMPTY — the incumbent's architecture cannot be established, so this publication cannot be proven safe. Refusing."
      exit 1
    fi
  fi
  if [ -n "$published_arch" ] && [ "$published_arch" != "$BAKE_ARCHITECTURE" ]; then
    echo "::error::$ACCOUNT/$SHARE holds a publication under $DEST built for '$published_arch', but this bake is '$BAKE_ARCHITECTURE'. One framework identity cannot hold two architectures: the reference assemblies differ, so pods resolving this identity would adopt bytes they did not build against (TypeLoadException inside a collectible ALC at activation). This means the identity is architecture-INDEPENDENT — a g<sha> commit stamp rather than an s<hash> surface hash — so the two lanes need distinct identities before both can publish. Refusing rather than overwriting '$published_arch'."
    exit 1
  fi
  complete=$(az storage file exists --account-name "$ACCOUNT" --share-name "$SHARE" \
    --path "$DEST/$SENTINEL" --auth-mode login --backup-intent --query exists -o tsv \
    --only-show-errors 2>/dev/null || echo false)
  # An incumbent with NO architecture marker predates this recording, and the only lane that has
  # ever published is amd64 — so it is treated as linux-x64.
  if [ -z "$published_arch" ] && [ "$complete" = "true" ]; then
    if [ "$BAKE_ARCHITECTURE" = "linux-x64" ]; then
      # 🚨 BACKFILL, and it must happen HERE — before the sealed-skip below, which `continue`s.
      # Without it the two rules deadlock: the arm64 lane refuses an unmarked incumbent and points
      # at this lane to stamp it, while this lane recognises its own publication, skips, and never
      # writes the marker — so the arm64 lane is blocked forever by an instruction that can never
      # be carried out. Stamping is safe precisely because the refusal below is sound: only the
      # amd64 lane has ever published, and this IS that lane.
      #
      # 🚨 Deliberately NOT `upload_published_file`: this writes onto a publication that is NOT
      # this run's, so stamping it with this run's `publication` token would be a lie — a later
      # verification would read one of somebody else's files as ours. It is safe to leave
      # unstamped because it only ever runs on a directory that IS sealed (`complete = true`), and
      # a publisher that is mid-flight has removed the sentinel — so no concurrent
      # `verify_publication` can be looking at this file while it is written. If this run goes on
      # to republish, `publish_one_target` overwrites it stamped a moment later.
      az storage file upload --account-name "$ACCOUNT" --share-name "$SHARE" \
        --path "$DEST" --source "$ARCH_MARKER_LOCAL" \
        --auth-mode login --backup-intent --only-show-errors > /dev/null
      echo "::notice::stamped $DEST/$ARCH_MARKER = $BAKE_ARCHITECTURE on a pre-existing publication (it predates architecture recording). Another architecture can now establish whether it may publish under this identity."
      published_arch="$BAKE_ARCHITECTURE"
    else
      echo "::error::$ACCOUNT/$SHARE holds a COMPLETE publication under $DEST with no $ARCH_MARKER — it predates architecture recording, so it can only be the linux-x64 lane. This bake is '$BAKE_ARCHITECTURE' and would overwrite it. The next linux-x64 publication to this target stamps the marker automatically (even when it skips the bundles); retry after it has run."
      exit 1
    fi
  fi
  resealing=false
  if [ "$complete" = "true" ]; then
    published_sha=$(az storage file download --account-name "$ACCOUNT" --share-name "$SHARE" \
      --path "$DEST/$SOURCE_MARKER" --dest "$SENTINEL_LOCAL_DIR/remote-$SOURCE_MARKER" \
      --auth-mode login --backup-intent --only-show-errors > /dev/null 2>&1 \
      && tr -d '[:space:]' < "$SENTINEL_LOCAL_DIR/remote-$SOURCE_MARKER" || echo "")
    # A publication sealed before module sealing existed has no modules/_index. Under the current
    # contract that publication is INCOMPLETE — a consumer cannot compose from it — so it is
    # republished even when the content is unchanged. This is what converges the fleet (#2698):
    # the next bake of each source re-seals it WITH its module set, and nothing is done by hand.
    module_set=$(az storage file exists --account-name "$ACCOUNT" --share-name "$SHARE" \
      --path "$DEST/$MODULES_DIR_NAME/$MODULES_INDEX" --auth-mode login --backup-intent --query exists -o tsv \
      --only-show-errors 2>/dev/null || echo unknown)
    if [ "$module_set" != "true" ]; then
      echo "sealed publication under $DEST carries no $MODULES_DIR_NAME/$MODULES_INDEX (exists=$module_set) — it predates module sealing; republishing WITH the module set."
      echo "→ $ACCOUNT/$SHARE: $DEST (${#BUNDLES[@]} bundle(s), ${#MODULES[@]} module(s))"
      publish_one_target "$ACCOUNT" "$SHARE" "$DEST" true
      return 0
    fi
    if [ -n "${SOURCE_SHA:-}" ] && [ "$published_sha" = "$SOURCE_SHA" ]; then
      echo "::notice::$ACCOUNT/$SHARE holds a COMPLETE publication of THIS content under $DEST (sentinel present, source $published_sha) — already published; skipping."
      return 0
    fi
    if [ -z "${SOURCE_SHA:-}" ]; then
      # No content identity given (framework-repo producer): the framework identity IS the
      # content key, so a sealed directory is already this publication.
      echo "::notice::$ACCOUNT/$SHARE holds a COMPLETE publication under $DEST ($SENTINEL present) — surface unchanged, bake already published; skipping."
      return 0
    fi
    resealing=true
    echo "sealed publication under $DEST is from source '${published_sha:-<unrecorded>}' but this bake is from '$SOURCE_SHA' — republishing."
  fi
  echo "→ $ACCOUNT/$SHARE: $DEST (${#BUNDLES[@]} bundle(s), ${#MODULES[@]} module(s))"
  publish_one_target "$ACCOUNT" "$SHARE" "$DEST" "$resealing"
}

FAILED=()
for target in $BAKE_PUBLISH_TARGETS; do
  if ( publish_to_target "$target" ); then
    :
  else
    FAILED+=("$target")
    echo "::error::target $target FAILED (see above) — continuing with the remaining targets so a dead target cannot starve the live ones"
  fi
done
PUBLISHED=$(awk '/^published$/ { c++ } END { print c + 0 }' "$OUTCOMES")
MARKERS=$(awk '/^marker$/ { c++ } END { print c + 0 }' "$OUTCOMES")
# Targets a sibling publication of THIS content had already sealed by the time this run's
# postcondition ran (MeshWeaver#3461). Counted separately from `published` so the summary never
# claims a seal this run did not write — and printed on every run, including zero, so the number
# is a denominator rather than an occasional line.
CONVERGED=$(awk '/^converged$/ { c++ } END { print c + 0 }' "$OUTCOMES")
if [ "${#FAILED[@]}" -gt 0 ]; then
  echo "::error::bake publication FAILED on ${#FAILED[@]} of $(printf '%s\n' $BAKE_PUBLISH_TARGETS | wc -l | tr -d ' ') target(s): ${FAILED[*]} — identity=$IDENTITY source=$SOURCE. Every OTHER target above was published and sealed; these were not. A target that no longer exists (a torn-down instance's share) belongs OUT of BAKE_PUBLISH_TARGETS — remove it, never route around it."
  exit 1
fi

echo "bake published: identity=$IDENTITY arch=$BAKE_ARCHITECTURE source=$SOURCE source-sha=${SOURCE_SHA:-unknown} bundles=${#BUNDLES[@]} surface=$HAS_SURFACE targets-published=$PUBLISHED targets-converged=$CONVERGED release=${RELEASE_VERSION:-none} release-markers=$MARKERS"
