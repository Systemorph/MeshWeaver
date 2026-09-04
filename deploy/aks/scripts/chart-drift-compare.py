#!/usr/bin/env python3
"""
The COMPARISON half of check-chart-drift.sh — rendered chart vs live objects vs release manifest.

Split out of that script so it can be exercised WITHOUT a cluster: the classification below is
real logic (which divergences are live-wrong, which are hygiene) and a gate whose logic is only
ever run against a private production cluster is a gate nobody can regression-test. Its self-test
is test-chart-drift-compare.sh, which chart-gate.yml runs on every pull request.

🚨 THREE SIDES, not two. D = the chart as CI renders it (`helm template`). L = the live cluster
objects. M = the last-deployed release manifest (`helm get manifest <release> -n <ns>`), which is
the `original` side of helm's three-way merge — exactly what helm PREVIOUSLY OWNED. D vs L is the
drift everybody talks about, and it is the comparison this file used to make alone. The deletion
hazard is not in it: it lives in D vs M. A key that is in L and in M but NOT in D is a chart
RETIREMENT that has not landed yet — helm owned it, the current chart no longer renders it, so the
next `helm upgrade` WILL remove it. That is the ONE cluster-only shape a deploy actually destroys;
everything else survives a deploy (measured 2026-09-03 on helm v3.21.1 and v4.2.4, with a positive
control). Until this comparison existed the two halves were reported identically, and the split had
to be re-derived by hand every run — 13 owned-but-retired out of 36 cluster-only findings on
2026-09-04, which nothing recomputed on the next one. `Systemorph/Memex#152` asked for this check.

Arguments, in order:
  desired.yaml  live-configmap.json  live-deployment.json  expect-patch.json
  live-poddisruptionbudgets.json  live-scaledobjects.json  live-envfrom-source-keys.txt
  release-manifest.yaml

The envFrom file is `secret/<name><TAB>key` and `configmap/<name><TAB>key` lines — the NAMES of the
keys every OTHER envFrom source supplies (memex-portal-config is fetched in full separately), and
nothing else. No secret VALUE is read by this script, and none may be added to it.
The release manifest is REQUIRED, and its absence is a FAILURE rather than a silent "no pending
deletions": a checker that answers "everything cluster-only survives" because it could not read the
manifest is a gate that passed on missing input.
Exit 0 = no drift, 1 = drift or the comparison could not be made.
"""
import json, os, sys, yaml

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

# 🚨 FAIL CLOSED on the manifest. It is the `original` side of the merge and the ONLY thing that
# distinguishes a cluster-only key helm still owns (a deploy DELETES it) from one nobody owns (a
# deploy leaves it). Without it every finding would read as the harmless half, which is the exact
# shape of a gate that passes because it could not read its input.
MANIFEST_HINT = "`helm get manifest <release> -n <namespace>`"
manifest_path = sys.argv[8] if len(sys.argv) > 8 else ""
if not manifest_path:
    print(f"::error::no release manifest was passed ({MANIFEST_HINT}). Without it the CLUSTER-ONLY "
          f"class cannot be split into 'helm still OWNS this, so the next upgrade DELETES it' and "
          f"'nobody owns it, so it survives' — and reporting every cluster-only key as surviving "
          f"would hide the one shape a deploy does destroy. Treating as FAILURE.")
    sys.exit(1)
if not os.path.exists(manifest_path):
    print(f"::error::the release manifest '{manifest_path}' does not exist. It is the output of "
          f"{MANIFEST_HINT} and it is a required input, not an optional one — see above. Treating "
          f"as FAILURE.")
    sys.exit(1)

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

