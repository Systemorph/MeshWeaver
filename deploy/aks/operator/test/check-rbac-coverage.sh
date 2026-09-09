#!/usr/bin/env bash
# Every kubectl verb + resource an operator script names must be GRANTED by the operator's
# ClusterRole — or this check is red, naming the script, the verb and the resource.
#
# 🚨 WHY THIS EXISTS. The ClusterRole (deploy/aks/manifests/hosting-operator/operator-rbac.yaml)
# and the scripts that need its grants live three directories apart and are reviewed separately.
# Twice a script reached main without its grant: hosting-pv-resize (`get storageclasses`) failed
# the first record-driven Reconcile of memex through the fixed operator on 2026-09-09 at step 1/6
# with `Forbidden`, and hosting-pv-purge (`list/delete persistentvolumes`) sat on main with no
# grant at all, so no Teardown could have completed. Care did not catch either; a control does.
#
# What it reads: every `kubectl [-n <ns>] <verb> <resource>` in bin/* whose resource is a literal
# (an alias such as `pv`, `pvc`, `namespace`, `storageclass` — or `kind/name`). What it cannot
# read, stated so nobody takes green for more: `kubectl apply -f` (the kinds are inside the
# manifest), `kubectl get "$res"` (dynamic), and everything helm does with the chart. Those stay
# covered by the chart-owner grants in the ClusterRole's own scope notes.
#
# Pure bash 3.2 + grep/sed/tr (the tools bin/ itself uses): it runs on the macOS laptop, the
# ubuntu runner and inside the operator image alike. 🚨 NOT awk and NOT python — the image
# (Azure Linux 3) has neither; the first cut used awk, passed on the laptop and the runner, and
# the in-image run answered "awk: command not found" (run 34295483037). The image run is the one
# that proves the check can run where the scripts run.
set -u
HERE="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
BIN="${HOSTING_BIN:-$HERE/../bin}"
RBAC="${HOSTING_RBAC_MANIFEST:-$HERE/../../manifests/hosting-operator/operator-rbac.yaml}"

if [ ! -f "$RBAC" ]; then
  echo "check-rbac-coverage: ERROR: ClusterRole manifest not found at ${RBAC} — set HOSTING_RBAC_MANIFEST (in the image run, mount deploy/aks/manifests). A check whose input is absent is red, never skipped." >&2
  exit 2
fi

# Alias → "<apiGroup> <plural>". An alias missing here is RED — the map grows deliberately.
resource_of() {
  case "$1" in
    namespace|namespaces|ns)                 echo " namespaces" ;;
    secret|secrets)                          echo " secrets" ;;
    configmap|configmaps|cm)                 echo " configmaps" ;;
    service|services|svc)                    echo " services" ;;
    serviceaccount|serviceaccounts|sa)       echo " serviceaccounts" ;;
    pvc|persistentvolumeclaim|persistentvolumeclaims) echo " persistentvolumeclaims" ;;
    pv|persistentvolume|persistentvolumes)   echo " persistentvolumes" ;;
    pod|pods|po)                             echo " pods" ;;
    event|events|ev)                         echo " events" ;;
    storageclass|storageclasses|sc)          echo "storage.k8s.io storageclasses" ;;
    deployment|deployments|deploy)           echo "apps deployments" ;;
    statefulset|statefulsets|sts)            echo "apps statefulsets" ;;
    replicaset|replicasets|rs)               echo "apps replicasets" ;;
    ingress|ingresses|ing)                   echo "networking.k8s.io ingresses" ;;
    job|jobs)                                echo "batch jobs" ;;
    cronjob|cronjobs|cj)                     echo "batch cronjobs" ;;
    secretproviderclass|secretproviderclasses|spc) echo "secrets-store.csi.x-k8s.io secretproviderclasses" ;;
    *) echo "" ;;
  esac
}

