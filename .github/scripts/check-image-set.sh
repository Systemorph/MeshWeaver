#!/usr/bin/env bash
#
# Is the COMPLETE deployment image set present in ACR for one commit?
#
#   .github/scripts/check-image-set.sh <short-sha> [<plugins-short-sha>] [--pointers <version>]
#   exit 0 = complete   exit 1 = something is missing / malformed
#
# 🚨 THE SECOND ARGUMENT IS WHAT MAKES THE IDENTITY HONEST (MeshWeaver#2622). The portal HOSTS
# live in MeshWeaver.Plugins, so a merge THERE that edits a file shipping in the image — an
# appsettings.json, a csproj — changes what the image should contain while core's HEAD does not
# move. Keyed on core's sha alone this script answers "complete", the reconciler does nothing,
# and the fix has no producer that would ever rebuild it. That is not hypothetical: Plugins#814
# fixed fresh-install engine activation at 14:54Z and the newest image predated it.
#
# Given a plugins sha, "the set" additionally requires the PAIR tag memex-portal-ai:<sha>-p<psha>,
# which `promote` phase A stamps on the image it publishes. A plugins merge therefore makes the
# set INCOMPLETE on core's next reconcile tick, and the reconciler heals it on its own.
#
# 🚨 Callers MUST pass a value RESOLVED ONCE by `gate` and threaded through, never re-resolve it.
# `gate` and `verify-images` both call this file and their answers must agree (see below); if
# each resolved plugins HEAD itself, a plugins merge landing during the ~20 min run would make
# verify look for a tag the publish could not have written, and every such publish would go red
# on a correct result. Cry wolf, and the ledger becomes noise.
#
# 🚨 THIS FILE IS THE DEFINITION OF "THE SET". Used by BOTH jobs in main-cd.yml that need to
# answer the question, and they MUST agree:
#   * `gate` (reconcile path) asks it to decide whether main's HEAD needs healing at all — a
#     scheduled tick that finds the set complete does nothing and costs one 40 s job;
#   * `verify-images` asks it after a publish, as the assertion that the run actually shipped.
# If those two ever disagreed, the reconciler would either heal forever (it thinks something is
# missing that verify calls fine) or never heal a genuine hole. That is why the answer lives in
# ONE file instead of being duplicated in two `run:` blocks — same reasoning as shard-assign.sh.
#
# 🚨 ADDING AN IMAGE TO THE SET touches THREE places, all in main-cd.yml plus this file:
#   1. its own build job (push ONLY the staging tag),
#   2. the `promote` job (identity tags in phase A, pointers in phase B),
#   3. the `REPOS` list below — otherwise nothing ever asserts it shipped. That list IS the count;
#      it currently holds three repositories, and every assertion in this file iterates it.
#
# 🚨 ADDING A POINTER touches TWO: the `promote` phase that writes it, and the pointer list
# under `--pointers` below. A tag whose only producer is a lane nobody asserts is not a contract, it
# is a coincidence — which is exactly how MeshWeaver#3670 happened. `memex-portal-ai:latest` was
# written by `.github/workflows/release-images.yml`; that lane was DELETED in 28fc2da4b (2026-09-05)
# and its replacement retags `<version>` only, so the tag stopped being rewritten, aged past
# `--ago 7d --keep 10` and was purged by the `purge-old-images` ACR task on 2026-09-07T03:01:32Z.
# No check in this repository ever went red, at any point, because none had ever looked. See
# Doc/Architecture/ImageTagContract.
#
# 🚨 THE PORTAL HAS NO `latest`, DELIBERATELY — do not "symmetrise" one in. The contract is
# `main` (moving, rewritten by every CD run) plus `<version>` (immutable). A re-added floating tag on
# the portal would have no producer this file could name, no protection from retention
# (`lock-pinned-digests.py` refuses to lock a floating tag by design), and would age out again.
#
# 🚨 `--pointers <version>` IS OPT-IN, AND THE OPT-IN IS AN ORDERING FACT, NOT A PREFERENCE.
# `promote` writes tags in three phases: A = `<short-sha>` on all three plus the tester's and the
# migration's `<version>`; B = `main` on all three and `mw-plugin-test:latest`; C = the portal's
# `<version>`, LAST and alone, because that single PUT is what arms the self-updater. So the pointer
# set is only whole after phase C, and ONLY a caller that runs after `promote` may pass the flag:
#
#   * `verify-images` (`needs: promote`) passes it — it asserts what the run shipped.
#   * `release.yml` passes it — it promotes an already-promoted set, so phase C is long past.
#   * `gate`'s reconcile probe MUST NOT. It runs BEFORE promote, and this run's version does not
#     exist yet by construction. A version assertion there would answer "incomplete" on every tick
#     forever, and the reconciler would publish forever — the completeness probe must never fail on
#     a pointer a later phase legitimately has not written.
#
# Runnable locally against the real registry, which is how it was verified:
#   az login && .github/scripts/check-image-set.sh 4f0c35c
#   az login && .github/scripts/check-image-set.sh 67cbbe0 7c640de --pointers 3.0.0-ci.8079
#
# Why the short SHA and not the version tag: every leg pushes the commit's short SHA, so it is the
# one identity every image of the set shares. The version tag (3.0.0-ci.<n>) is per-RUN, so it is not a
# cross-image identity. (This note used to record a drift between legs computing 3.0.0-rc1.ci.<n>
# and memex-portal-next hand-writing 3.0.0-ci.<n>. Both halves are gone: the rc line is retired —
# every leg mints the clean shape, Doc/Architecture/ReleaseProcess §1 — and portal-next is no
# longer built here.)
set -uo pipefail

