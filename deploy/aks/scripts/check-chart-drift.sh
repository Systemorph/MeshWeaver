#!/usr/bin/env bash
#
# Does the cluster actually run what the chart describes?
#
#   deploy/aks/scripts/check-chart-drift.sh -n <namespace> -r <release> [-f values.yaml]...
#   exit 0 = the rendered chart and the live objects agree   exit 1 = drift, or the check
#                                                                     could not be made
#
# 🚨 WHY THIS EXISTS. Four divergences between the chart and the cluster surfaced on one day
# (2026-08-12), each invisible until something forced a comparison, and NOTHING detected any of
# them:
#
#   1. the drain preStop hook lived in the chart and was NEVER APPLIED to the cluster — every
#      roll severed live Blazor circuits, and a user lost a session mid-task;
#   2. a `wget` probe in the chart was CONTRADICTED BY THE IMAGE (curl present, wget absent) —
#      had it ever been applied, every pod termination would have hung the full 1800 s grace;
#   3. the GitHub App identity was hand-applied with `kubectl set env` and was NEVER IN THE
#      CHART — so one namespace never got it, and `grep -c 'GitHub__App__'` on main was 0;
#   4. that namespace's PluginCatalog__* were inline `env:` on the Deployment, NOT in memex-portal-config
#      — so the chart cannot express them, and (MEASURED, see below) a `helm upgrade` does not
#      remove them either: they outlive every deploy while SHADOWING the ConfigMap key of the
#      same name, which is how a chart value can be dead and still look authoritative.
#
# They are one defect: nothing verifies that what we run is what we described. This script is
# that verification. It is deliberately a SCRIPT and not a CI job — it needs cluster credentials,
# and a CI gate that skips when credentials are absent renders the same grey tick as one that
# passed (AGENTS.md → "A gate NEVER tests its own inputs"). A green-by-default job would BE the
# defect, one level up.
#
# WHAT IT COMPARES (portal only — the object a code update actually rolls):
#   ConfigMap  memex-portal-config       every key and value
#   Deployment memex-portal-deployment   the portal container's inline env (NAMES only — values
#                                        are never printed, they hold tokens), envFrom refs,
#                                        lifecycle.preStop, terminationGracePeriodSeconds, the
#                                        three probes, and the AVAILABILITY shape below
#   PodDisruptionBudget / ScaledObject   existence and shape
#
# 🚨 THE AVAILABILITY SHAPE was added 2026-08-14, because the check as first written would have
# MISSED the incident it was created in response to. On that day production served every request
# from ONE pod, and all three of the reasons were outside what this script looked at:
#
#   * spec.replicas               live 1; the chart hard-coded 1 too, so even a comparison agreed —
#                                 which is why the CHART side of it is now gated separately by
#                                 check-chart-invariants.sh, on every pull request.
#   * PodDisruptionBudget         live `minAvailable: 2` over ONE healthy pod ⇒ disruptionsAllowed
#                                 0 for ever. Hand-applied 2026-07-26 (kubectl-client-side-apply);
#                                 the chart renders `maxUnavailable: 1`, a different object shape
#                                 entirely. Nothing compared them.
#   * ScaledObject                live, minReplicaCount 2 — and carrying the annotation
#                                 `autoscaling.keda.sh/paused-replicas: "1"`, which PINS the
#                                 deployment at one pod and deletes the HPA. The chart has never
#                                 heard of that annotation. It is invisible in `kubectl get deploy`
#                                 and it silently reverts `kubectl scale`.
#
# A drift checker that reads only the ConfigMap and the pod template is a drift checker that cannot
# see availability. It reads all three now, and it reports a live `disruptionsAllowed: 0` as a
# finding in its own right — that is an outage condition whether or not the chart agrees with it.
#
# Inline `env` and the ConfigMap are BOTH needed and neither is redundant: an inline env entry
# OVERRIDES envFrom, so `kubectl set env` (defect 3) leaves the ConfigMap matching the render
# perfectly while the pod runs a value the chart has never heard of.
#
# 🚨 WHAT A `helm upgrade` ACTUALLY DOES TO DRIFT — MEASURED, not assumed (2026-09-03, helm
# v3.21.1, the exact binary `az aks command invoke` runs; identical result on helm v4.2.4).
# A throwaway release was installed, drifted four ways (a live-only ConfigMap key, a live-only
# inline env, an inline env duplicating a chart ConfigMap key, and an `initialDelaySeconds`
# added to a live probe), then upgraded with a chart that genuinely changed — a new image, a new
# ConfigMap value, and a new `periodSeconds` on that same probe object. The chart's own changes
# landed (the positive control: without it the test could not have failed), and ALL FOUR drift
# shapes survived, including the probe field on the very probe the chart rewrote.
#
# Helm 3+ patches with a three-way strategic merge: it removes only what IT previously owned.
# Anything added out-of-band is not in the old manifest, so no patch touches it. This corrects
# what this header and these findings used to assert, and what MeshWeaver#2355 was triaged
# against for a week: `helm upgrade` DOES NOT delete cluster-only settings and DOES NOT overwrite
# a DIFFERS value. Ranking the backlog by "what a deploy would destroy" ranked it by a hazard
# that does not exist, and buried the one that does — see SHADOWS/COLLIDES below.
#
# FIVE CLASSES OF FINDING, worst first, and they mean different things:
#   COLLIDES       inline env + a ConfigMap key differing only in CASE → the pod carries BOTH
#                  (Linux env is case-SENSITIVE); .NET's config provider is case-INSENSITIVE and
#                  the last one enumerated wins, so the effective value is A COIN TOSS PER POD
#                  START. This is not future drift — it is live non-determinism, and it is what
#                  crashed memex at boot on 2026-08-30 (EmailConfigurationGuard, SIGABRT, and a
#                  restart on identical config came up fine).
#   SHADOWS        inline env + the SAME ConfigMap key → the inline entry deterministically wins.
#                  The chart's value is DEAD while still rendering, so every reader of the chart,
#                  the values files and the ConfigMap is reading a value no pod uses. This is the
#                  #2235 shape: the ConfigMap said Hosting/PlatformBuilds, the pod ran
#                  Store/Payments, every signal was green and the endpoint 404'd for 11 days.
#   CLUSTER-ONLY   live, not rendered   → survives a deploy, but exists in NO committed source, so
#                  it is lost the moment the namespace is rebuilt or restored from the chart, and
#                  it is invisible to review. Fleet rule: nothing may live only on the cluster.
#   CHART-ONLY     rendered, not live   → described but never applied; nobody is getting it
#   DIFFERS        both, values differ  → chart and cluster disagree; a deploy does NOT resolve it,
#                  so it persists until somebody decides which side is authoritative
#
# EXPECTED post-helm patches. An env's deploy.sh applies portal-patch.json AFTER `helm upgrade`
# (the CSI envFrom, extra volumes, nodeSelector, resources). Pass that file with --expect-patch
# so its envFrom additions are recognised as DECLARED rather than reported as drift. Without it
# they are reported — reporting an expected difference is a nuisance; hiding an unexpected one
# is the bug this script exists to prevent.
#
# PRIVATE CLUSTER. <aks-cluster> takes no direct kubectl. Use the aks-invoke transport:
#   ./check-chart-drift.sh -n <namespace> -r <release> --via aks-invoke \
#       -g <aks-resource-group> --aks <aks-cluster> -f ../values.aks.yaml -f <env>/values.<env>.yaml
# The transport is an EXPLICIT flag with no silent fallback: a kubectl that cannot reach the
# cluster fails RED rather than quietly comparing against nothing.
#
# Per-env values files live in the PRIVATE Systemorph/Memex repo, not here — point -f at your
# checkout of them. Render with the SAME -f list the deploy uses, or you are diffing against a
# chart nobody deployed.
set -uo pipefail

