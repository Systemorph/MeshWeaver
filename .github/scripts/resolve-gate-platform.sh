#!/usr/bin/env bash
# resolve-gate-platform.sh --volume-root <dir> [--tester-digest <sha256:…>] [--portal-digest <sha256:…>]
#
# Decides WHERE a gate shard takes the platform from, and — when that is the CI platform volume —
# proves the set on it is the one the caller pinned and is COMPLETE, before a single assembly is
# read. Prints `key=value` lines to stdout for the lane to consume (and to $GITHUB_OUTPUT when set).
#
# THE TWO MODES, and why the choice is a MEASUREMENT and not an input (MeshWeaver#4113):
#
#   volume     `<volume-root>` (the runner pod's read-only mount of the CI platform share,
#              /opt/platform — Systemorph/Memex deployments/aks/ci-runners/ci-platform.yaml) EXISTS.
#              The tester's /app and the portal's /app are already on the node; the shard runs the
#              tester as a PROCESS on the runner's .NET and needs no Docker daemon, no registry
#              credential and no pull.
#   container  `<volume-root>` does not exist — a GitHub-hosted `ubuntu-latest` runner, which has a
#              Docker daemon and no such mount. The shard pulls both images and runs the tester in
#              a container, exactly as the lane did before #4113.
#
# 🚨 THERE IS NO FALLBACK BETWEEN THEM, and that is the whole point. Once the mount is there, every
# failure below is RED and NAMES `ci-platform-refresh`; a set that is absent, half-written or paired
# with another portal must never degrade into `docker pull`. A silent fallback would turn "the
# refresh job has been dead for a day" into "the gate is a bit slower today", which is precisely the
# class of failure this lane exists to refuse. The mount's presence is a reliable signal rather than
# a guess: it is a Kubernetes volumeMount in the ARC scale sets' pod template, so it is there on
# every runner pod of both sets and on no GitHub-hosted runner (`/opt` is EMPTY in
# ghcr.io/actions/actions-runner:2.337.0 — measured 2026-09-12).
#
# THE LAYOUT it reads (owned by ci-platform-refresh.py; Azure Files forbids `:` in a name, so the
# directory is the digest with the first `:` turned into `-`):
#
#   <root>/current                       the newest set's TESTER digest, a FILE, read ONCE
#   <root>/sha256-<hex>/app/             the tester image's /app        (89 files' worth of CLI)
#   <root>/sha256-<hex>/platform-refs/   the PORTAL image's /app        (the reference surface)
#   <root>/sha256-<hex>/platform.json    set, core sha, both digests, installed-at
#   <root>/sha256-<hex>/.complete        the tester digest, written LAST — no .complete, no set
#
# WHAT IT DOES NOT DO: it never runs `docker`, never reaches a registry, and never writes to the
# volume. It is pure enough to be self-tested against synthetic directories, which is the only way
# the refusals above are known to be able to fire at all.
set -euo pipefail

REFRESH_JOB="ci-platform-refresh (Systemorph/Memex deployments/aks/ci-runners/ci-platform.yaml)"

die() { echo "::error::resolve-gate-platform: $1"; exit 1; }

emit() {  # <key> <value>
  printf '%s=%s\n' "$1" "$2"
  [ -n "${GITHUB_OUTPUT:-}" ] && printf '%s=%s\n' "$1" "$2" >> "$GITHUB_OUTPUT"
  return 0
}

