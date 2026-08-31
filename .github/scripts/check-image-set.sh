#!/usr/bin/env bash
#
# Is the COMPLETE deployment image set present in ACR for one commit?
#
#   .github/scripts/check-image-set.sh <short-sha> [<plugins-short-sha>]
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
# 🚨 ADDING A FIFTH IMAGE touches THREE places, all in main-cd.yml plus this file:
#   1. its own build job (push ONLY the staging tag),
#   2. the `promote` job (identity tags in phase A, pointers in phase B),
#   3. the list below — otherwise nothing ever asserts it shipped.
#
# Runnable locally against the real registry, which is how it was verified:
#   az login && .github/scripts/check-image-set.sh 4f0c35c
#
# Why the short SHA and not the version tag: every leg pushes the commit's short SHA, so it is the
# one identity all four images share. The version tag (3.0.0-rc1.ci.<n>) is per-RUN, and
# memex-portal-next computes a DIFFERENT one (3.0.0-ci.<n>, no -rc1 — pre-existing drift), so it
# is not a cross-image identity.
set -uo pipefail

SHA="${1:?usage: check-image-set.sh <short-sha> [<plugins-short-sha>]}"
PLUGINS_SHA="${2:-}"
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

# The three multi-arch .NET legs publish an OCI/Docker image INDEX over both linux architectures.
# Asserting the architectures — not just the tag — is what makes this more than a restatement of
# the job status:
# an index that lost a leg still resolves for one arch, and a swallowed cancellation in
# Microsoft.NET.Build.Containers is exactly how a leg goes missing while reporting success
# (issue #1026; the MW1026 guard in the root Directory.Build.props is the other half of that).
for repo in memex-portal-ai memex-migration mw-plugin-test; do
  if ! m=$(az acr manifest show --registry "$REGISTRY" --name "$repo:$SHA" -o json 2>/dev/null); then
    report "$repo:$SHA is MISSING from ACR — main $SHA has an INCOMPLETE image set"
    continue
  fi
  arches=$(printf '%s' "$m" | jq -r '[(.manifests // [])[] | select(.platform.os == "linux") | .platform.architecture] | sort | join(",")')
  if [ "$arches" != "amd64,arm64" ]; then
    report "$repo:$SHA is not a linux amd64+arm64 image index (architectures: '${arches:-<single-arch manifest>}')"
  else
    ok "$repo:$SHA (linux/amd64 + linux/arm64)"
  fi
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

if [ "$fail" -ne 0 ]; then
  echo "::error::main $SHA does NOT have a complete image set. Every self-updating install stays on the previous image until a CD run publishes all of them."
  exit 1
fi
echo "All images exist in ACR for $SHA${PLUGINS_SHA:+ (built from plugins $PLUGINS_SHA)}."