NS="" ; RELEASE="" ; CHART="" ; VIA="kubectl" ; RG="" ; AKS="" ; EXPECT_PATCH=""
VALUES=()

SELF_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
CHART_DEFAULT="$SELF_DIR/../../helm"

usage() {
  sed -n '2,96p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
  exit 2
}

while [ $# -gt 0 ]; do
  case "$1" in
    -n|--namespace)     NS="${2:-}"; shift 2 ;;
    -r|--release)       RELEASE="${2:-}"; shift 2 ;;
    -f|--values)        VALUES+=("${2:-}"); shift 2 ;;
    -c|--chart)         CHART="${2:-}"; shift 2 ;;
    --via)              VIA="${2:-}"; shift 2 ;;
    -g|--resource-group) RG="${2:-}"; shift 2 ;;
    --aks)              AKS="${2:-}"; shift 2 ;;
    --expect-patch)     EXPECT_PATCH="${2:-}"; shift 2 ;;
    -h|--help)          usage ;;
    *) echo "::error::unknown argument '$1'"; usage ;;
  esac
done

CHART="${CHART:-$CHART_DEFAULT}"

fail=0
summary() { [ -n "${GITHUB_STEP_SUMMARY:-}" ] && echo "$1" >> "$GITHUB_STEP_SUMMARY"; return 0; }
report()  { echo "::error::$1"; summary "- ❌ $1"; fail=1; }
ok()      { echo "$1";          summary "- ✅ $1"; }