# ── self-test ────────────────────────────────────────────────────────────────────────────────
#     .github/scripts/resolve-gate-platform.sh --self-test
# 🚨 THE "COULD THIS ASSERTION FAIL?" PROOF. Every refusal below is exercised against a synthetic
# volume, and each is asserted to (a) exit non-zero, (b) NAME the refresh job, and (c) emit no
# `mode=container` — i.e. never degrade into a pull. Deleting any one guard from the body makes
# this self-test RED; that is the property that makes the guard worth having. Run on every platform
# PR (dotnet-test.yml preflight) beside compose-gate-host.sh's.
if [ "${1:-}" = "--self-test" ]; then
  set +e
  self="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"
  tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' EXIT
  fail() { echo "SELF-TEST FAILED: $1"; exit 1; }
  D1="sha256:1111111111111111111111111111111111111111111111111111111111111111"
  D2="sha256:2222222222222222222222222222222222222222222222222222222222222222"
  P1="sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
  P2="sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"

  make_set() {  # <root> <tester-digest> <portal-digest>
    local root="$1" d="$2" p="$3" dir
    dir="$root/${2/:/-}"
    mkdir -p "$dir/app" "$dir/platform-refs"
    printf 'CLI'  > "$dir/app/mw-plugin-test.dll"
    printf '{}'   > "$dir/app/mw-plugin-test.runtimeconfig.json"
    printf 'X=1\n' > "$dir/platform-refs/meshweaver-surface.manifest"
    printf '{"tfm":"net10.0"}' > "$dir/platform-refs/Memex.Portal.Distributed.runtimeconfig.json"
    printf '{"set":"3.0.0-ci.1","sha":"deadbeef","image-digest":"%s","portal-image-digest":"%s"}\n' "$d" "$p" \
      > "$dir/platform.json"
    printf '%s\n' "$d" > "$dir/.complete"
    printf '%s\n' "$d" > "$root/current"
    printf '%s' "$dir"
  }

  # 1. No mount at all ⇒ container mode, exit 0. (The ubuntu-latest path stays byte-identical.)
  out="$("$self" --volume-root "$tmp/absent" --tester-digest "$D1" --portal-digest "$P1" 2>&1)" \
    || fail "a missing volume root must select container mode and exit 0 (got: $out)"
  grep -q '^mode=container$' <<<"$out" || fail "a missing volume root must emit mode=container (got: $out)"

  # 2. The happy path ⇒ volume mode, with every path resolved.
  good="$tmp/good"; dir="$(make_set "$good" "$D1" "$P1")"
  out="$("$self" --volume-root "$good" --tester-digest "$D1" --portal-digest "$P1" 2>&1)" \
    || fail "a complete matching set must be accepted (got: $out)"
  grep -q '^mode=volume$' <<<"$out"                   || fail "a complete set must emit mode=volume (got: $out)"
  grep -q "^tester_app=$dir/app$" <<<"$out"           || fail "tester_app must be <set>/app (got: $out)"
  grep -q "^portal_app=$dir/platform-refs$" <<<"$out" || fail "portal_app must be <set>/platform-refs (got: $out)"
  grep -q '^set=3.0.0-ci.1$' <<<"$out"                || fail "the set name must be reported (got: $out)"
  grep -qi 'docker' <<<"$out" && fail "volume mode must never mention docker (got: $out)"

  # 3. An empty pin (allow-unpinned) follows `current`, read ONCE.
  out="$("$self" --volume-root "$good" --tester-digest "" --portal-digest "" 2>&1)" \
    || fail "an empty pin must follow <root>/current (got: $out)"
  grep -q "^tester_app=$dir/app$" <<<"$out" || fail "an empty pin must resolve the set that 'current' names (got: $out)"

  # ── the refusals. Each must exit non-zero, name the refresh job, and never say mode=container. ──
  refuses() {  # <label> <root> <tester-digest> <portal-digest>
    local label="$1" root="$2" d="$3" p="$4" o rc
    o="$("$self" --volume-root "$root" --tester-digest "$d" --portal-digest "$p" 2>&1)"; rc=$?
    [ "$rc" -ne 0 ] || fail "$label must be refused, got exit 0: $o"
    grep -q 'ci-platform-refresh' <<<"$o" || fail "$label must name the refresh job (got: $o)"
    grep -q '^mode=container$' <<<"$o" && fail "$label must NOT fall back to a registry pull (got: $o)"
    return 0
  }

  # 3a. The pinned set is simply not installed (the refresh keeps only the 3 newest).
  refuses "a set the volume does not carry" "$good" "$D2" "$P1"
  # 3b. A half-install: the directory exists, `.complete` does not.
  half="$tmp/half"; hdir="$(make_set "$half" "$D1" "$P1")"; rm "$hdir/.complete"
  refuses "a set without .complete" "$half" "$D1" "$P1"
  # 3c. `.complete` naming ANOTHER digest — a directory reused, or a truncated write.
  wrong="$tmp/wrongmarker"; wdir="$(make_set "$wrong" "$D1" "$P1")"; printf '%s\n' "$D2" > "$wdir/.complete"
  refuses "a .complete that names another digest" "$wrong" "$D1" "$P1"
  # 3d. No CLI in app/ — the directory is not a tester /app.
  nocli="$tmp/nocli"; ndir="$(make_set "$nocli" "$D1" "$P1")"; rm "$ndir/app/mw-plugin-test.dll"
  refuses "a set whose app/ has no mw-plugin-test.dll" "$nocli" "$D1" "$P1"
  # 3e. No surface manifest in platform-refs/ — a host that resolves the fallback identity.
  noman="$tmp/nomanifest"; mdir="$(make_set "$noman" "$D1" "$P1")"; rm "$mdir/platform-refs/meshweaver-surface.manifest"
  refuses "a set whose platform-refs/ has no meshweaver-surface.manifest" "$noman" "$D1" "$P1"
  # 3f. The set pairs a DIFFERENT portal than the caller pinned — gating against another platform.
  refuses "a set paired with another portal digest" "$good" "$D1" "$P2"
  # 3g. `current` is absent and no pin was given — nothing names a set.
  nocur="$tmp/nocurrent"; make_set "$nocur" "$D1" "$P1" > /dev/null; rm "$nocur/current"
  refuses "an empty pin with no <root>/current" "$nocur" "" ""
  # 3h. platform.json missing — the set cannot be named, so it cannot be reported.
  nojson="$tmp/nojson"; jdir="$(make_set "$nojson" "$D1" "$P1")"; rm "$jdir/platform.json"
  refuses "a set without platform.json" "$nojson" "$D1" "$P1"

  # 4. Usage refusals.
  "$self" > /dev/null 2>&1 && fail "a missing --volume-root must be refused"
  "$self" --volume-root "$good" --tester-digest "not-a-digest" > /dev/null 2>&1 \
    && fail "a malformed digest must be refused"

  echo "resolve-gate-platform.sh self-test: OK (1 container case, 2 volume cases, 8 refusals, 2 usage refusals)"
  exit 0