# ---- owned: what the LAST deploy rendered ----------------------------------
# Parsed exactly like desired.yaml, because it IS the same shape — `helm get manifest` returns the
# release's stored render. Missing objects are a FAILURE for the same reason an unreadable manifest
# is: an empty M makes every live-only key look never-owned, i.e. harmless, which is the answer this
# comparison exists to stop being given by default.
m_cm = m_dep = None
for doc in yaml.safe_load_all(open(manifest_path)):
    if not doc:
        continue
    name = (doc.get("metadata") or {}).get("name")
    if doc.get("kind") == "ConfigMap" and name == "memex-portal-config":
        m_cm = doc
    if doc.get("kind") == "Deployment" and name == "memex-portal-deployment":
        m_dep = doc

if m_cm is None or m_dep is None:
    print(f"::error::the release manifest '{manifest_path}' ({MANIFEST_HINT}) contains no "
          f"memex-portal-config ConfigMap and/or no memex-portal-deployment Deployment. helm's "
          f"ownership of the live keys could not be determined, so 'this key survives a deploy' "
          f"could not be established for a single finding. Treating as FAILURE.")
    sys.exit(1)

m_c = portal_container(m_dep)
if m_c is None:
    print(f"::error::no container named 'memex-portal' in the Deployment of the release manifest "
          f"'{manifest_path}' — helm's ownership of the live inline env entries could not be "
          f"determined. Treating as FAILURE.")
    sys.exit(1)

m_data = m_cm.get("data") or {}
m_env = {e["name"] for e in (m_c.get("env") or []) if "name" in e}

# Findings whose subject helm OWNS and the chart no longer renders. Tracked as a list rather than
# sniffed back out of the message text, so the summary count cannot drift from the findings.
pending_deletions = []

def deletion_weight(value):
    """How much a pending deletion actually removes. The VALUE decides that, not the key name —
    all 13 owned-but-retired keys found on 2026-09-04 were zero-length, and reading only the names
    would have raised an alarm about deleting nothing. The value itself is never printed here."""
    if value is None:
        return ("Its live value comes from a valueFrom ref, so what the removal takes away cannot "
                "be read here — resolve the ref before deciding.")
    if value == "":
        return ("Its live value is EMPTY (zero-length), so the removal itself is a NO-OP: nothing "
                "the pod reads changes.")
    return (f"Its live value is NON-EMPTY ({len(value)} characters), so the removal is a REAL "
            f"change to what the pod reads.")

