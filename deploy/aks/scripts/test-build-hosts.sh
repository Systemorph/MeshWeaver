#!/usr/bin/env bash
#
# The standing build hosts' chart half (templates/build-host/, Doc/Architecture/BuildHosts), checked
# the only ways that can fail without a cluster:
#
#   1. the runner ships in ONE place — the mw-build-host image, which copies .github/scripts'
#      build-run.py and compose-gate-host.sh (deploy/build-host/Dockerfile); the chart carries no
#      copy, and the runner's self-test passes;
#   2. the chart defaults render NO build-host object at all (buildHosts.enabled: false), so no
#      estate changes until its record turns the hosts on;
#   3. the fixture renders, per enabled channel, a Deployment with no `replicas` (KEDA owns it),
#      images by DIGEST, the cibuild toleration + nodeSelector, the worker token from a Secret, the
#      build-cache claim, readiness on the staged marker, and a ScaledObject with minReplicaCount 0,
#      the channel's maxReplicaCount and a metrics-api trigger on <apiUrl>/queue?channel=<c> with
#      valueLocation demand and a scale-down stabilization window, the worker token as a mounted
#      FILE (never env), grace = the run deadline, no credential volume but that one and no bake
#      share — plus a ServiceAccount with no token and a NetworkPolicy with no ingress whose egress
#      is pinned whole (DNS to the cluster resolver only; the build API's pods on their port; 80/443
#      outside every private range, IMDS and WireServer);
#   4. the refusals hold: a tag instead of a digest, plain http to a public host, and a registry
#      credential for the host, fail
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

# 1. one implementation: the runner and the composer ship INSIDE the mw-build-host image, copied
#    from .github/scripts by deploy/build-host/Dockerfile — the chart carries no copy of either.
DOCKERFILE="$REPO/deploy/build-host/Dockerfile"
if [ -f "$CHART/files/build-run.py" ]; then
  report "deploy/helm/files/build-run.py exists — the chart must not carry a second copy of the runner; it ships in the mw-build-host image"
fi
if [ -f "$DOCKERFILE" ] \
   && grep -q '^COPY .github/scripts/build-run.py .github/scripts/compose-gate-host.sh .github/scripts/sandbox-postgres.sh .github/scripts/module-build-key.py .github/scripts/ci-artifact-store.py /opt/build-host/$' "$DOCKERFILE" \
   && grep -q '^COPY artifacts/plugin-build/ /opt/build-host/plugin-build/$' "$DOCKERFILE" \
   && grep -q 'bubblewrap' "$DOCKERFILE" \
   && grep -q 'sandbox-postgres.sh --self-test' "$DOCKERFILE" \
   && grep -q 'build-run.py --self-test' "$DOCKERFILE" \
   && ! grep -Eq '^FROM [^ ]*:latest' "$DOCKERFILE"; then
  ok "deploy/build-host/Dockerfile ships .github/scripts' runner, composer, sandbox-postgres and module-build-key plus the plugin-build tool, installs bubblewrap, runs both self-tests, and pins no :latest"
else
  report "deploy/build-host/Dockerfile must COPY .github/scripts/{build-run.py,compose-gate-host.sh,sandbox-postgres.sh,module-build-key.py} and artifacts/plugin-build/ to /opt/build-host/, install bubblewrap, run both self-tests, and name no :latest base"
fi
if python3 "$REPO/.github/scripts/build-run.py" --self-test; then
  ok "build-run.py passes its self-test"
