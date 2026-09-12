#!/usr/bin/env bash
# Every KIND the chart can render must be writable by the operator's ClusterRole — helm applies
# the release under that identity, so a kind the role cannot create/patch/delete is an upgrade
# that fails inside helm and rolls back (or, worse, half-applies).
#
# 🚨 WHY THIS EXISTS. core #3774 (2026-09-09) made the chart render a PodDisruptionBudget for
# every two-replica instance. The ClusterRole granted poddisruptionbudgets get+list only. The next
# record-driven Reconcile of memex (Deployments/memex-reconcile-20260909-adopt) got through
# adoption and died in helm: "failed to create resource: poddisruptionbudgets.policy is forbidden",
# and helm's --atomic rollback erred too. Nothing paired the chart change with its grant; this
# check does. Every Provision of a two-replica instance would have hit the same wall.
#
# Excluded on purpose (listed here, refused by name at run time by hosting-deploy's preflight):
# ClusterRole and ClusterRoleBinding (instances-rbac.yaml, `instancesAdmin.clusterRead`) — a Job
# must never widen cluster-scoped RBAC. A record that enables that block is a design decision for
# a person, not a grant to add here.
#
# Pure bash 3.2 + grep/sed (no awk, no python — the operator image has neither).
set -u
HERE="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
CHART="${HOSTING_CHART_TEMPLATES:-$HERE/../../../helm/templates}"
# Inside the operator image the chart is baked at /opt/hosting/chart (COPY helm/ …); the repo
# layout is not there. Fall back to it so the in-image run checks the chart it will actually deploy.
[ -d "$CHART" ] || [ ! -d /opt/hosting/chart/templates ] || CHART=/opt/hosting/chart/templates
RBAC="${HOSTING_RBAC_MANIFEST:-$HERE/../../manifests/hosting-operator/operator-rbac.yaml}"
[ -d "$CHART" ] || { echo "check-chart-kinds-granted: ERROR: chart templates not found at ${CHART} — set HOSTING_CHART_TEMPLATES; an absent input is red, never a skip" >&2; exit 2; }
[ -f "$RBAC" ]  || { echo "check-chart-kinds-granted: ERROR: ClusterRole manifest not found at ${RBAC} — set HOSTING_RBAC_MANIFEST" >&2; exit 2; }

# kind → "<apiGroup> <plural>"; a kind missing here is RED so the map grows deliberately.
resource_of() {
  case "$1" in
    Service) echo " services" ;;                 ConfigMap) echo " configmaps" ;;
    Secret) echo " secrets" ;;                   ServiceAccount) echo " serviceaccounts" ;;
    PersistentVolumeClaim) echo " persistentvolumeclaims" ;;  Endpoints) echo " endpoints" ;;
    Namespace) echo " namespaces" ;;             Pod) echo " pods" ;;
    Deployment) echo "apps deployments" ;;       StatefulSet) echo "apps statefulsets" ;;
    DaemonSet) echo "apps daemonsets" ;;         Job) echo "batch jobs" ;;
    CronJob) echo "batch cronjobs" ;;            Ingress) echo "networking.k8s.io ingresses" ;;
    NetworkPolicy) echo "networking.k8s.io networkpolicies" ;;
    PodDisruptionBudget) echo "policy poddisruptionbudgets" ;;
    HorizontalPodAutoscaler) echo "autoscaling horizontalpodautoscalers" ;;
    ScaledObject) echo "keda.sh scaledobjects" ;;
    SecretProviderClass) echo "secrets-store.csi.x-k8s.io secretproviderclasses" ;;
    Role) echo "rbac.authorization.k8s.io roles" ;;  RoleBinding) echo "rbac.authorization.k8s.io rolebindings" ;;
    ClusterRole|ClusterRoleBinding) echo "EXCLUDED" ;;
    *) echo "" ;;
  esac
}

# The role's rules as "group resource verb" lines (same parser as check-rbac-coverage.sh).
list_of() { local s="$1"; s="${s#*[}"; s="${s%%]*}"; s="${s//\"/}"; s="${s//\'/}"; s="${s// /}"; printf '%s' "$s"; }
grants=""; rule_g=""; rule_r=""; rule_v=""
flush_rule() {
  [ -n "${rule_g}${rule_r}" ] || return 0
  local gs rs vs g r v
  IFS=, read -ra gs <<<"$rule_g"; IFS=, read -ra rs <<<"$rule_r"; IFS=, read -ra vs <<<"$rule_v"
  [ "${#gs[@]}" -gt 0 ] || gs=("")
  for g in "${gs[@]}"; do for r in "${rs[@]}"; do for v in "${vs[@]}"; do grants="${grants}${g:--} ${r} ${v}
"; done; done; done
}
while IFS= read -r line; do
  [[ "$line" =~ ^[[:space:]]*# ]] && continue
  [[ "$line" =~ ^kind:[[:space:]]*ClusterRoleBinding ]] && break
  if [[ "$line" =~ ^[[:space:]]*-[[:space:]]*apiGroups: ]]; then flush_rule; rule_g="$(list_of "$line")"; rule_r=""; rule_v=""
  elif [[ "$line" =~ ^[[:space:]]*resources: ]]; then rule_r="$(list_of "$line")"
  elif [[ "$line" =~ ^[[:space:]]*verbs: ]]; then rule_v="$(list_of "$line")"; flush_rule; rule_g=""; rule_r=""; rule_v=""
  fi
done < "$RBAC"
flush_rule
granted() { local g="${1:--}"; printf '%s\n' "$grants" | grep -qx -- "${g} ${2} ${3}" || printf '%s\n' "$grants" | grep -qx -- "${g} ${2} \*"; }

kinds="$(grep -rh '^kind:' "$CHART" | sed 's/^kind:[[:space:]]*//; s/"//g; s/[[:space:]]*$//' | sort -u)"
checked=0; missing=0; unknown=0; excluded=0
while IFS= read -r kind; do
  [ -n "$kind" ] || continue
  mapped="$(resource_of "$kind")"
  case "$mapped" in
    "") echo "  UNKNOWN  kind ${kind} — add it to resource_of() in $(basename "$0")"; unknown=$((unknown+1)); continue ;;
    EXCLUDED) excluded=$((excluded+1)); continue ;;
  esac
  checked=$((checked+1))
  group="${mapped%% *}"; plural="${mapped#* }"
  for verb in create patch delete; do
    granted "$group" "$plural" "$verb" || { echo "  MISSING  chart renders ${kind} but the ClusterRole lacks verbs:[\"${verb}\"] on apiGroups:[\"${group}\"] resources:[\"${plural}\"]"; missing=$((missing+1)); }
  done
done <<< "$kinds"
echo "check-chart-kinds-granted: $(printf '%s\n' "$kinds" | grep -c .) kind(s) rendered by the chart, ${checked} checked for create/patch/delete against $(basename "$RBAC"), ${excluded} excluded by design (cluster-scoped RBAC), ${missing} missing, ${unknown} unknown"
[ "$checked" -gt 0 ] || { echo "check-chart-kinds-granted: ERROR: found no kinds — the parser is broken, not the chart empty" >&2; exit 2; }
[ "$missing" -eq 0 ] && [ "$unknown" -eq 0 ]
