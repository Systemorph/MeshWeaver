#!/usr/bin/env python3
"""
The COMPARISON half of check-chart-drift.sh — rendered chart vs live objects.

Split out of that script so it can be exercised WITHOUT a cluster: the classification below is
real logic (which divergences are live-wrong, which are hygiene) and a gate whose logic is only
ever run against a private production cluster is a gate nobody can regression-test. Its self-test
is test-chart-drift-compare.sh, which chart-gate.yml runs on every pull request.

Arguments, in order:
  desired.yaml  live-configmap.json  live-deployment.json  expect-patch.json
  live-poddisruptionbudgets.json  live-scaledobjects.json  live-envfrom-source-keys.txt

The last file is `secret/<name><TAB>key` and `configmap/<name><TAB>key` lines — the NAMES of the
keys every OTHER envFrom source supplies (memex-portal-config is fetched in full separately), and
nothing else. No secret VALUE is read by this script, and none may be added to it.
Exit 0 = no drift, 1 = drift or the comparison could not be made.
"""
import json, sys, yaml

desired_path, live_cm_path, live_dep_path, expect_patch = sys.argv[1:5]
live_pdb_path, live_so_path = sys.argv[5:7]
# `<kind>/<name><TAB>key` lines. Key NAMES only — see the module docstring. A ConfigMap reaches the
# container through envFrom exactly as a Secret does, so an inline env shadows either identically;
# enumerating only the secrets would leave the same blind spot #3204 closed, one source-kind over.
envfrom_keys = {}
if len(sys.argv) > 7 and sys.argv[7]:
    for _line in open(sys.argv[7]):
        if "\t" in _line:
            _src, _key = _line.rstrip("\n").split("\t", 1)
            envfrom_keys.setdefault(_key, []).append(_src)

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
                f"live '{l_data[k]}' — edited on the cluster; a `helm upgrade` PRESERVES it "
                f"(measured), but it exists in no committed source, so it is lost on any rebuild "
                f"or restore and is invisible to review")
    elif d_data[k] != l_data[k]:
        finding("DIFFERS", f"ConfigMap {k}",
                f"chart '{d_data[k]}' vs live '{l_data[k]}' — a deploy does NOT resolve this "
                f"(measured); it persists until one side is made authoritative")

# ---- 2. inline env NAMES (values withheld — they hold tokens) --------------
# 🚨 The name alone does not say how bad an entry is. An inline env OVERRIDES envFrom, so an
# inline entry that DUPLICATES a key of the rendered ConfigMap silently kills the chart's value.
# The header explained that from the day this script was written; nothing here ever checked it,
# so every such entry was reported as a plain CLUSTER-ONLY and sorted in among dozens of harmless
# ones. That is why MeshWeaver#2355 read as an undifferentiated 60-entry backlog nobody owned.
#
# Two duplicate shapes, and only one is deterministic:
#   * SAME CASE      → the inline entry wins on every start. The ConfigMap value is dead.
#   * DIFFERING CASE → Linux keeps BOTH variables; .NET's provider is case-insensitive and the
#                      last enumerated wins, so it resolves at random PER POD START.
# Values are compared but NEVER printed (they hold tokens) — only the verdict is.
def env_entries(c):
    return {e["name"]: e for e in (c.get("env") or []) if "name" in e}

d_env_v, l_env_v = env_entries(d_c), env_entries(l_c)
d_env, l_env = set(d_env_v), set(l_env_v)
# literal values only: an entry with valueFrom has nothing to compare against a ConfigMap value
l_env_lit = {n: e["value"] for n, e in l_env_v.items() if "value" in e}
# ConfigMap keys indexed case-insensitively. BOTH sides matter and they answer different
# questions: the RENDERED map says whether the chart's value is the one being killed, the LIVE
# map says whether the ConfigMap the pod actually mounts is being killed. A key that is
# cluster-only in the ConfigMap AND set inline is still a dead ConfigMap entry — on memex today
# that is Features__Ai__Providers__AzureOpenAI, live 'false' under an inline 'true'.
def by_lower(d):
    out = {}
    for k in d:
        out.setdefault(k.lower(), []).append(k)
    return out
d_cm_by_lower, l_cm_by_lower = by_lower(d_data), by_lower(l_data)
# A key from ANY OTHER envFrom source — a Secret, or a second ConfigMap added through
# .Values.extraEnvFrom — reaches the container exactly like a memex-portal-config key, so an inline
# env of the same name shadows it identically. The Secret half was the case the ConfigMap-only
# comparison missed on memex (MeshWeaver#3201); the second-ConfigMap half is the same blind spot one
# source-kind over, and the chart documents `{configMapRef: {name: …}}` as a supported extraEnvFrom
# entry. Only the key NAMES of these sources are read, so their values are deliberately unknown here
# and such a shadow is reported WITHOUT an agree/disagree verdict rather than with a guessed one.
ext_by_lower = by_lower(envfrom_keys)

