#!/usr/bin/env bash
# The operator Job's entrypoint: execute the plan the mesh composed, in order, and stop at the
# first failure.
#
# 🚨 THIS SCRIPT CONTAINS NO POLICY. Every ordering rule — backup before quiesce, verify before
# delete, manifest last — lives in the mesh's pure, unit-tested InstanceActionPlan. That is
# deliberate: the rules are then testable without a cluster, and there is exactly one place to read
# them. A step added here would be a second, untested planner.
#
# 🚨 ONE INTERLOCK, and it is not a planner: policy `roll-migrates-first` (Doc/Architecture/
# PlanningADatabaseMigration). A step that moves the portal image (`set image … memex-portal=<ref>`)
# never runs unless the SAME plan ran `hosting-migrate` for that namespace and tag before it — and if
# it did not, this script runs `hosting-migrate --namespace <ns> --tag <tag>` itself, and a failure
# there stops the run before the image moves. Why here and not only in the planner: the plan is
# composed by the Hosting code the PORTAL runs, and a portal whose Hosting generation predates the
# migrate-first plan (Plugins #2219) planned image-only rolls of ITSELF — memex, 2026-09-24/25:
# 2-step `set image` plans to ci.9317/9321/9332 past DbVersion 58, a pod crash-looping 91 times on
# DbVersionGate, and the fix unable to arrive because it would have had to arrive through that roll.
# This image (`hosting-operator:main`, pulled Always) is the one piece of the roll path that does not
# depend on the build being rolled — so the rule the control plane cannot be trusted to carry lives
# here too. A plan that already migrates first is untouched (the interlock sees its step).
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

# ── Azure sign-in through Workload Identity ──────────────────────────────────────────────────
# The Job runs as the hosting-operator ServiceAccount, federated to the operator's managed identity
# (backups.bicep); the workload-identity webhook projects a token into the pod and sets
# AZURE_CLIENT_ID / AZURE_TENANT_ID / AZURE_FEDERATED_TOKEN_FILE. 🚨 az DOES NOT READ THOSE ON ITS
# OWN — the Azure SDKs do (WorkloadIdentityCredential), the CLI does not. Measured 2026-09-09 01:33Z
# on the first Provision ever run through the lane (Deployments/pearl-provision-20260909): step 1/14
# `az postgres flexible-server db create` answered "ERROR: Please run 'az login' to setup account."
# Every earlier run (memex Reconcile/Restart) was kubectl+helm only, which authenticate in-cluster,
# so no run had reached an az step before. Sign in ONCE here, as the identity, and say so.
#
# The token never reaches a command line or the log: it is read from the file straight into az's
# argument by this process, and az is not wrapped in hosting::do (which narrates argv).
if [ -n "${AZURE_FEDERATED_TOKEN_FILE:-}" ]; then
  [ -r "$AZURE_FEDERATED_TOKEN_FILE" ] \
    || hosting::die "AZURE_FEDERATED_TOKEN_FILE=${AZURE_FEDERATED_TOKEN_FILE} is not readable — the workload-identity webhook set the variable but the projected token is missing"
  hosting::need_env AZURE_CLIENT_ID "the operator identity's client id (Hosting:Operator:Environment or the record's operator.environment; the webhook also sets it)"
  hosting::need_env AZURE_TENANT_ID "set by the workload-identity webhook from the ServiceAccount's azure.workload.identity/tenant-id annotation or the webhook default"
  if az login --service-principal --username "$AZURE_CLIENT_ID" --tenant "$AZURE_TENANT_ID" \
       --federated-token "$(cat "$AZURE_FEDERATED_TOKEN_FILE")" --allow-no-subscriptions --output none 2>/tmp/az-login.err; then
    az_sub="$(az account show --query id -o tsv 2>/dev/null || true)"
    hosting::log "azure     signed in as ${AZURE_CLIENT_ID} (subscription ${az_sub:-none})"
    hosting::say az_login true
  else
    hosting::die "az login as workload identity ${AZURE_CLIENT_ID} failed: $(tr -d '\n' < /tmp/az-login.err). Check the federated credential on the operator identity (subject system:serviceaccount:memex-ops:hosting-operator, issuer AZ_OIDC_ISSUER) — a subject/issuer mismatch fails here and nowhere else."
  fi
else
  # Not a refusal: a Reconcile/Roll/Restart is kubectl+helm only and needs no Azure session. A step
  # that DOES need az then fails by name — and this line, above it in the log, says what was missing.
  hosting::log "azure     no workload-identity token (AZURE_FEDERATED_TOKEN_FILE unset) — steps that call az will fail. The Job needs the pod label azure.workload.identity/use=true and the hosting-operator ServiceAccount annotated azure.workload.identity/client-id (manifests/hosting-operator/operator-serviceaccount.yaml)."
  hosting::say az_login false
fi

