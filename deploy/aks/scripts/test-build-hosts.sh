#!/usr/bin/env bash
#
# The standing build hosts' chart half (templates/build-host/, Doc/Architecture/BuildHosts), checked
# the only ways that can fail without a cluster:
#
#   1. the runner the chart ships (deploy/helm/files/build-run.py) IS the platform's
#      (.github/scripts/build-run.py), byte for byte — one implementation, and its self-test passes
#      from the chart's copy;
#   2. the chart defaults render NO build-host object at all (buildHosts.enabled: false), so no
#      estate changes until its record turns the hosts on;
#   3. the fixture renders, per enabled channel, a Deployment with no `replicas` (KEDA owns it),
#      images by DIGEST, the cibuild toleration + nodeSelector, the worker token from a Secret, the
#      build-cache claim, readiness on the staged marker, and a ScaledObject with minReplicaCount 0,
#      the channel's maxReplicaCount and a metrics-api trigger on <apiUrl>/queue?channel=<c> with
#      valueLocation claimable — plus a ServiceAccount with no token and a NetworkPolicy with no ingress;
#   4. the refusals hold: a tag instead of a digest, and plain http to a public host, fail
#      `helm template` naming why.
#
# NO SKIP-TRAPDOOR: helm, python3 and PyYAML are asserted; a render that yields nothing is RED.
set -uo pipefail

SELF_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd -- "$SELF_DIR/../../.." && pwd)"
CHART="$REPO/deploy/helm"
TD="$SELF_DIR/testdata"
fail=0
report() { echo "::error::$1"; fail=1; }
ok() { echo "✅ $1"; }

command -v helm >/dev/null 2>&1 || { echo "::error::helm is not on PATH"; exit 1; }
python3 -c "import yaml" >/dev/null 2>&1 || { echo "::error::python3 with PyYAML is required (pip install pyyaml)"; exit 1; }

# 1. one implementation
if cmp -s "$CHART/files/build-run.py" "$REPO/.github/scripts/build-run.py"; then
  ok "deploy/helm/files/build-run.py is the platform's .github/scripts/build-run.py"
else
  report "deploy/helm/files/build-run.py differs from .github/scripts/build-run.py — copy it byte for byte (cp .github/scripts/build-run.py deploy/helm/files/)"
fi
if python3 "$CHART/files/build-run.py" --self-test; then
  ok "the chart's build-run.py passes its self-test"
else
  report "the chart's build-run.py fails its self-test"
fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# 2. defaults render nothing
if helm template r "$CHART" -f "$CHART/values.yaml" --namespace check > "$WORK/defaults.yaml" 2> "$WORK/defaults.err"; then
  if python3 - "$WORK/defaults.yaml" <<'PY'
import sys, yaml
bad = [f"{d['kind']}/{d['metadata']['name']}" for d in yaml.safe_load_all(open(sys.argv[1]))
       if d and (d.get("metadata", {}).get("labels") or {}).get("app.kubernetes.io/component") == "build-host"]
if bad:
    print("rendered with buildHosts.enabled false: " + ", ".join(bad)); sys.exit(1)
PY
  then ok "the chart defaults render no build-host object"
  else report "the chart defaults render build-host objects — buildHosts must be OFF until a record turns it on"
  fi
else
  report "the chart defaults do not render:"; sed 's/^/    /' "$WORK/defaults.err"
fi

# 3. the fixture
if helm template r "$CHART" -f "$CHART/values.yaml" -f "$TD/values.build-hosts.yaml" --namespace check \
     > "$WORK/hosts.yaml" 2> "$WORK/hosts.err"; then
  if python3 - "$WORK/hosts.yaml" <<'PY'
import sys, yaml
docs = [d for d in yaml.safe_load_all(open(sys.argv[1])) if d]
mine = [d for d in docs if (d.get("metadata", {}).get("labels") or {}).get("app.kubernetes.io/component") == "build-host"]
problems = []
def find(kind, name):
    return next((d for d in mine if d["kind"] == kind and d["metadata"]["name"] == name), None)
