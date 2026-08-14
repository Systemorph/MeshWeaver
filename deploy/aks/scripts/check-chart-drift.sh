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
#                                        lifecycle.preStop, terminationGracePeriodSeconds, the
#                                        three probes, and the AVAILABILITY shape below
#   PodDisruptionBudget / ScaledObject   existence and shape
#
# 🚨 THE AVAILABILITY SHAPE was added 2026-08-14, because the check as first written would have
# MISSED the incident it was created in response to. On that day memex-cloud served every request
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
python3 - \
  "$WORK/desired.yaml" \
  "$WORK/live-configmap-memex-portal-config.json" \
  "$WORK/live-deployment-memex-portal-deployment.json" \
  "$EXPECT_PATCH" \
  "$WORK/live-poddisruptionbudgets.json" \
  "$WORK/live-scaledobjects-keda-sh.json" <<'PY'
import json, sys, yaml

desired_path, live_cm_path, live_dep_path, expect_patch = sys.argv[1:5]
live_pdb_path, live_so_path = sys.argv[5:7]

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

# ---- 5. the AVAILABILITY shape: replicas, the budget, the autoscaler --------
# Everything above describes ONE pod's configuration. None of it can tell you how many pods there
# are, or whether anything is allowed to take one away — which is the entire difference between a
# namespace that survives a node upgrade and one that 503s for minutes.
def named(path, obj_name):
    doc = json.load(open(path))
    for item in doc.get("items", []) if isinstance(doc, dict) else []:
        if (item.get("metadata") or {}).get("name") == obj_name:
            return item
    return None

d_pdb = next((d for d in yaml.safe_load_all(open(desired_path))
              if d and d.get("kind") == "PodDisruptionBudget"), None)
d_so = next((d for d in yaml.safe_load_all(open(desired_path))
             if d and d.get("kind") == "ScaledObject"), None)
l_pdb = named(live_pdb_path, "memex-portal-pdb")
l_so = named(live_so_path, "memex-portal-scaler")

# 5a. spec.replicas. Under KEDA the chart renders NO replicas (the HPA owns the field), so a live
# value is expected and is not drift — what matters then is the autoscaler's floor, checked below.
comparisons += 1
d_reps = (d_dep.get("spec") or {}).get("replicas")
l_reps = (l_dep.get("spec") or {}).get("replicas")
if d_so is not None and d_reps is not None:
    finding("DIFFERS", "spec.replicas",
            f"the chart renders replicas={d_reps} AND a ScaledObject. helm and the HPA would fight "
            f"over the field on every upgrade — the chart must omit it under KEDA")
elif d_so is None and d_reps != l_reps:
    finding("DIFFERS", "spec.replicas", f"chart {d_reps} vs live {l_reps}")

# 5b. the disruption budget — shape AND its live verdict.
comparisons += 1
if d_pdb is None and l_pdb is not None:
    finding("CLUSTER-ONLY", "PodDisruptionBudget memex-portal-pdb",
            f"live {json.dumps(l_pdb.get('spec'))} — hand-applied, and NOT owned by the helm "
            f"release. A `helm upgrade` whose values later render a PDB of this name does not adopt "
            f"it: helm refuses an object without its ownership metadata ('invalid ownership "
            f"metadata') and the upgrade FAILS. Express it in values (keda.enabled) and let the "
            f"chart own it")
elif d_pdb is not None and l_pdb is None:
    finding("CHART-ONLY", "PodDisruptionBudget memex-portal-pdb",
            "rendered but not running — nothing throttles voluntary disruption today")
elif d_pdb is not None and l_pdb is not None:
    d_spec = {k: v for k, v in (d_pdb.get("spec") or {}).items() if k != "selector"}
    l_spec = {k: v for k, v in (l_pdb.get("spec") or {}).items() if k != "selector"}
    if d_spec != l_spec:
        finding("DIFFERS", "PodDisruptionBudget spec",
                f"chart {json.dumps(d_spec, sort_keys=True)} vs live {json.dumps(l_spec, sort_keys=True)}. "
                f"minAvailable and maxUnavailable are DIFFERENT OBJECT SHAPES, not two spellings — "
                f"minAvailable set equal to the replica count allows zero disruptions for ever")

# A live budget that permits nothing is an outage condition on its own terms — report it whether or
# not the chart happens to agree, because agreeing with it would not make it survivable.
comparisons += 1
if l_pdb is not None:
    st = l_pdb.get("status") or {}
    if st.get("disruptionsAllowed") == 0:
        finding("DIFFERS", "PodDisruptionBudget allows NO disruption",
                f"live disruptionsAllowed=0 (currentHealthy={st.get('currentHealthy')}, "
                f"desiredHealthy={st.get('desiredHealthy')}). Every node image-upgrade and every "
                f"drain is blocked, indefinitely and silently. Either the budget is the wrong shape "
                f"or there are fewer healthy pods than it requires")

# 5c. the autoscaler — including the annotation that silently pins the replica count.
comparisons += 1
if d_so is None and l_so is not None:
    finding("CLUSTER-ONLY", "ScaledObject memex-portal-scaler",
            "live but not rendered — hand-applied, and NOT owned by the helm release, so the "
            "replica floor this namespace depends on is invisible to every deploy. Helm will not "
            "adopt it either: once values render a ScaledObject of this name the upgrade FAILS on "
            "'invalid ownership metadata'. Set keda.enabled in the values instead")
elif d_so is not None and l_so is None:
    finding("CHART-ONLY", "ScaledObject memex-portal-scaler",
            "rendered but not running — nothing is holding the replica floor")
elif d_so is not None and l_so is not None:
    for key in ("minReplicaCount", "maxReplicaCount"):
        comparisons += 1
        dv, lv = (d_so.get("spec") or {}).get(key), (l_so.get("spec") or {}).get(key)
        if dv != lv:
            finding("DIFFERS", f"ScaledObject {key}", f"chart {dv} vs live {lv}")

comparisons += 1
paused = ((l_so or {}).get("metadata") or {}).get("annotations", {}).get(
    "autoscaling.keda.sh/paused-replicas")
if paused is not None:
    finding("CLUSTER-ONLY", "ScaledObject autoscaling.keda.sh/paused-replicas",
            f"live '{paused}' — KEDA is PAUSED and pins the deployment at {paused} replica(s). The "
            f"HPA is deleted while this is set, minReplicaCount is not enforced, and `kubectl scale` "
            f"is silently reverted. Nothing in the chart sets this; remove the annotation to resume "
            f"autoscaling (`kubectl annotate scaledobject memex-portal-scaler "
            f"autoscaling.keda.sh/paused-replicas-`)")

# ---- verdict ---------------------------------------------------------------
# The evidence assertion: a run that compared (almost) nothing must not read as a pass.
MIN = 25
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
