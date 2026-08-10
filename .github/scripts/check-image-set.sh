#!/usr/bin/env bash
#
# Is the COMPLETE deployment image set present in ACR for one commit?
#
#   .github/scripts/check-image-set.sh <short-sha>
#   exit 0 = complete   exit 1 = something is missing / malformed
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
# 🚨 ADDING A SIXTH IMAGE touches THREE places, all in main-cd.yml plus this file:
#   1. its own build job (push ONLY the staging tag),
#   2. the `promote` job (identity tags in phase A, pointers in phase B),
#   3. the list below — otherwise nothing ever asserts it shipped.
#
# Runnable locally against the real registry, which is how it was verified:
#   az login && .github/scripts/check-image-set.sh 4f0c35c
#
# Why the short SHA and not the version tag: every leg pushes the commit's short SHA, so it is the
# one identity all five images share. The version tag (3.0.0-rc1.ci.<n>) is per-RUN, and
# memex-portal-next computes a DIFFERENT one (3.0.0-ci.<n>, no -rc1 — pre-existing drift), so it
# is not a cross-image identity.
set -uo pipefail

SHA="${1:?usage: check-image-set.sh <short-sha>}"
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

# The four .NET legs publish an OCI/Docker image INDEX over both linux architectures. Asserting the
# architectures — not just the tag — is what makes this more than a restatement of the job status:
# an index that lost a leg still resolves for one arch, and a swallowed cancellation in
# Microsoft.NET.Build.Containers is exactly how a leg goes missing while reporting success
# (issue #1026; the MW1026 guard in the root Directory.Build.props is the other half of that).
for repo in memex-portal-ai memex-migration memex-bake mw-plugin-test; do
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

# portal-next is amd64-only BY DESIGN (every AKS node is amd64) — existence is the check.
if ! az acr manifest show --registry "$REGISTRY" --name "memex-portal-next:$SHA" -o json >/dev/null 2>&1; then
  report "memex-portal-next:$SHA is MISSING from ACR — main $SHA has an INCOMPLETE image set"
else
  ok "memex-portal-next:$SHA (linux/amd64)"
fi

if [ "$fail" -ne 0 ]; then
  echo "::error::main $SHA does NOT have a complete image set. Every self-updating install stays on the previous image until a CD run publishes all of them."
  exit 1
fi
echo "All five images exist in ACR for $SHA."