def env_shape(e):
    """The entry minus its literal value — a ref NAME is not a secret, a value may be."""
    return {k: v for k, v in e.items() if k != "value"}

for n in sorted(d_env | l_env):
    comparisons += 1
    if n not in l_env:
        finding("CHART-ONLY", f"inline env {n}", "rendered but not on the running pod spec")
        continue
    if n in d_env:
        # 🚨 On BOTH sides is NOT the same as agreeing, and this branch used to `continue` — so a
        # `kubectl set env` over an entry the chart renders inline was the one drift shape this
        # script could not see at all. It compared the NAMES and nothing else, having counted the
        # comparison. The chart renders five such entries today (the DOTNET_Dbg* crash-dump block
        # and the self-updater's AZURE_CLIENT_ID), and a wrong AZURE_CLIENT_ID silently breaks the
        # ACR credential the self-updater pulls with. Values are compared and NEVER printed.
        d_e, l_e = d_env_v[n], l_env_v[n]
        if env_shape(d_e) != env_shape(l_e):
            finding("DIFFERS", f"inline env {n}",
                    f"the chart renders {json.dumps(env_shape(d_e))} and the pod carries "
                    f"{json.dumps(env_shape(l_e))} — the entry's SHAPE differs (a literal against a "
                    f"valueFrom ref, or two different refs), so they cannot be the same setting")
        elif d_e.get("value") != l_e.get("value"):
            finding("DIFFERS", f"inline env {n}",
                    "the chart renders this entry AND the pod carries it, with DIFFERENT values "
                    "(withheld — an inline env can hold a token). helm owns this entry, so unlike "
                    "every other class here a `helm upgrade` DOES reset it; until one runs, the pod "
                    "runs the hand-set value and the chart's is not what is executing")
        continue
    # live-only inline env. Does it collide with a key that reaches the pod via envFrom?
    d_twins = d_cm_by_lower.get(n.lower(), [])
    l_twins = l_cm_by_lower.get(n.lower(), [])
    s_twins = ext_by_lower.get(n.lower(), [])
    twins = sorted(set(d_twins) | set(l_twins) | set(s_twins))
    if not twins:
        finding("CLUSTER-ONLY", f"inline env {n}",
                "set on the Deployment (`kubectl set env`?) and NOT in the chart. A `helm upgrade` "
                "PRESERVES it (measured), but it exists in no committed source — so it is lost on "
                "any rebuild and no reviewer can see it")
        continue
    # Where the twins come from, for the messages below. A twin from another envFrom source is NOT
    # a lesser case: its key reaches the container exactly like a memex-portal-config key, so it
    # collides and shadows identically. What differs is only that its VALUE is unknown here by
    # design — this script reads key names from those sources and nothing more.
    exts = sorted({src for k in s_twins for src in envfrom_keys.get(k, [])})
    sources = []
    if d_twins or l_twins:
        sources.append("ConfigMap key(s) " + ", ".join(sorted(set(d_twins) | set(l_twins))))
    if exts:
        sources.append("key(s) from " + " and ".join(exts))
    origin = " and ".join(sources)
    external_only = bool(s_twins) and not d_twins and not l_twins

    # Which ConfigMap value would the pod read if this inline entry were removed? The LIVE map is
    # what is mounted, so it decides the effective value; fall back to the render when the key is
    # rendered but not (yet) live.
    if n in l_data:
        shadowed_value, shadowed_src = l_data[n], "the live ConfigMap"
    elif n in d_data:
        shadowed_value, shadowed_src = d_data[n], "the rendered ConfigMap"
    else:
        shadowed_value, shadowed_src = None, None

    if n not in twins:            # every twin differs from this name only in case
        finding("COLLIDES", f"inline env {n}",
                f"{origin} — {', '.join(twins)} — reach this pod via envFrom and differ from this "
                f"one ONLY IN CASE. Linux env is case-sensitive so the pod carries BOTH; .NET "
                f"resolves case-insensitively and the last enumerated wins — the effective value is "
                f"a COIN TOSS PER POD START. Delete the inline entry (`kubectl set env deploy/… "
                f"{n}-`); adding it to the chart does NOT remove the collision")
    elif external_only:
        finding("SHADOWS", f"inline env {n}",
                f"{origin} supplies '{n}' through envFrom, and an inline env OVERRIDES envFrom — so "
                f"this entry wins and THAT source's value is dead. Whether the two agree is NOT "
                f"checked here (only key names are read from that source) and must not be assumed: "
                f"on memex they differed, leaving the pod on a plaintext copy while the Key Vault "
                f"credential went unused (MeshWeaver#3201). Establish which value is the live one "
                f"FIRST, then put it in that source and delete the inline entry")
    else:
        in_chart = n in d_data
        # An entry sourced with valueFrom (secretKeyRef/fieldRef) carries no literal to compare —
        # it still SHADOWS, but claiming the two disagree would be an invented fact.
        if n not in l_env_lit:
            verdict = ("its value comes from valueFrom (a secret/field ref), so the two cannot be "
                       "compared here — but the inline entry still wins and the ConfigMap value is "
                       "unused either way")
        elif l_env_lit.get(n) == shadowed_value:
            verdict = ("the two agree TODAY, so nothing is broken — but the ConfigMap is not what "
                       "the pod reads, and the next change to it will silently fail to take effect")
        else:
            verdict = (f"🚨 THE TWO DISAGREE: the pod runs the inline value and the value in "
                       f"{shadowed_src} is DEAD. Anyone reading "
                       + ("the chart, the values files or the ConfigMap"
                          if in_chart else "the ConfigMap")
                       + " is reading a setting no pod uses")
        where = ("the chart also renders ConfigMap key" if in_chart
                 else "the live ConfigMap also carries key")
        finding("SHADOWS", f"inline env {n}",
                f"{where} '{n}', which reaches the pod via envFrom — an inline env OVERRIDES "
                f"envFrom, so this entry wins. {verdict}. Fix by deleting the inline entry "
                f"(`kubectl set env deploy/… {n}-`) once the ConfigMap carries the intended value — "
                f"either step alone leaves the pod on the inline value")

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
        finding("CLUSTER-ONLY", label,
                f"live {json.dumps(lv)} — set on the cluster; a `helm upgrade` PRESERVES it "
                f"(measured), but it is in no committed source")
    else:
        note = ""
        # A liveness/readiness `initialDelaySeconds` is INERT whenever a startupProbe is present:
        # Kubernetes suspends both probes until the startup probe succeeds, so the delay can never
        # be what protects a slow boot. Saying so here is what stops the recurring, and wrong,
        # "live is authoritative — add initialDelaySeconds to the chart" conclusion on #2355.
        if (label in ("readinessProbe", "livenessProbe")
                and isinstance(dv, dict) and isinstance(lv, dict)
                and l_c.get("startupProbe")
                and {k: v for k, v in lv.items() if k != "initialDelaySeconds"} == dv):
            note = (" — the ONLY difference is initialDelaySeconds, and this container has a "
                    "startupProbe: Kubernetes suspends liveness AND readiness until that probe "
                    "passes, so the delay is inert. The chart is authoritative here; do NOT "
                    "'fix' this by adding initialDelaySeconds to the chart")
        finding("DIFFERS", label, f"chart {json.dumps(dv)} vs live {json.dumps(lv)}{note}")

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