# The ClusterRole's rules as "group resource verb" lines (one per combination). Comments are
# dropped; a rule is the three bracketed lists that follow an `- apiGroups:` line; the core API
# group (`[""]`) is recorded as "-" so a grep can anchor on it.
list_of() { # the comma-joined items of a `key: ["a", "b"]` line
  local s="$1"; s="${s#*[}"; s="${s%%]*}"; s="${s//\"/}"; s="${s//\'/}"; s="${s// /}"; printf '%s' "$s"
}
grants=""
flush_rule() {
  [ -n "${rule_g}${rule_r}" ] || return 0
  local gs rs vs g r v
  IFS=, read -ra gs <<<"$rule_g"; IFS=, read -ra rs <<<"$rule_r"; IFS=, read -ra vs <<<"$rule_v"
  [ "${#gs[@]}" -gt 0 ] || gs=("")      # apiGroups: [""] is the core group, not "no rule"
  for g in "${gs[@]}"; do for r in "${rs[@]}"; do for v in "${vs[@]}"; do
    grants="${grants}${g:--} ${r} ${v}
"
  done; done; done
}
rule_g=""; rule_r=""; rule_v=""
while IFS= read -r line; do
  [[ "$line" =~ ^[[:space:]]*# ]] && continue
  [[ "$line" =~ ^kind:[[:space:]]*ClusterRoleBinding ]] && break
  if [[ "$line" =~ ^[[:space:]]*-[[:space:]]*apiGroups: ]]; then
    flush_rule; rule_g="$(list_of "$line")"; rule_r=""; rule_v=""
  elif [[ "$line" =~ ^[[:space:]]*resources: ]]; then
    rule_r="$(list_of "$line")"
  elif [[ "$line" =~ ^[[:space:]]*verbs: ]]; then
    rule_v="$(list_of "$line")"; flush_rule; rule_g=""; rule_r=""; rule_v=""
  fi
done < "$RBAC"
flush_rule

granted() { # group resource verb
  local g="${1:--}"
  printf '%s\n' "$grants" | grep -qx -- "${g} ${2} ${3}" && return 0
  printf '%s\n' "$grants" | grep -qx -- "${g} ${2} \*" && return 0
  return 1
}

calls=0; distinct=""; reported=""; missing=0; unknown=0
for f in "$BIN"/hosting-* "$BIN"/run.sh; do
  [ -f "$f" ] || continue
  script="$(basename "$f")"
  # verb + first non-flag token after it; `kind/name` keeps only the kind; `rollout status X`
  # is a get on X; `logs` is get on pods/log.
  while read -r verb res _; do
    [ -n "$verb" ] || continue
    case "$res" in -*|'"$'*|'$'*|*'<'*|"") continue ;; esac
    res="${res%%/*}"
    res="$(printf '%s' "$res" | tr -cd 'A-Za-z0-9')"   # a prose mention ends in ` or '. — the word is what matters
    [ -n "$res" ] || continue
    case "$verb" in
      apply) continue ;;                          # kinds live in the manifest, not on the line
      rollout) verb="get" ;;
      logs) verb="get"; res="pods/log" ;;
      annotate|label) verb="patch" ;;
    esac
    calls=$((calls+1))
    if [ "$res" = "pods/log" ]; then mapped=" pods/log"; else mapped="$(resource_of "$res")"; fi
    if [ -z "$mapped" ]; then
      echo "  UNKNOWN  ${script}: kubectl ${verb} ${res} — add the alias to resource_of() in $(basename "$0")"
      unknown=$((unknown+1)); continue
    fi
    group="${mapped%% *}"; plural="${mapped#* }"
    key="${group:--} ${plural} ${verb}"
    case "$distinct" in *"|${key}|"*) ;; *) distinct="${distinct}|${key}|" ;; esac
    case "$reported" in *"|${script} ${key}|"*) continue ;; esac
    reported="${reported}|${script} ${key}|"
    if ! granted "$group" "$plural" "$verb"; then
      echo "  MISSING  ${script}: kubectl ${verb} ${res} needs ClusterRole rule apiGroups:[\"${group}\"] resources:[\"${plural}\"] verbs:[\"${verb}\"]"
      missing=$((missing+1))
    fi
  done < <(grep -o 'kubectl \(-n [^ ]* \)\?\(get\|create\|delete\|patch\|apply\|label\|annotate\|scale\|logs\|rollout\) [^ ;|)]*\( [^ ;|)]*\)\?' "$f" \
             | sed 's/^kubectl //; s/^-n [^ ]* //; s/^rollout [^ ]* /rollout /')
done

n_distinct="$(printf '%s' "$distinct" | tr '|' '\n' | grep -c .)"
echo "check-rbac-coverage: ${calls} kubectl call(s) with a literal resource across bin/, ${n_distinct} distinct grant(s) checked against $(basename "$RBAC"), ${missing} missing, ${unknown} unknown alias(es)"
# A zero denominator is a broken parser, not a clean fleet.
[ "$calls" -gt 0 ] || { echo "check-rbac-coverage: ERROR: found no kubectl calls at all — the parser is broken, not the scripts clean" >&2; exit 2; }
[ "$missing" -eq 0 ] && [ "$unknown" -eq 0 ]