usage() {
  echo "usage: check-image-set.sh <short-sha> [<plugins-short-sha>] [--pointers <version>]" >&2
}

SHA=""
PLUGINS_SHA=""
POINTER_VERSION=""
CHECK_POINTERS=0
positional=0
while [ $# -gt 0 ]; do
  case "$1" in
    --pointers)
      CHECK_POINTERS=1
      # Deliberately tolerant of an EMPTY value here: an unresolved version is reported below,
      # AFTER the per-sha diagnostics, so a run whose image leg died still gets "<repo>:<sha> is
      # MISSING" as its headline instead of a usage message about a flag.
      if [ $# -ge 2 ]; then POINTER_VERSION="$2"; shift 2; else shift; fi
      ;;
    --*) echo "::error::check-image-set.sh: unknown option '$1'"; usage; exit 1 ;;
    *)
      positional=$((positional + 1))
      case "$positional" in
        1) SHA="$1" ;;
        2) PLUGINS_SHA="$1" ;;
        *) echo "::error::check-image-set.sh: unexpected argument '$1'"; usage; exit 1 ;;
      esac
      shift
      ;;
  esac
done
if [ -z "$SHA" ]; then
  echo "::error::check-image-set.sh needs the commit's short sha — the one identity every image of the set shares."
  usage
  exit 1
fi
REGISTRY="${ACR_NAME:-meshweaver}"

# Reads manifests through ARM (an `az login` is enough — `az acr login` is for docker push creds).
# `az acr manifest show` is an Azure-CLI PREVIEW command group (it prints a warning on stderr,
# discarded here). If it is ever withdrawn, the equivalent is
# `docker buildx imagetools inspect --raw <acr>/<repo>:<tag>` after `az acr login`.
fail=0
summary() { [ -n "${GITHUB_STEP_SUMMARY:-}" ] && echo "$1" >> "$GITHUB_STEP_SUMMARY"; return 0; }
report()  { echo "::error::$1"; summary "- ❌ $1"; fail=1; }
ok()      { echo "$1";          summary "- ✅ $1"; }

summary "### Images for main \`$SHA\`"

# THE REPOSITORIES OF THE SET. Every assertion below iterates THIS list — a repository added here
# is asserted for its sha tag and for every set-wide pointer, in one edit.
REPOS="memex-portal-ai memex-migration mw-plugin-test"

# The three multi-arch .NET legs publish an OCI/Docker image INDEX over both linux architectures.
# Asserting the architectures — not just the tag — is what makes this more than a restatement of
# the job status:
# an index that lost a leg still resolves for one arch, and a swallowed cancellation in
# Microsoft.NET.Build.Containers is exactly how a leg goes missing while reporting success
# (issue #1026; the MW1026 guard in the root Directory.Build.props is the other half of that).
#
# ONE definition of "a good image", reused by the sha check and by the pointer checks — a pointer
# that resolves to a single-arch manifest is as broken as one that does not resolve at all, and
# nothing would be gained by letting the two answers drift apart.
#   assert_index <repo> <tag> <what-a-miss-means>
assert_index() {
  local repo="$1" tag="$2" consequence="$3" m arches
  if ! m=$(az acr manifest show --registry "$REGISTRY" --name "$repo:$tag" -o json 2>/dev/null); then
    report "$repo:$tag is MISSING from ACR — $consequence"
    return 1
  fi
  arches=$(printf '%s' "$m" | jq -r '[(.manifests // [])[] | select(.platform.os == "linux") | .platform.architecture] | sort | join(",")')
  if [ "$arches" != "amd64,arm64" ]; then
    report "$repo:$tag is not a linux amd64+arm64 image index (architectures: '${arches:-<single-arch manifest>}')"
    return 1
  fi
  ok "$repo:$tag (linux/amd64 + linux/arm64)"
  return 0
}