want = {"latest": ("1111", "2222", 2, "3.1.10412"), "stable": ("3333", "4444", 1, "3.1.10388")}
for ch, (tester, portal, mx, pv) in want.items():
    dep = find("Deployment", f"build-host-{ch}")
    so = find("ScaledObject", f"build-host-{ch}-scaler")
    if not dep or not so:
        problems.append(f"{ch}: Deployment or ScaledObject missing"); continue
    spec = dep["spec"]
    if "replicas" in spec:
        problems.append(f"{ch}: Deployment renders spec.replicas beside a ScaledObject")
    pod = spec["template"]["spec"]
    inits = {c["name"]: c for c in pod.get("initContainers", [])}
    if not inits.get("tester", {}).get("image", "").endswith("@sha256:" + tester * 16):
        problems.append(f"{ch}: tester init image is not the channel's digest: {inits.get('tester', {}).get('image')}")
    if not inits.get("portal", {}).get("image", "").endswith("@sha256:" + portal * 16):
        problems.append(f"{ch}: portal init image is not the channel's digest: {inits.get('portal', {}).get('image')}")
    if pod.get("nodeSelector") != {"workload": "cibuild"}:
        problems.append(f"{ch}: nodeSelector is not workload=cibuild: {pod.get('nodeSelector')}")
    if not any(t.get("key") == "ci" and t.get("value") == "build" for t in pod.get("tolerations", [])):
        problems.append(f"{ch}: no ci=build toleration")
    if pod.get("serviceAccountName") != "build-host" or pod.get("automountServiceAccountToken") is not False:
        problems.append(f"{ch}: not the build-host ServiceAccount without a mounted token")
    build = next((c for c in pod["containers"] if c["name"] == "build"), {})
    env = {e["name"]: e for e in build.get("env", [])}
    if env.get("BUILD_TOKEN", {}).get("valueFrom", {}).get("secretKeyRef", {}).get("name") != "build-worker":
        problems.append(f"{ch}: BUILD_TOKEN is not read from the build-worker Secret")
    if env.get("MW_BUILD_CHANNEL", {}).get("value") != ch or env.get("MW_BUILD_PLATFORM_VERSION", {}).get("value") != pv:
        problems.append(f"{ch}: channel/platformVersion env wrong")
    if env.get("BUILD_API", {}).get("value") != "http://memex-portal-service:8080/api/builds":
        problems.append(f"{ch}: BUILD_API not the trimmed apiUrl: {env.get('BUILD_API')}")
    if "exec python3 /scripts/build-run.py --host" not in " ".join(build.get("command", [])):
        problems.append(f"{ch}: the runner is not exec'd (SIGTERM would not reach it)")
    if "test -f /tmp/build-host-ready" not in " ".join(build.get("readinessProbe", {}).get("exec", {}).get("command", [])):
        problems.append(f"{ch}: readiness is not the staged marker")
    vols = {v["name"]: v for v in pod.get("volumes", [])}
    if vols.get("cache", {}).get("persistentVolumeClaim", {}).get("claimName") != "build-cache":
        problems.append(f"{ch}: the cache is not the build-cache claim")
    s = so["spec"]
    if s.get("minReplicaCount") != 0 or s.get("maxReplicaCount") != mx:
        problems.append(f"{ch}: ScaledObject min/max {s.get('minReplicaCount')}/{s.get('maxReplicaCount')} != 0/{mx}")
    if s.get("scaleTargetRef", {}).get("name") != f"build-host-{ch}":
        problems.append(f"{ch}: ScaledObject targets {s.get('scaleTargetRef')}")
    trig = [t for t in s.get("triggers", []) if t.get("type") == "metrics-api"]
    url = f"http://memex-portal-service:8080/api/builds/queue?channel={ch}"
    if not any(t["metadata"].get("url") == url and t["metadata"].get("valueLocation") == "claimable" for t in trig):
        problems.append(f"{ch}: no metrics-api trigger on {url} valueLocation claimable: {trig}")
sa = find("ServiceAccount", "build-host")
if not sa or sa.get("automountServiceAccountToken") is not False:
    problems.append("ServiceAccount build-host missing or mounts a token")
np = find("NetworkPolicy", "build-host")
if not np or np["spec"].get("ingress") != [] or "Ingress" not in np["spec"].get("policyTypes", []):
    problems.append("NetworkPolicy build-host missing or admits ingress")
if problems:
    print("\n".join(problems)); sys.exit(1)
PY
  then ok "the fixture renders both channels' hosts: digests, pool, token Secret, cache, readiness, KEDA 0..max on the channel's queue, no SA token, no ingress"
  else report "the build-host fixture renders a wrong shape (see above)"
  fi
else
  report "the build-host fixture does not render:"; sed 's/^/    /' "$WORK/hosts.err"
fi

# 4. refusals
for entry in \
  "values.build-hosts-tag-not-digest.yaml|must be a sha256:<64 hex> digest" \
  "values.build-hosts-public-http.yaml|must be https, or http to a cluster-local host"; do
  f="${entry%%|*}"; phrase="${entry#*|}"
  if helm template r "$CHART" -f "$CHART/values.yaml" -f "$TD/$f" > "$WORK/refusal.yaml" 2> "$WORK/refusal.err"; then
    report "refusal $f RENDERED — the chart must refuse it"
  elif grep -qF -- "$phrase" "$WORK/refusal.err"; then
    ok "refusal $f holds ($phrase)"
  else
    report "refusal $f failed, but not for the stated reason ('$phrase'):"; sed 's/^/    /' "$WORK/refusal.err"
  fi
done

exit "$fail"