else
  report "build-run.py fails its self-test"
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
want = {"latest": ("1111", "2222", "5555", 2, "3.1.10412"), "stable": ("3333", "4444", "6666", 1, "3.1.10388")}
for ch, (tester, portal, runner, mx, pv) in want.items():
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
    if build.get("image") != "cr.example.com/mw-build-host@sha256:" + runner * 16:
        problems.append(f"{ch}: the runner image is not the channel's mw-build-host digest: {build.get('image')}")
    sc = pod.get("securityContext", {})
    if not (sc.get("runAsNonRoot") is True and sc.get("runAsUser") == 1654 and sc.get("fsGroup") == 1654):
        problems.append(f"{ch}: the pod does not run as the runner image's unprivileged user 1654: {sc}")
    if spec["template"]["metadata"].get("labels", {}).get("azure.workload.identity/use") != "true":
        problems.append(f"{ch}: the pod does not opt into Azure workload identity (label azure.workload.identity/use)")
    skip = set((spec["template"]["metadata"].get("annotations") or {}).get("azure.workload.identity/skip-containers", "").split(";"))
    if skip != {"tester", "portal", "oras"}:
        problems.append(f"{ch}: the identity token is not restricted to the build container (skip-containers {sorted(skip)})")
    env = {e["name"]: e for e in build.get("env", [])}
    if "BUILD_TOKEN" in env:
        problems.append(f"{ch}: BUILD_TOKEN is an environment variable — the worker token must be a mounted file")
    if env.get("BUILD_TOKEN_FILE", {}).get("value") != "/run/build-worker/token":
        problems.append(f"{ch}: BUILD_TOKEN_FILE is not /run/build-worker/token")
    if pod.get("terminationGracePeriodSeconds") != 3600 or env.get("MW_BUILD_RUN_DEADLINE_SECONDS", {}).get("value") != "3600":
        problems.append(f"{ch}: terminationGracePeriodSeconds / the run deadline are not the same 3600 s")
    if build.get("securityContext", {}).get("seccompProfile", {}) != {"type": "Unconfined"}:
        problems.append(f"{ch}: the stated sandbox seccomp profile did not render: {build.get('securityContext')}")
    if build.get("securityContext", {}).get("appArmorProfile", {}) != {"type": "Localhost", "localhostProfile": "build-host-userns"}:
        problems.append(f"{ch}: a Localhost AppArmor profile must render its localhostProfile: {build.get('securityContext')}")
    if env.get("MW_BUILD_CHANNEL", {}).get("value") != ch or env.get("MW_BUILD_PLATFORM_VERSION", {}).get("value") != pv:
        problems.append(f"{ch}: channel/platformVersion env wrong")
    if env.get("BUILD_API", {}).get("value") != "http://memex-portal-service:8080/api/builds":
        problems.append(f"{ch}: BUILD_API not the trimmed apiUrl: {env.get('BUILD_API')}")
    if "exec python3 /opt/build-host/build-run.py --host" not in " ".join(build.get("command", [])):
        problems.append(f"{ch}: the runner is not exec'd (SIGTERM would not reach it)")
    if "test -f /tmp/build-host-ready" not in " ".join(build.get("readinessProbe", {}).get("exec", {}).get("command", [])):
        problems.append(f"{ch}: readiness is not the staged marker")
    cmd = " ".join(build.get("command", []))
    if "compose-gate-host.sh /portal/app /tester /host/app" not in cmd or env.get("MW_BUILD_GATE_HOST", {}).get("value") != "/host/app":
        problems.append(f"{ch}: the gate host must be composed into /host/app (the composer removes its output; /host is a mount point)")
    if any(c["name"] == "postgres" for c in pod.get("containers", []) + pod.get("initContainers", [])):
        problems.append(f"{ch}: a shared Postgres container renders — a stage's database is disposable, inside its own sandbox")
    _vols = {v["name"]: v for v in pod.get("volumes", [])}
    if env.get("MW_BUILD_ARTIFACT_STORE", {}).get("value") != "file:/ci-artifacts" \
            or _vols.get("ci-artifacts", {}).get("persistentVolumeClaim", {}).get("claimName") != "ci-artifacts":
        problems.append(f"{ch}: the H9 artifact store is not wired (file:/ci-artifacts on its claim): {env.get('MW_BUILD_ARTIFACT_STORE')}")
    if env.get("MW_REGISTRY_URL", {}).get("value") != "https://portal.example.com" or env.get("MW_REGISTRY_KEY_FILE", {}).get("value") != "/run/registry-key/key":
        problems.append(f"{ch}: the registry bundle index (H8) is not wired: {env.get('MW_REGISTRY_URL')}")
    rk = vols.get("registry-key", {}).get("secret", {}) if (vols := {v["name"]: v for v in pod.get("volumes", [])}) else {}
    if rk.get("secretName") != "build-registry-key":
        problems.append(f"{ch}: the mwi_ instance key is not mounted as a file from its Secret: {rk}")
    vols = {v["name"]: v for v in pod.get("volumes", [])}
    if vols.get("cache", {}).get("persistentVolumeClaim", {}).get("claimName") != "build-cache":
        problems.append(f"{ch}: the cache is not the build-cache claim")
    wt = vols.get("worker-token", {}).get("secret", {})
    if wt.get("secretName") != "build-worker" or wt.get("items") != [{"key": "token", "path": "token"}]:
        problems.append(f"{ch}: the worker token is not the build-worker Secret mounted as a file: {wt}")
    secret_vols = sorted(v["name"] for v in pod.get("volumes", []) if "secret" in v)
    if secret_vols != ["registry-key", "worker-token"]:
        problems.append(f"{ch}: a host mounts no credential but the worker token; secret volumes: {secret_vols}")
    if any("bake" in v["name"] for v in pod.get("volumes", [])):
        problems.append(f"{ch}: a bake share is mounted")
    s = so["spec"]
    if s.get("minReplicaCount") != 0 or s.get("maxReplicaCount") != mx:
        problems.append(f"{ch}: ScaledObject min/max {s.get('minReplicaCount')}/{s.get('maxReplicaCount')} != 0/{mx}")
    if s.get("scaleTargetRef", {}).get("name") != f"build-host-{ch}":
        problems.append(f"{ch}: ScaledObject targets {s.get('scaleTargetRef')}")
    trig = [t for t in s.get("triggers", []) if t.get("type") == "metrics-api"]
    url = f"http://memex-portal-service:8080/api/builds/queue?channel={ch}"
    if len(trig) != 1 or trig[0]["metadata"].get("url") != url or trig[0]["metadata"].get("valueLocation") != "demand":
        problems.append(f"{ch}: not ONE metrics-api trigger on {url} valueLocation demand: {trig}")
    win = s.get("advanced", {}).get("horizontalPodAutoscalerConfig", {}).get("behavior", {}).get("scaleDown", {}).get("stabilizationWindowSeconds")
    if win != 300:
        problems.append(f"{ch}: scale-down stabilization window {win} != 300")