def pending_deletion(what, weight, subject):
    """The one cluster-only shape a `helm upgrade` destroys — D vs M, not D vs L."""
    pending_deletions.append(what)
    return (f"🚨 PENDING DELETION: {subject} is in the release manifest ({MANIFEST_HINT}), so helm "
            f"OWNS it, and the current chart no longer renders it. helm's three-way merge removes "
            f"exactly what it previously owned, so the next `helm upgrade` WILL remove this — the "
            f"ONE cluster-only shape a deploy does destroy. {weight} Decide before the next deploy: "
            f"put it back in the chart, or retire it on purpose.")

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
        # The class splits HERE, on manifest membership — see the module docstring. Both halves are
        # "live and not rendered"; only one of them is one deploy away from being gone.
        if k in m_data:
            finding("CLUSTER-ONLY", f"ConfigMap {k}",
                    pending_deletion(f"ConfigMap {k}", deletion_weight(l_data[k]),
                                     "this ConfigMap key"))
        else:
            finding("CLUSTER-ONLY", f"ConfigMap {k}",
                    f"live '{l_data[k]}' — edited on the cluster; helm never owned it (it is NOT "
                    f"in the release manifest), so a `helm upgrade` PRESERVES it (measured), but "
                    f"it exists in no committed source, so it is lost on any rebuild or restore "
                    f"and is invisible to review")
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
        # Same split as the ConfigMap branch above, over the manifest's OWN inline env entries: an
        # entry the last deploy rendered and this chart does not is helm's to delete. The literal is
        # consulted only for its emptiness — an inline env can hold a token and none is printed.
        if n in m_env:
            finding("CLUSTER-ONLY", f"inline env {n}",
                    pending_deletion(f"inline env {n}",
                                     deletion_weight(l_env_lit.get(n)), "this inline env entry"))
        else:
            finding("CLUSTER-ONLY", f"inline env {n}",
                    "set on the Deployment (`kubectl set env`?) and NOT in the chart. helm never "
                    "owned it (it is NOT in the release manifest), so a `helm upgrade` PRESERVES "
                    "it (measured), but it exists in no committed source — so it is lost on any "
                    "rebuild and no reviewer can see it")
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
        # 🚨 "Which value is live?" is the wrong question for a CREDENTIAL, and this message used
        # to ask only that — while also asserting a Key Vault provenance this script cannot see.
        # Measured on memex 2026-09-04 (MeshWeaver#3201): the shadowed side was NOT a stale copy of
        # the same secret. Both sides were VALID registry instance keys, of DIFFERENT registered
        # instances, with different entitlements — so the cleanup everyone reaches for first would
        # have re-identified the portal, not merely changed a value. A name is all this script
        # reads; it must not imply the two sides are the same credential, nor say where either came
        # from.
        finding("SHADOWS", f"inline env {n}",
                f"{origin} supplies '{n}' through envFrom, and an inline env OVERRIDES envFrom — so "
                f"this entry wins and THAT source's value is dead. Whether the two agree is NOT "
                f"checked here (only key names are read from that source) and must not be assumed — "
                f"and for a CREDENTIAL they may not even be the same secret: on memex both sides "
                f"were valid keys of DIFFERENT registered instances, so deleting the inline entry "
                f"would have changed which principal the deployment authenticates as, and silently "
                f"widened what it was entitled to (MeshWeaver#3201, measured 2026-09-04). Establish "
                f"what EACH side IS — its value, and for a credential the identity it authenticates "
                f"as — FIRST, then put the intended one in that source and delete the inline entry")
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
# The count that used to be a hand measurement. 13 of 36 on 2026-09-04, re-derived by nobody on the
# next run — a number a report does not carry is a number that goes stale the day it is written.
if pending_deletions:
    print(f"🚨 {len(pending_deletions)} of the CLUSTER-ONLY finding(s) are PENDING DELETIONS — helm "
          f"owns them and this chart no longer renders them, so the next `helm upgrade` removes "
          f"them: " + ", ".join(sorted(pending_deletions)))
else:
    print("0 pending deletions: no live-and-unrendered key is in the release manifest, so nothing "
          "here is removed by the next `helm upgrade`.")
print("")
print("🚨 COLLIDES     → LIVE NON-DETERMINISM, fix first. Delete the inline entry; the chart already")
print("                 feeds the key through envFrom. Adding it to the chart does NOT clear it.")
print("🚨 SHADOWS      → the chart's value is dead. Put the intended value in the chart, THEN delete")
print("                 the inline entry — either step alone leaves the pod on the inline value.")
print("CLUSTER-ONLY  → TWO halves, and the finding says which. NEVER-OWNED (not in the release")
print("                 manifest): a `helm upgrade` does NOT drop it (measured); the risk is that it")
print("                 lives in no committed source. Move it onto the Deployment record + chart,")
print("                 then delete it. 🚨 PENDING DELETION (in the manifest, so helm owns it, and")
print("                 this chart no longer renders it): the next `helm upgrade` REMOVES it — the")
print("                 one cluster-only shape a deploy destroys. The finding says whether the live")
print("                 value is empty (the removal is a no-op) or not (it is a real removal).")
print("CHART-ONLY    → apply it: `helm upgrade` for chart-managed fields; nobody is getting it today.")
print("DIFFERS       → a deploy does NOT resolve it (measured) for a ConfigMap key or a probe field:")
print("                 decide which side is authoritative, then make the other match. The ONE")
print("                 exception is `inline env` — helm owns an entry it renders, so an upgrade")
print("                 DOES reset that one; the finding says so.")
sys.exit(1)