# ---------------------------------------------------------------------------
# PREFLIGHT — assert every input this check depends on, and fail RED naming exactly what to
# provide. The check never asks "is the input present?" to decide whether to RUN; a missing
# input is a failure, never a skip.
# ---------------------------------------------------------------------------
missing=()
[ -n "$NS" ]      || missing+=("-n <namespace>            the k8s namespace the release runs in")
[ -n "$RELEASE" ] || missing+=("-r <release>              the helm release name (e.g. exampledb)")
[ ${#VALUES[@]} -gt 0 ] || missing+=("-f <values.yaml>          at least one env values file — rendering with chart defaults compares against a chart nobody deployed. These live in the PRIVATE Systemorph/Memex repo.")
command -v helm    >/dev/null 2>&1 || missing+=("helm                      not on PATH — brew install helm")
if ! command -v python3 >/dev/null 2>&1; then
  missing+=("python3                   not on PATH — needed to parse the manifests")
# PyYAML is third-party, not stdlib. The compare phase below imports it, so assert it HERE: without
# this the check dies mid-run with a traceback instead of naming what to install, and a dependency
# that is present only by luck of the runner image is an unstated input.
elif ! python3 -c "import yaml" >/dev/null 2>&1; then
  missing+=("PyYAML                    python3 has no 'yaml' module — pip install pyyaml (or apt-get install python3-yaml)")
fi
[ -d "$CHART" ]   || missing+=("-c <chart-dir>            chart not found at '$CHART'")
for v in ${VALUES[@]+"${VALUES[@]}"}; do
  [ -f "$v" ] || missing+=("-f '$v'                   values file does not exist")
done
case "$VIA" in
  kubectl)
    command -v kubectl >/dev/null 2>&1 || missing+=("kubectl                   not on PATH — brew install kubectl") ;;
  aks-invoke)
    command -v az >/dev/null 2>&1 || missing+=("az                        not on PATH — the aks-invoke transport needs the Azure CLI")
    [ -n "$RG" ]  || missing+=("-g <resource-group>       required by --via aks-invoke")
    [ -n "$AKS" ] || missing+=("--aks <cluster>           required by --via aks-invoke") ;;
  *)
    missing+=("--via <kubectl|aks-invoke>  unknown transport '$VIA'") ;;
esac
[ -z "$EXPECT_PATCH" ] || [ -f "$EXPECT_PATCH" ] || missing+=("--expect-patch '$EXPECT_PATCH'  file does not exist")

if [ ${#missing[@]} -gt 0 ]; then
  echo "::error::check-chart-drift cannot run — provide the following:"
  for m in "${missing[@]}"; do echo "  • $m"; done
  exit 1
fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# ---------------------------------------------------------------------------
# DESIRED — what the chart says. A render failure is RED: it is the whole left-hand side.
# ---------------------------------------------------------------------------
helm_args=( template "$RELEASE" "$CHART" --namespace "$NS" )
for v in ${VALUES[@]+"${VALUES[@]}"}; do helm_args+=( -f "$v" ); done
if ! helm "${helm_args[@]}" > "$WORK/desired.yaml" 2> "$WORK/helm.err"; then
  echo "::error::helm template FAILED — cannot determine what the chart describes:"
  cat "$WORK/helm.err"
  exit 1
fi

# ---------------------------------------------------------------------------
# LIVE — what the cluster runs. Any transport failure is RED; there is no "assume no drift".
# ---------------------------------------------------------------------------
fetch() { # $1 = resource
  case "$VIA" in
    kubectl)    kubectl -n "$NS" get "$1" -o json 2>"$WORK/kubectl.err" ;;
    aks-invoke) az aks command invoke -g "$RG" -n "$AKS" -o tsv --query logs \
                   --command "kubectl -n $NS get $1 -o json" 2>"$WORK/kubectl.err" ;;
  esac
}
# The availability objects are fetched as LISTS, not by name, and that is deliberate. A named GET
# of an object that does not exist fails, and "it does not exist" is a legitimate, expected answer
# in a non-KEDA namespace — so a named GET forces the script to treat a real transport failure and
# a real absence identically. A list GET always returns a valid List document when the transport
# works (possibly with `items: []`) and invalid output when it does not, which keeps "no PDB here"
# a POSITIVE finding while a broken connection stays RED.
for res in "configmap/memex-portal-config" "deployment/memex-portal-deployment" \
           "poddisruptionbudgets" "scaledobjects.keda.sh"; do
  out="$WORK/live-$(echo "$res" | tr '/' '-' | tr '.' '-').json"
  if ! fetch "$res" > "$out" || ! python3 -c "import json,sys; json.load(open(sys.argv[1]))" "$out" 2>/dev/null; then
    echo "::error::could not read $res from namespace '$NS' via --via $VIA. This is a FAILURE, not an absence of drift."
    [ -s "$WORK/kubectl.err" ] && sed 's/^/    /' "$WORK/kubectl.err"
    exit 1
  fi
done

# ---------------------------------------------------------------------------
# COMPARE
# ---------------------------------------------------------------------------
python3 "$SELF_DIR/chart-drift-compare.py" \
  "$WORK/desired.yaml" \
  "$WORK/live-configmap-memex-portal-config.json" \
  "$WORK/live-deployment-memex-portal-deployment.json" \
  "$EXPECT_PATCH" \
  "$WORK/live-poddisruptionbudgets.json" \
  "$WORK/live-scaledobjects-keda-sh.json"
rc=$?

if [ "$rc" -ne 0 ]; then
  report "namespace '$NS' has drifted from the chart (or the comparison could not be made)"
else
  ok "namespace '$NS' matches the rendered chart"
fi
exit "$fail"