sa = find("ServiceAccount", "build-host")
if not sa or sa.get("automountServiceAccountToken") is not False:
    problems.append("ServiceAccount build-host missing or mounts a token")
elif (sa["metadata"].get("annotations") or {}).get("azure.workload.identity/client-id") != "00000000-0000-0000-0000-000000000001":
    problems.append("ServiceAccount build-host does not carry the workload identity's client id")
if find("ConfigMap", "memex-build-host-scripts"):
    problems.append("the chart renders a scripts ConfigMap — the runner ships in the image")
np = find("NetworkPolicy", "build-host")
if np:
    # The egress shape, pinned whole: exactly three rules.
    #   1. DNS (53/udp+tcp) to the cluster resolver ONLY;
    #   2. the build API's pods on their port ONLY;
    #   3. 80/443 to 0.0.0.0/0 EXCEPT every private range, link-local/IMDS and WireServer.
    egress = np["spec"].get("egress", [])
    if len(egress) != 3:
        problems.append(f"NetworkPolicy build-host has {len(egress)} egress rules, not the pinned 3")
    def ports(rule):
        return sorted((p.get("protocol"), p.get("port")) for p in rule.get("ports", []))
    dns = [r for r in egress if ports(r) == [("TCP", 53), ("UDP", 53)]]
    if not dns or not all(to.get("namespaceSelector", {}).get("matchLabels") == {"kubernetes.io/metadata.name": "kube-system"}
                          and to.get("podSelector", {}).get("matchLabels") == {"k8s-app": "kube-dns"}
                          for r in dns for to in (r.get("to") or [{}])):
        problems.append(f"DNS egress is not restricted to the cluster resolver: {dns}")
    api = [r for r in egress if ports(r) == [("TCP", 8080)]]
    if not api or any("ipBlock" in to for r in api for to in r.get("to", [])):
        problems.append(f"the build-API egress is not a pod selector on its port: {api}")
    web = [r for r in egress if ports(r) == [("TCP", 80), ("TCP", 443)]]
    blocks = [to.get("ipBlock") for r in web for to in r.get("to", [])]
    must = {"10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "168.63.129.16/32"}
    if len(blocks) != 1 or not blocks[0] or blocks[0].get("cidr") != "0.0.0.0/0" or not must <= set(blocks[0].get("except", [])):
        problems.append(f"the 80/443 egress does not exclude every private range, IMDS and WireServer: {blocks}")
    if any(not to for r in egress for to in r.get("to", [None])):
        problems.append("an egress rule has no destination (it would allow every address)")
if not np or np["spec"].get("ingress") != [] or "Ingress" not in np["spec"].get("policyTypes", []):
    problems.append("NetworkPolicy build-host missing or admits ingress")
if problems:
    print("\n".join(problems)); sys.exit(1)
PY
  then ok "the fixture renders both channels' hosts: tester/portal/runner digests, non-root, workload identity (AcrPull, host only), pool, token as a file, grace = run deadline, cache, readiness, KEDA 0..max on demand with a scale-down window, no other credential, no bake share, no SA token, no ingress"
  else report "the build-host fixture renders a wrong shape (see above)"
  fi
else
  report "the build-host fixture does not render:"; sed 's/^/    /' "$WORK/hosts.err"
fi

# 4. refusals
for entry in \
  "values.build-hosts-tag-not-digest.yaml|must be a sha256:<64 hex> digest" \
  "values.build-hosts-public-http.yaml|must be https, or http to a cluster-local host" \
  "values.build-hosts-registry-credential.yaml|a build host holds NO registry credential" \
  "values.build-hosts-postgres-sidecar.yaml|runs NO shared database" \
  "values.build-hosts-localhost-no-profile.yaml|names no profile"; do
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
