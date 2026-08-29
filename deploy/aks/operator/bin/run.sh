#!/usr/bin/env bash
# The operator Job's entrypoint: execute the plan the mesh composed, in order, and stop at the
# first failure.
#
# 🚨 THIS SCRIPT CONTAINS NO POLICY. Every ordering rule — backup before quiesce, verify before
# delete, manifest last — lives in the mesh's pure, unit-tested InstanceActionPlan. That is
# deliberate: the rules are then testable without a cluster, and there is exactly one place to read
# them. A step added here would be a second, untested planner.
#
# The contract (HostingOperator.JobManifest):
#   HOSTING_ACTION           provision | teardown | suspend | reactivate | restore | export | …
#   HOSTING_DEPLOYMENT       the deployment id, for logging
#   HOSTING_PLAN             base64 of `name<TAB>command` lines — base64 so no step's quoting can
#                            escape the environment value carrying it
#   HOSTING_CATALOG_CONFIG   base64 of the catalog config lines, materialised at $CATALOG_CONFIG
#   HOSTING_VALUES           base64 of the helm values RENDERED FROM THE DEPLOYMENT RECORD
#                            (HelmValues in the Hosting plugin), materialised at
#                            $HOSTING_VALUES_FILE — the ONE values file hosting-deploy consumes.
#   plus Hosting:Operator:Environment entries (AZ_RESOURCE_GROUP, AZ_PORTAL_IDENTITY, INGRESS_IP,
#   PAYWALL_URL, VALUES_FILE, CATALOG_CONFIG)
#
# 🚨 NOT `set -e`. A step's failure is handled explicitly below, because the exit code has to be
# reported with the STEP NAME attached — `set -e` would abort with a bare status and the mesh would
# record "failed" without saying what was inside.

set -uo pipefail

# shellcheck source=_common.sh
source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"
# shellcheck disable=SC2034  # read by hosting::die in _common.sh, which shellcheck does not follow here
HOSTING_CMD="run.sh"

action="${HOSTING_ACTION:-<unset>}"
deployment="${HOSTING_DEPLOYMENT:-<unset>}"

echo "hosting-operator: ${action} of ${deployment}"
hosting::dry && echo "  DRY RUN — every mutation is narrated, nothing is changed."

[ -n "${HOSTING_PLAN:-}" ] \
  || hosting::die "HOSTING_PLAN is empty. The mesh composes the plan and passes it base64-encoded; an empty one means the action reached the cluster with nothing to do, which is a bug in the caller — refusing rather than reporting a successful no-op."

plan="$(printf '%s' "$HOSTING_PLAN" | base64 -d)" \
  || hosting::die "HOSTING_PLAN is not valid base64"
[ -n "$plan" ] || hosting::die "HOSTING_PLAN decoded to nothing"

# Materialise the catalog config where the plan's --config-file points. A provision whose plugin
# mounts silently failed to arrive is the failure hosting-verify-catalog exists to catch; writing
# an empty file here would make that check pass against nothing.
if [ -n "${HOSTING_CATALOG_CONFIG:-}" ]; then
  target="${CATALOG_CONFIG:-/tmp/hosting-catalog-config}"
  if printf '%s' "$HOSTING_CATALOG_CONFIG" | base64 -d > "$target"; then
    hosting::log "catalog config → ${target} ($(wc -c < "$target") bytes)"
  else
    hosting::die "HOSTING_CATALOG_CONFIG is not valid base64"
  fi
fi

# The rendered values file. Same shape as the catalog config above: base64 in, a file out, and
# a decode failure is a refusal — a deploy step that found NO file would fall through to the
# chart defaults (ghcr :latest, an in-cluster Postgres this fleet does not run), which is the
# exact failure the record-driven render exists to end.
if [ -n "${HOSTING_VALUES:-}" ]; then
  HOSTING_VALUES_FILE="${HOSTING_VALUES_FILE:-/tmp/hosting-values.yaml}"
  export HOSTING_VALUES_FILE
  if printf '%s' "$HOSTING_VALUES" | base64 -d > "$HOSTING_VALUES_FILE"; then
    hosting::log "rendered values → ${HOSTING_VALUES_FILE} ($(wc -c < "$HOSTING_VALUES_FILE") bytes)"
  else
    hosting::die "HOSTING_VALUES is not valid base64"
  fi
fi

total=0
while IFS=$'\t' read -r name command; do
  [ -n "${name:-}" ] || continue
  total=$((total + 1))
done <<< "$plan"

[ "$total" -gt 0 ] || hosting::die "the decoded plan has no steps"
echo "  ${total} step(s)"
echo

index=0
while IFS=$'\t' read -r name command; do
  [ -n "${name:-}" ] || continue
  index=$((index + 1))

  if [ -z "${command:-}" ]; then
    hosting::die "step ${index} ('${name}') has no command — refusing to skip it silently"
  fi

  hosting::step "$name"
  echo "[${index}/${total}] ${name}"

  if hosting::dry; then
    echo "  DRY-RUN would run: ${command}"
    echo
    continue
  fi

  # `bash -c` because a step legitimately contains quotes, pipes and $VAR references that the mesh
  # wrote deliberately — re-splitting it into an argv here is exactly where an escaping bug becomes
  # an arbitrary-command bug, which is why the plan arrives as one opaque string per step.
  if bash -c "$command"; then
    echo
  else
    rc=$?
    echo
    hosting::die "step ${index}/${total} '${name}' failed with exit ${rc}. The run stops here: the plan is ordered so that everything destructive sits behind a verification, and continuing past a failure would step over one. Fix the cause and re-request the action — the plan is idempotent from the top."
  fi
done <<< "$plan"

echo "hosting-operator: ${action} of ${deployment} completed ${total}/${total} steps."