fi

VOLUME_ROOT=""
TESTER_DIGEST=""
PORTAL_DIGEST=""
while [ $# -gt 0 ]; do
  case "$1" in
    --volume-root)   VOLUME_ROOT="${2:-}"; shift 2 ;;
    --tester-digest) TESTER_DIGEST="${2:-}"; shift 2 ;;
    --portal-digest) PORTAL_DIGEST="${2:-}"; shift 2 ;;
    *) die "unknown argument '$1' (usage: --volume-root <dir> [--tester-digest <sha256:…>] [--portal-digest <sha256:…>])" ;;
  esac
done
[ -n "$VOLUME_ROOT" ] || die "--volume-root is required — it is the path the runner pod mounts the CI platform share at (/opt/platform)"
for d in "$TESTER_DIGEST" "$PORTAL_DIGEST"; do
  case "$d" in
    "" | sha256:[0-9a-f][0-9a-f]* ) : ;;
    *) die "'$d' is not a digest — pass 'sha256:<hex>' or nothing (allow-unpinned)" ;;
  esac
done

# ── the mode ────────────────────────────────────────────────────────────────────────────────
if [ ! -d "$VOLUME_ROOT" ]; then
  emit mode container
  echo "no platform volume at '$VOLUME_ROOT' on runner '${RUNNER_NAME:-?}' — this shard pulls both images and runs the tester in a container (the pre-#4113 path, which is what a GitHub-hosted ubuntu-latest runner takes). The self-hosted ARC scale sets mount the share at /opt/platform; a runner that should have it and does not is a pod started before the mount was added."
  exit 0
fi

# From here on the mount IS there, so every failure is RED and names the refresh job. Nothing below
# may end in `mode=container`.
resolved="$TESTER_DIGEST"
if [ -z "$resolved" ]; then
  # allow-unpinned: the lane follows the tag, so the volume follows `current` — read ONCE, because
  # the share is one live SMB view and a refresh may land mid-job.
  [ -f "$VOLUME_ROOT/current" ] \
    || die "the platform volume at '$VOLUME_ROOT' has no 'current' file, and this caller pinned no image-digest (allow-unpinned), so nothing names a set to gate with. That file is written by $REFRESH_JOB after it installs a set; an empty volume means the refresh has never completed a run. This gate does NOT fall back to a registry pull."
  resolved="$(tr -d '[:space:]' < "$VOLUME_ROOT/current")"
  [ -n "$resolved" ] \
    || die "the platform volume's '$VOLUME_ROOT/current' is empty — $REFRESH_JOB writes the installed set's tester digest there LAST, so an empty file is an interrupted refresh, not an empty registry."
  echo "allow-unpinned: following '$VOLUME_ROOT/current' → $resolved (read once)"
fi

SET_DIR="$VOLUME_ROOT/${resolved/:/-}"
installed=""
for candidate in "$VOLUME_ROOT"/sha256-*; do
  [ -d "$candidate" ] && installed="$installed $(basename "$candidate")"
done
[ -n "$installed" ] || installed=" none"
[ -d "$SET_DIR" ] \
  || die "the platform volume at '$VOLUME_ROOT' carries no set for tester digest ${resolved}. $REFRESH_JOB installs the newest SEALED set every 10 minutes and keeps the 3 newest, so this pin is either older than those three or has never been sealed. Fix it by bumping the caller's image-digest/platform-image-digest to a set the refresh has installed (the volume holds:${installed}), or by forcing a refresh run. 🚨 This shard does NOT fall back to 'docker pull': a gate that quietly reached the registry would hide a dead refresh job for as long as the registry answers."
