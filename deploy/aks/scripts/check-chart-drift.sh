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
#      CHART — so atioz never got it, and `grep -c 'GitHub__App__'` on main was 0;
#   4. atioz's PluginCatalog__* were inline `env:` on the Deployment, NOT in memex-portal-config
#      — a `helm upgrade` does not reproduce them, it DELETES them.
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
#                                        lifecycle.preStop, terminationGracePeriodSeconds, and
#                                        the three probes
#
# Inline `env` and the ConfigMap are BOTH needed and neither is redundant: an inline env entry
# OVERRIDES envFrom, so `kubectl set env` (defect 3) leaves the ConfigMap matching the render
# perfectly while the pod runs a value the chart has never heard of.
#
# THREE CLASSES OF FINDING, and they mean different things:
#   CLUSTER-ONLY   live, not rendered   → hand-applied; the next `helm upgrade` DELETES it
#   CHART-ONLY     rendered, not live   → described but never applied; nobody is getting it
#   DIFFERS        both, values differ  → the render would overwrite the running value
#
# EXPECTED post-helm patches. An env's deploy.sh applies portal-patch.json AFTER `helm upgrade`
# (the CSI envFrom, extra volumes, nodeSelector, resources). Pass that file with --expect-patch
# so its envFrom additions are recognised as DECLARED rather than reported as drift. Without it
# they are reported — reporting an expected difference is a nuisance; hiding an unexpected one
# is the bug this script exists to prevent.
#
# PRIVATE CLUSTER. <aks-cluster> takes no direct kubectl. Use the aks-invoke transport:
#   ./check-chart-drift.sh -n memex -r memex --via aks-invoke \
#       -g <aks-resource-group> --aks <aks-cluster> -f ../values.aks.yaml -f ../envs/memex/values.yaml
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
  sed -n '2,60p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
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
[ -n "$NS" ]      || missing+=("-n <namespace>            the k8s namespace the release runs in (e.g. memex, atioz)")
[ -n "$RELEASE" ] || missing+=("-r <release>              the helm release name (e.g. memex, exampledb)")
[ ${#VALUES[@]} -gt 0 ] || missing+=("-f <values.yaml>          at least one env values file — rendering with chart defaults compares against a chart nobody deployed. These live in the PRIVATE Systemorph/Memex repo.")
command -v helm    >/dev/null 2>&1 || missing+=("helm                      not on PATH — brew install helm")
command -v python3 >/dev/null 2>&1 || missing+=("python3                   not on PATH — needed to parse the manifests")
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
for res in "configmap/memex-portal-config" "deployment/memex-portal-deployment"; do
  out="$WORK/live-$(echo "$res" | tr '/' '-').json"
  if ! fetch "$res" > "$out" || ! python3 -c "import json,sys; json.load(open(sys.argv[1]))" "$out" 2>/dev/null; then
    echo "::error::could not read $res from namespace '$NS' via --via $VIA. This is a FAILURE, not an absence of drift."
    [ -s "$WORK/kubectl.err" ] && sed 's/^/    /' "$WORK/kubectl.err"
    exit 1
  fi
done

# ---------------------------------------------------------------------------
# COMPARE
# ---------------------------------------------------------------------------
python3 - \
  "$WORK/desired.yaml" \
  "$WORK/live-configmap-memex-portal-config.json" \
  "$WORK/live-deployment-memex-portal-deployment.json" \
  "$EXPECT_PATCH" <<'PY'
import json, sys, yaml

desired_path, live_cm_path, live_dep_path, expect_patch = sys.argv[1:5]

findings, comparisons = [], 0
def finding(kind, what, detail=""):
    findings.append((kind, what, detail))

# ---- desired ---------------------------------------------------------------
d_cm = d_dep = None
for doc in yaml.safe_load_all(open(desired_path)):
    if not doc:
        continue
    name = (doc.get("metadata") or {}).get("name")
    if doc.get("kind") == "ConfigMap" and name == "memex-portal-config":
        d_cm = doc
    if doc.get("kind") == "Deployment" and name == "memex-portal-deployment":
        d_dep = doc

if d_cm is None or d_dep is None:
    print("::error::the rendered chart contains no memex-portal-config ConfigMap and/or no "
          "memex-portal-deployment Deployment. Nothing could be compared — treating as FAILURE, "
          "because 'compared nothing' must never read as 'found no drift'.")
    sys.exit(1)

l_cm = json.load(open(live_cm_path))
l_dep = json.load(open(live_dep_path))

def portal_container(dep):
    for c in ((dep.get("spec") or {}).get("template") or {}).get("spec", {}).get("containers", []):
        if c.get("name") == "memex-portal":
            return c
    return None

d_c, l_c = portal_container(d_dep), portal_container(l_dep)
if d_c is None or l_c is None:
    print("::error::no container named 'memex-portal' in the rendered and/or live Deployment — "
          "nothing could be compared. Treating as FAILURE.")
    sys.exit(1)

# ---- 1. ConfigMap: every key, every value ----------------------------------
d_data, l_data = d_cm.get("data") or {}, l_cm.get("data") or {}
if not d_data:
    print("::error::the rendered memex-portal-config has ZERO keys — the comparison would be "
          "vacuous. Treating as FAILURE.")
    sys.exit(1)

for k in sorted(set(d_data) | set(l_data)):
    comparisons += 1
    if k not in l_data:
        finding("CHART-ONLY", f"ConfigMap {k}",
                f"rendered '{d_data[k]}' — described but NOT running; nobody is getting it")
    elif k not in d_data:
        finding("CLUSTER-ONLY", f"ConfigMap {k}",
                f"live '{l_data[k]}' — hand-applied; the next `helm upgrade` DELETES it")
    elif d_data[k] != l_data[k]:
        finding("DIFFERS", f"ConfigMap {k}",
                f"chart '{d_data[k]}' vs live '{l_data[k]}' — a render would overwrite the running value")

# ---- 2. inline env NAMES (values withheld — they hold tokens) --------------
def env_names(c):
    return {e["name"] for e in (c.get("env") or []) if "name" in e}

d_env, l_env = env_names(d_c), env_names(l_c)
for n in sorted(d_env | l_env):
    comparisons += 1
    if n not in l_env:
        finding("CHART-ONLY", f"inline env {n}", "rendered but not on the running pod spec")
    elif n not in d_env:
        finding("CLUSTER-ONLY", f"inline env {n}",
                "hand-applied (`kubectl set env`?) and NOT in the chart. An inline env OVERRIDES "
                "envFrom, so this value is what the pod actually uses — and `helm upgrade` DELETES it")

# ---- 3. envFrom refs, minus whatever the env's post-helm patch declares -----
def env_from(c):
    out = set()
    for e in c.get("envFrom") or []:
        for kind in ("configMapRef", "secretRef"):
            if kind in e:
                out.add(f"{kind}:{e[kind].get('name')}")
    return out

declared = set()
if expect_patch:
    for op in json.load(open(expect_patch)):
        if "envFrom" in str(op.get("path", "")):
            val = op.get("value") or {}
            for kind in ("configMapRef", "secretRef"):
                if kind in val:
                    declared.add(f"{kind}:{val[kind].get('name')}")

d_ef, l_ef = env_from(d_c), env_from(l_c)
for r in sorted(d_ef | l_ef):
    comparisons += 1
    if r not in l_ef:
        finding("CHART-ONLY", f"envFrom {r}", "rendered but not on the running pod spec")
    elif r not in d_ef and r not in declared:
        hint = "hand-applied and NOT in the chart"
        if not expect_patch:
            hint += " — if an env portal-patch.json adds it, pass --expect-patch to declare it"
        finding("CLUSTER-ONLY", f"envFrom {r}", hint)

# ---- 4. the pod-lifecycle fields a roll depends on -------------------------
# The API server DEFAULTS omitted probe fields (scheme: HTTP, successThreshold: 1, …), so a live
# probe is never byte-equal to a rendered one even when they are the same probe. Normalise BOTH
# sides to effective values before comparing: a chart that omits `successThreshold` and a live
# object that carries the default 1 mean the identical thing, and reporting that as drift is how
# a checker trains its reader to ignore it.
PROBE_DEFAULTS = {"successThreshold": 1, "initialDelaySeconds": 0}
def norm_probe(p):
    if not isinstance(p, dict):
        return p
    p = json.loads(json.dumps(p))
    for k, dflt in PROBE_DEFAULTS.items():
        if p.get(k) == dflt:
            p.pop(k, None)
    if isinstance(p.get("httpGet"), dict) and p["httpGet"].get("scheme") == "HTTP":
        p["httpGet"].pop("scheme")
    return p

d_pod = (d_dep["spec"]["template"]["spec"])
l_pod = (l_dep["spec"]["template"]["spec"])
for label, dv, lv in [
    ("lifecycle.preStop", (d_c.get("lifecycle") or {}).get("preStop"),
                          (l_c.get("lifecycle") or {}).get("preStop")),
    ("terminationGracePeriodSeconds", d_pod.get("terminationGracePeriodSeconds"),
                                      l_pod.get("terminationGracePeriodSeconds")),
    ("startupProbe",   norm_probe(d_c.get("startupProbe")),   norm_probe(l_c.get("startupProbe"))),
    ("readinessProbe", norm_probe(d_c.get("readinessProbe")), norm_probe(l_c.get("readinessProbe"))),
    ("livenessProbe",  norm_probe(d_c.get("livenessProbe")),  norm_probe(l_c.get("livenessProbe"))),
]:
    comparisons += 1
    if dv == lv:
        continue
    if lv is None:
        finding("CHART-ONLY", label,
                f"rendered {json.dumps(dv)} — NOT on the running pod. This is exactly how the "
                f"drain preStop sat unapplied while every roll cut live circuits")
    elif dv is None:
        finding("CLUSTER-ONLY", label, f"live {json.dumps(lv)} — hand-applied; `helm upgrade` DELETES it")
    else:
        finding("DIFFERS", label, f"chart {json.dumps(dv)} vs live {json.dumps(lv)}")

# ---- verdict ---------------------------------------------------------------
# The evidence assertion: a run that compared (almost) nothing must not read as a pass.
MIN = 20
if comparisons < MIN:
    print(f"::error::only {comparisons} fields were compared (expected at least {MIN}) — the "
          f"objects are not the shape this check understands. Treating as FAILURE rather than "
          f"reporting 'no drift' on no evidence.")
    sys.exit(1)

if not findings:
    print(f"No drift: {comparisons} fields compared, the cluster matches the rendered chart.")
    sys.exit(0)

order = {"CLUSTER-ONLY": 0, "CHART-ONLY": 1, "DIFFERS": 2}
for kind, what, detail in sorted(findings, key=lambda f: (order[f[0]], f[1])):
    print(f"::error::{kind:<12} {what}")
    if detail:
        print(f"                 {detail}")
print(f"\n{len(findings)} divergence(s) across {comparisons} compared fields.")
print("CLUSTER-ONLY  → put it in the chart (values + template), or the next `helm upgrade` drops it.")
print("CHART-ONLY    → apply it: `helm upgrade` for chart-managed fields; nobody is getting it today.")
print("DIFFERS       → decide which is authoritative, then make the other match.")
sys.exit(1)
PY
rc=$?

if [ "$rc" -ne 0 ]; then
  report "namespace '$NS' has drifted from the chart (or the comparison could not be made)"
else
  ok "namespace '$NS' matches the rendered chart"
fi
exit "$fail"