total=0
while IFS=$'\t' read -r name command; do
  [ -n "${name:-}" ] || continue
  total=$((total + 1))
done <<< "$plan"

[ "$total" -gt 0 ] || hosting::die "the decoded plan has no steps"

# ── the migrate-first interlock (policy roll-migrates-first; see the header) ─────────────────────
# Recognise a step that moves the PORTAL image and pull out its namespace and target tag. Anything
# that names deployment/memex-portal-deployment and `memex-portal=` in a `set image` is one; a step
# that does but whose namespace or tag cannot be read is REFUSED — an image move the interlock
# cannot place is exactly the move it exists to stop.
portal_image_move() { # <command> → sets pim_ns pim_tag; returns 0 when the command moves the portal image
  local cmd="$1" ref
  pim_ns="" pim_tag=""
  [[ "$cmd" == *"set image"* && "$cmd" == *"memex-portal-deployment"* && "$cmd" == *"memex-portal="* ]] || return 1
  if [[ "$cmd" =~ (^|[[:space:]])(-n|--namespace)[[:space:]=]+([A-Za-z0-9][A-Za-z0-9-]*) ]]; then pim_ns="${BASH_REMATCH[3]}"; fi
  if [[ "$cmd" =~ memex-portal=([^[:space:]\"\']+) ]]; then
    ref="${BASH_REMATCH[1]}"; pim_tag="${ref##*:}"
    { [ "$pim_tag" != "$ref" ] && [[ "$pim_tag" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]]; } || pim_tag=""
  fi
  return 0
}
migrates() { # <command> <ns> <tag> → 0 when the command runs hosting-migrate for exactly that ns and tag
  local cmd="$1" ns="$2" tag="$3"
  [[ "$cmd" == *"hosting-migrate"* ]] || return 1
  [[ "$cmd" =~ --namespace[[:space:]=]+([A-Za-z0-9-]+) && "${BASH_REMATCH[1]}" == "$ns" ]] || return 1
  [[ "$cmd" =~ memex-migration:([A-Za-z0-9._-]+) && "${BASH_REMATCH[1]}" == "$tag" ]] && return 0
  [[ "$cmd" =~ --tag[[:space:]=]+([A-Za-z0-9._-]+) && "${BASH_REMATCH[1]}" == "$tag" ]]
}
interlocked=""   # "<position>:<ns>:<tag>" per guarded step, space-separated
seen_cmds=()
scan=0
while IFS=$'\t' read -r name command; do
  [ -n "${name:-}" ] || continue
  scan=$((scan + 1))
  if portal_image_move "${command:-}"; then
    [ -n "$pim_ns" ] && [ -n "$pim_tag" ] \
      || hosting::die "step ${scan} ('${name}') moves the portal image, but its namespace or target tag cannot be read (${command}) — the migrate-first interlock cannot place it, so it refuses the plan (policy roll-migrates-first). Nothing ran."
    covered=false
    for prior in "${seen_cmds[@]+"${seen_cmds[@]}"}"; do
      if migrates "$prior" "$pim_ns" "$pim_tag"; then covered=true; break; fi
    done
    if ! $covered; then
      interlocked="${interlocked} ${scan}:${pim_ns}:${pim_tag}"
      total=$((total + 1))
      hosting::log "interlock step ${scan} ('${name}') moves the portal image of ${pim_ns} to ${pim_tag} and the plan runs no migration for it first — the operator runs hosting-migrate before it (policy roll-migrates-first)"
    fi
  fi
  seen_cmds+=("${command:-}")
done <<< "$plan"
[ -z "$interlocked" ] || hosting::say migrate_interlock "${interlocked# }"

echo "  ${total} step(s)"
echo

index=0
position=0
while IFS=$'\t' read -r name command; do
  [ -n "${name:-}" ] || continue
  position=$((position + 1))

  # The interlock's synthesised step, immediately before the image move it guards.
  for entry in $interlocked; do
    [ "${entry%%:*}" = "$position" ] || continue
    rest="${entry#*:}"; ilk_ns="${rest%%:*}"; ilk_tag="${rest#*:}"
    index=$((index + 1))
    ilk_name="Run the database migration first (operator interlock)"
    ilk_cmd="hosting-migrate --namespace ${ilk_ns} --tag ${ilk_tag}"
    hosting::step "$ilk_name"
    echo "[${index}/${total}] ${ilk_name}"
    if hosting::dry; then
      echo "  DRY-RUN would run: ${ilk_cmd}"
      echo
      continue
    fi
    if bash -c "$ilk_cmd"; then
      echo
    else
      rc=$?
      echo
      hosting::die "step ${index}/${total} '${ilk_name}' failed with exit ${rc}. The next step would have moved the portal image of ${ilk_ns} to ${ilk_tag} without its database migration — REFUSED (policy roll-migrates-first): the run stops before the image moves. Read the migration Job's log; re-request the action once it can succeed."
    fi
  done

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