[ -f "$SET_DIR/.complete" ] \
  || die "'$SET_DIR' has no .complete marker — $REFRESH_JOB writes it LAST, so this directory is a half-install and its assemblies may be a mix of two sets. Wait for the next refresh run (≤ 10 min) or force one; do not gate on it."
marker="$(tr -d '[:space:]' < "$SET_DIR/.complete")"
[ "$marker" = "$resolved" ] \
  || die "'$SET_DIR/.complete' names '$marker' but this shard asked for '$resolved' — the directory and its marker disagree, which $REFRESH_JOB's write order (bytes, then .complete) makes impossible for a completed run. Treat the set as corrupt: delete its .complete so the next refresh rebuilds it in place."
[ -f "$SET_DIR/app/mw-plugin-test.dll" ] \
  || die "'$SET_DIR/app' has no mw-plugin-test.dll — that directory is meant to be the TESTER image's /app, and $REFRESH_JOB asserts the same file before it writes .complete. A set that passed that assertion and lacks it now has been damaged on the share."
[ -f "$SET_DIR/app/mw-plugin-test.runtimeconfig.json" ] \
  || die "'$SET_DIR/app' has no mw-plugin-test.runtimeconfig.json — the CLI cannot be started without it, and compose-gate-host.sh refuses a tester directory without it. See $REFRESH_JOB."
[ -s "$SET_DIR/platform-refs/meshweaver-surface.manifest" ] \
  || die "'$SET_DIR/platform-refs' has no meshweaver-surface.manifest — that directory is meant to be the PORTAL image's /app, and a host without a manifest resolves the fallback identity no bake may be keyed to. See $REFRESH_JOB."
[ -f "$SET_DIR/platform.json" ] \
  || die "'$SET_DIR' has no platform.json — $REFRESH_JOB writes it with the set name, the core sha and both image digests, and without it this shard cannot say WHICH platform it gated against. A run whose platform cannot be named is not a reproducible gate."

set_name="$(sed -n 's/.*"set"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$SET_DIR/platform.json" | head -1)"
core_sha="$(sed -n 's/.*"sha"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$SET_DIR/platform.json" | head -1)"
portal_on_volume="$(sed -n 's/.*"portal-image-digest"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$SET_DIR/platform.json" | head -1)"

# 🚨 The set is installed as a PAIR, so pinning the tester digest already picks the portal — but a
# caller pins both, and the two pins disagreeing is a caller error worth naming here rather than
# discovering as a framework-identity mismatch two steps later. When the volume's platform.json
# records no portal digest (a set laid down by the manual bootstrap rather than by the refresh),
# say so: the pairing then rests on the lane's own framework-identity assertion, which compares the
# two /app trees' assemblies and is STRICTLY STRONGER than this string compare. It always runs.
if [ -n "$PORTAL_DIGEST" ] && [ -n "$portal_on_volume" ] && [ "$PORTAL_DIGEST" != "$portal_on_volume" ]; then
  die "the set on the volume for tester ${resolved} was installed paired with portal ${portal_on_volume}, but this caller pinned platform-image-digest ${PORTAL_DIGEST}. Gating the caller's portal against another set's tester is the mixed pair this lane refuses by name. Pin BOTH digests from ONE CD wave; $REFRESH_JOB installs them as a pair, so the volume's pairing is the authoritative one."
fi
if [ -z "$portal_on_volume" ]; then
  echo "::notice::'$SET_DIR/platform.json' records no portal-image-digest (a set laid down before $REFRESH_JOB owned the install), so the caller's platform-image-digest could not be cross-checked here. The pairing is still asserted — by the framework-identity step below, which compares the two /app trees themselves."
fi

emit mode volume
emit dir "$SET_DIR"
emit tester_app "$SET_DIR/app"
emit portal_app "$SET_DIR/platform-refs"
emit digest "$resolved"
emit set "${set_name:-unknown}"
emit core_sha "${core_sha:-unknown}"
echo "platform from the volume: set ${set_name:-unknown} (core ${core_sha:-unknown}) at '$SET_DIR' — app/ $(find "$SET_DIR/app" -maxdepth 1 -name '*.dll' | wc -l | tr -d ' ') assemblies, platform-refs/ $(find "$SET_DIR/platform-refs" -maxdepth 1 -name '*.dll' | wc -l | tr -d ' ') assemblies. No registry, no daemon, no pull."