# Worst first. COLLIDES and SHADOWS are live-wrong RIGHT NOW; the rest are hygiene that a deploy
# will not resolve either way. Ranking them the other way round is what made this report unreadable.
order = {"COLLIDES": 0, "SHADOWS": 1, "CLUSTER-ONLY": 2, "CHART-ONLY": 3, "DIFFERS": 4}
for kind, what, detail in sorted(findings, key=lambda f: (order[f[0]], f[1])):
    print(f"::error::{kind:<12} {what}")
    if detail:
        print(f"                 {detail}")
by_kind = {}
for kind, _, _ in findings:
    by_kind[kind] = by_kind.get(kind, 0) + 1
print(f"\n{len(findings)} divergence(s) across {comparisons} compared fields: "
      + ", ".join(f"{by_kind[k]} {k}" for k in sorted(by_kind, key=lambda k: order[k])))
print("")
print("🚨 COLLIDES     → LIVE NON-DETERMINISM, fix first. Delete the inline entry; the chart already")
print("                 feeds the key through envFrom. Adding it to the chart does NOT clear it.")
print("🚨 SHADOWS      → the chart's value is dead. Put the intended value in the chart, THEN delete")
print("                 the inline entry — either step alone leaves the pod on the inline value.")
print("CLUSTER-ONLY  → a `helm upgrade` does NOT drop it (measured); the risk is that it lives in no")
print("                 committed source. Move it onto the Deployment record + chart, then delete it.")
print("CHART-ONLY    → apply it: `helm upgrade` for chart-managed fields; nobody is getting it today.")
print("DIFFERS       → a deploy does NOT resolve it (measured) for a ConfigMap key or a probe field:")
print("                 decide which side is authoritative, then make the other match. The ONE")
print("                 exception is `inline env` — helm owns an entry it renders, so an upgrade")
print("                 DOES reset that one; the finding says so.")
sys.exit(1)