for repo in $REPOS; do
  assert_index "$repo" "$SHA" "main $SHA has an INCOMPLETE image set" || true
done

# 🚨 memex-portal-next is NOT checked here any more: its sources and its build lane moved to
# MeshWeaver.Plugins (MeshWeaver#2169). It is publishable independently because portalNext is
# opt-in (chart default enabled: false) and the self-updater never rolls it — nothing waits on
# it, so it is not part of THIS repo's all-or-nothing set. Asserting an image this repo does not
# build would fail every commit.

# 🚨 The PAIR tag — is the published portal image the one built from the CURRENT plugins HEAD?
# Only checked when a caller supplies the plugins sha, so every other caller keeps today's exact
# behaviour and nothing else has to change. Absent argument = absent check, deliberately: this is
# the one place the answer may narrow, and it narrows only for callers that opted in.
if [ -n "$PLUGINS_SHA" ]; then
  pair="$SHA-p$PLUGINS_SHA"
  if az acr manifest show --registry "$REGISTRY" --name "memex-portal-ai:$pair" -o json >/dev/null 2>&1; then
    ok "memex-portal-ai:$pair — built from plugins $PLUGINS_SHA"
  else
    report "memex-portal-ai:$pair is MISSING — the published portal image was NOT built from the current plugins HEAD ($PLUGINS_SHA). The portal hosts live in MeshWeaver.Plugins, so a merge there ships in the image while core's sha does not move (#2622). The reconciler will rebuild."
  fi
fi

# ── THE POINTERS: every named tag the promotion publishes, on every repository it publishes it to ──
#
# 🚨 MeshWeaver#3670. Everything above is keyed on the commit's short sha, so it says NOTHING about
# the tags consumers actually name. `memex-portal-ai:latest` stopped resolving in ACR and every check
# in this repository stayed green, because no check had ever looked. A tag with no producer is not a
# contract; before relying on one, ask what rewrites it and what deletes it.
#
# What the promotion publishes, and therefore what is asserted here:
#   * `main`      — SET-WIDE. Phase B writes it on all three, so a promoted set where only one half
#                   carries it is what this makes unrepresentable.
#   * `<version>` — SET-WIDE. Phases A (migration, tester) and C (portal) write it; all three legs
#                   compute $(Version) from the same root props under the same GITHUB_RUN_NUMBER, so
#                   the three values are equal BY CONSTRUCTION and one argument covers the set.
#   * `latest`    — REPO-SCOPED to `mw-plugin-test`, which phase B writes alongside `main`. It is
#                   what the satellites resolve as `vars.MW_TEST_IMAGE`, so it is a real consumer
#                   contract — but it is the TESTER's, not the set's. The portal deliberately has no
#                   `latest`; see the header. Do not generalise this line into the loop.
#
# No `if:` on a variable and no tolerated failure anywhere in here: a pointer that cannot be checked
# is reported RED naming what is missing, exactly like one that is missing. A gate that can decline
# to run is indistinguishable from one that passed.
if [ "$CHECK_POINTERS" -eq 1 ]; then
  summary "### Pointers"
  for repo in $REPOS; do
    assert_index "$repo" main \
      "the promotion's moving pointer is not on every repository of the set. Phase B writes \`main\` on all three; a set where only one half carries it is exactly MeshWeaver#3670 (Doc/Architecture/ImageTagContract)." || true
  done
  assert_index mw-plugin-test latest \
    "the satellites resolve this tag as \`vars.MW_TEST_IMAGE\`, so their CI cannot pull a tester. Phase B writes it beside \`mw-plugin-test:main\`." || true
  if [ -z "$POINTER_VERSION" ]; then
    report "--pointers was given an EMPTY version, so the set's immutable pointer could not be asserted. The caller must pass the version this run promoted (every leg's \$(Version), e.g. 3.0.0-ci.8079). An empty value means the leg that computes it never ran — the set is not promoted."
  else
    for repo in $REPOS; do
      assert_index "$repo" "$POINTER_VERSION" \
        "the promoted set is ASYMMETRIC: $POINTER_VERSION does not resolve on every repository. The portal's is phase C, the arming write SelfUpdateHostedService acts on; the other two are phase A." || true
    done
  fi
fi

if [ "$fail" -ne 0 ]; then
  echo "::error::main $SHA does NOT have a complete image set — see the errors above: an image is missing or malformed, or a pointer the promotion publishes does not resolve on every repository of the set. Every self-updating install stays on the previous image until a CD run publishes all of them, and a consumer naming a missing pointer cannot resolve anything at all."
  exit 1
fi
echo "All images exist in ACR for $SHA${PLUGINS_SHA:+ (built from plugins $PLUGINS_SHA)}${POINTER_VERSION:+, and every promoted pointer resolves ($POINTER_VERSION)}."
