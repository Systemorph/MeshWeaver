#!/usr/bin/env python3
"""Assert that ONE rendered manifest set describes a deployment that can actually work.

Driven by check-chart-invariants.sh (see that file for why this exists). Reads a rendered
`helm template` stream plus the PVC manifest, and reports every contradiction it finds —
never just the first, so one run tells you the whole story.

Exit 0 = self-consistent. Exit 1 = at least one contradiction, or too little was checked.
"""
import sys

import yaml

name, rendered_path, pvc_path = sys.argv[1:4]

findings = []
checks = 0


def finding(what, detail):
    findings.append((what, detail))


def load_all(path):
    with open(path) as fh:
        return [d for d in yaml.safe_load_all(fh) if d]


docs = load_all(rendered_path)


def by_kind(kind, obj_name=None):
    out = [
        d for d in docs
        if d.get("kind") == kind
        and (obj_name is None or (d.get("metadata") or {}).get("name") == obj_name)
    ]
    return out


dep = next(iter(by_kind("Deployment", "memex-portal-deployment")), None)
if dep is None:
    print(
        f"::error::[{name}] the render contains no memex-portal-deployment. Nothing could be "
        f"checked — treating as FAILURE, because 'checked nothing' must never read as "
        f"'found no contradiction'."
    )
    sys.exit(1)

spec = dep.get("spec") or {}
pod = ((spec.get("template") or {}).get("spec")) or {}
containers = pod.get("containers") or []
portal = next((c for c in containers if c.get("name") == "memex-portal"), None)
if portal is None:
    print(f"::error::[{name}] no container named 'memex-portal' in the rendered Deployment — FAILURE.")
    sys.exit(1)

scaled = next(iter(by_kind("ScaledObject", "memex-portal-scaler")), None)
pdb = next(iter(by_kind("PodDisruptionBudget", "memex-portal-pdb")), None)
cfg = next(iter(by_kind("ConfigMap", "memex-portal-config")), None)
secret = next(iter(by_kind("Secret", "memex-portal-secrets")), None)

chart_replicas = spec.get("replicas")
scaled_min = ((scaled or {}).get("spec") or {}).get("minReplicaCount")

# The floor that will actually apply once the objects are live.
floor = scaled_min if scaled is not None else (chart_replicas if chart_replicas is not None else 1)

# ---- 1. helm and the HPA must not both own spec.replicas -------------------
checks += 1
if scaled is not None and chart_replicas is not None:
    finding(
        "spec.replicas is rendered alongside a ScaledObject",
        f"the chart sets replicas={chart_replicas} while KEDA's HPA also owns that field. Every "
        f"`helm upgrade` would yank a scaled-out deployment back to {chart_replicas} until the HPA "
        f"pushed it out again. Omit spec.replicas when keda.enabled.",
    )

# ---- 2. more than one pod means more than one Orleans silo -----------------
checks += 1
clustering = ((cfg or {}).get("data") or {}).get("Deployment__Orleans__Clustering", "Localhost")
if floor > 1 and clustering.lower() == "localhost":
    finding(
        f"replica floor is {floor} but Orleans clustering is 'Localhost'",
        "Localhost clustering is single-process membership — the replicas do not form ONE mesh, "
        "they form one mesh EACH, and a grain call resolves against whichever pod the request "
        "landed on. Set config.memex_portal.Deployment__Orleans__Clustering to AdoNet (or "
        "AzureTables) in the same change that raises the floor.",
    )

# ---- 3. more than one pod means more than one writer per volume ------------
checks += 1
if floor > 1:
    claims = {
        v["persistentVolumeClaim"]["claimName"]
        for v in (pod.get("volumes") or [])
        if isinstance(v.get("persistentVolumeClaim"), dict)
        and v["persistentVolumeClaim"].get("claimName")
    }
    modes = {
        (d.get("metadata") or {}).get("name"): (d.get("spec") or {}).get("accessModes") or []
        for d in load_all(pvc_path)
        if d.get("kind") == "PersistentVolumeClaim"
    }
    for claim in sorted(claims):
        if claim not in modes:
            # The chart mounts claims it does not create; an env may provision them elsewhere.
            # Not a contradiction in the chart — but say so, rather than silently passing it.
            print(
                f"  note [{name}]: claim '{claim}' is mounted but not declared in "
                f"{pvc_path} — its access mode could not be checked here."
            )
            continue
        if "ReadWriteMany" not in modes[claim]:
            finding(
                f"replica floor is {floor} but PVC '{claim}' is {'/'.join(modes[claim])}",
                "every claim the portal mounts is SHARED state across replicas (/data holds the "
                "DataProtection key ring, the NodeType assembly cache and the NuGet cache). A "
                "ReadWriteOnce claim either pins both pods to one node or leaves the second "
                "unschedulable. Use ReadWriteMany (azurefile-memex).",
            )

# ---- 4. AdoNet needs a membership connection string ------------------------
checks += 1
if clustering.lower() == "adonet":
    keys = (secret or {}).get("stringData") or {}
    if not keys.get("ConnectionStrings__orleans"):
        finding(
            "Orleans clustering is 'AdoNet' but no ConnectionStrings__orleans is rendered",
            "the silo throws at startup ('Features:Orleans:Clustering=AdoNet but "
            "ConnectionStrings:orleans is not set') and the namespace has no portal at all.",
        )

# ---- 5./6. the disruption budget --------------------------------------------
checks += 1
if pdb is not None:
    pdb_spec = pdb.get("spec") or {}
    if "minAvailable" in pdb_spec:
        finding(
            f"the PodDisruptionBudget uses minAvailable: {pdb_spec['minAvailable']}",
            "minAvailable is not scale-invariant — it has to be re-tuned every time the replica "
            "floor moves, and set equal to the floor it allows ZERO voluntary disruptions "
            "(disruptionsAllowed: 0), which silently blocks every node image-upgrade and drain. "
            "Use maxUnavailable: 1 — exactly one pod at a time, whether KEDA holds 2 or bursts "
            "to 8.",
        )
    checks += 1
    if floor < 2:
        finding(
            f"a PodDisruptionBudget is rendered but the replica floor is {floor}",
            "over a single pod a budget can only do harm: minAvailable blocks all voluntary "
            "disruption for ever, and maxUnavailable makes the ONLY serving pod evictable. Raise "
            "the floor in the same change that introduces the budget.",
        )

# ---- 7. surge-first rolls ---------------------------------------------------
checks += 1
if scaled is not None:
    rolling = ((spec.get("strategy") or {}).get("rollingUpdate")) or {}
    if rolling.get("maxUnavailable") != 0:
        finding(
            f"strategy.rollingUpdate.maxUnavailable is {rolling.get('maxUnavailable')!r}, not 0",
            "0 is what makes a roll surge-first: the old pod keeps serving until the new one "
            "passes its probes. Above 0 a serving pod can be deleted before its replacement is "
            "ready, which also makes the NodeType bake gate (PreWarm__GateReadiness) report "
            "without protecting anything.",
        )

# ---------------------------------------------------------------------------
# 8. Every key the PLATFORM must let a deployment set is actually RENDERED.
#
# 🚨 This is the `replicas.portal: 2` defect in its other direction, and it is the one this
# ConfigMap invites: the template names every key EXPLICITLY and the Deployment's only env path is
# `envFrom` on it, so a key the template omits is consumed by nothing — a values file can set it in
# three environments and no container ever sees it. helm reports success either way, because the
# values file is syntactically perfect and the template simply never mentions it.
#
# That is not hypothetical. Memex#53 set `config.memex_portal.Modules__Root: /data` in all three
# AKS values files, titled "Modules__Root belongs in the chart — that is why it kept vanishing".
# It kept vanishing because it was never templated here.
#
# So: for each key below, the rendered ConfigMap must CARRY it and its value must be non-blank.
# Blank matters as much as absent for any key whose consumer treats blank as unset — ModuleRoot
# does exactly that and falls back to AppContext.BaseDirectory (/app), which is READ-ONLY in the
# container, so a blank key silently restores the very defect the key exists to fix.
REQUIRED_CONFIG = {
    "Modules__Root":
        "the writable root the store-installed modules/ tree and its activation.json sidecar are "
        "written under (ModuleRoot.ConfigKey). Unset or blank resolves to AppContext.BaseDirectory "
        "— /app, read-only in the container — so nothing can be store-activated and every "
        "module-contributed route (/mcp among them) stays unmapped.",
}

cfg_data = (cfg or {}).get("data") or {}
for key, why in REQUIRED_CONFIG.items():
    checks += 1
    if key not in cfg_data:
        finding(
            f"the rendered memex-portal-config carries no '{key}'",
            f"{why} The ConfigMap names every key explicitly and the Deployment reads it via "
            f"envFrom, so an un-templated key reaches no container — setting it in a values file "
            f"changes nothing, silently.",
        )
    elif not str(cfg_data[key]).strip():
        finding(
            f"'{key}' renders BLANK in memex-portal-config",
            f"{why} Give it a value in the template (defaulting to another key is fine) rather "
            f"than emitting an empty string.",
        )

# ---------------------------------------------------------------------------
# 9. A key whose consumer does NOT read it as a string must never render BLANK.
#
# 🚨 The mirror of 8, and the more dangerous half: 8 catches a key that reaches no container, this
# catches one that reaches every container carrying a value that cannot be parsed. For a string,
# blank and absent mean the same thing — that is why `default ""` is the file's usual idiom. For an
# enum, an int or a bool they are opposites: ABSENT leaves the binder at the type's default, BLANK
# throws, and the throw happens during DI activation rather than at config load, so it surfaces
# somewhere else entirely.
#
# WebSearch__Provider did this to memex.systemorph.com on 2026-08-20. It binds to the enum
# WebSearchProviderType (default None = auto-detect); the template emitted it with `default ""`;
# "" is not a member, so WebSearchPlugin's constructor threw FormatException. Because
# ChatClientAgentFactory resolves plugins with GetServices<IAgentPlugin>(), which activates the
# whole array, that one plugin removed EVERY custom tool from EVERY agent — and the log blamed the
# agent it was resolving at the time, not the plugin that threw. Two days, ~100 failures/24h, and
# the two namespaces that still worked were simply the ones the chart had never rendered.
#
# The chart already knew the rule — OpenAICompatible__DiscoverModels carries a note saying
# `default "false"`, never `default ""`, because Boolean.Parse fails on empty (issue #352) — and
# the WebSearch block violated it forty lines further down. A rule stated in a comment is not a
# gate; this is the gate.
#
# Absent is ACCEPTED here (that is the fix's whole point). Only a rendered-but-blank value fails.
NEVER_BLANK_CONFIG = {
    "WebSearch__Provider":
        "binds to the enum WebSearchProviderType (WebSearchConfiguration.Provider). Its default, "
        "None, means auto-detect, so LEAVING THE KEY OUT is correct for an instance with no web "
        "search configured. An empty string is not a member of the enum and throws FormatException "
        "while WebSearchPlugin is being constructed — which, via GetServices<IAgentPlugin>(), "
        "removes every custom tool from every agent.",
    "OpenAICompatible__DiscoverModels":
        "read with GetValue<bool> — Boolean.Parse throws on an empty string and takes host startup "
        "with it (issue #352). Emit \"false\", never \"\".",
    "Anthropic__Order":
        "read as an Int32 — empty fails the binder. Emit \"0\", never \"\".",
    "AzureAIS__Order":
        "read as an Int32 — empty fails the binder. Emit \"0\", never \"\".",
}

for key, why in NEVER_BLANK_CONFIG.items():
    checks += 1
    if key in cfg_data and not str(cfg_data[key]).strip():
        finding(
            f"'{key}' renders BLANK in memex-portal-config",
            f"{why} Guard the line so the key is emitted only when a value is set "
            f"({{{{- if .Values.config.memex_portal.{key} }}}}), or give it a PARSEABLE default — "
            f"never `default \"\"`. Absent is fine here; blank is not.",
        )

# ---------------------------------------------------------------------------
# 10. Readiness and liveness must not probe the SAME path.
#
# 🚨 MeshWeaver#3330. Both post-startup probes were `/alive`, which was harmless only while
# /alive was the trivial process-up check core ships. MeshWeaver.Plugins#1234 registered a
# progress-aware handler on the tag /alive filters on (merged 2026-09-03), and READINESS inherited
# it silently — two probes cannot be given different semantics while they share a path.
#
# The arithmetic is what makes it a cascade rather than a blip: readiness trips at 10s x 3 = 30s,
# liveness at 15s x 6 = 90s. So a GC-bound replica LEFT THE SERVICE a full minute before anything
# restarted it, and for that minute its traffic landed on siblings converging on the same memory
# ceiling with age (measured 2026-09-04, ns memex: two 28h replicas at 9936Mi and 9409Mi, ratio
# 1.06). That is the 2026-07-21 death spiral rebuilt out of the fix that was meant to prevent it.
#
# Checked on the RENDERED object, per values combination, because that is where an overlay can
# re-merge what the template separates — a `helm template` is perfectly happy to emit two probes
# asking one question.
checks += 1
_probe_paths = {
    which: ((portal.get(which) or {}).get("httpGet") or {}).get("path")
    for which in ("startupProbe", "readinessProbe", "livenessProbe")
}
if not _probe_paths["readinessProbe"] or not _probe_paths["livenessProbe"]:
    finding(
        "the rendered portal container is missing a readinessProbe or livenessProbe httpGet path",
        "both are load-bearing and this invariant cannot be evaluated without them — a pod with no "
        "readiness probe is put in rotation the instant it starts, and one with no liveness probe "
        "is never restarted when it wedges.",
    )
elif _probe_paths["readinessProbe"] == _probe_paths["livenessProbe"]:
    finding(
        f"readinessProbe and livenessProbe both probe {_probe_paths['readinessProbe']}",
        "they cannot then be given different semantics. The moment that path answers a "
        "progress-aware verdict (ProcessProgressHealthCheck, MeshWeaver.Plugins#1234), a GC-bound "
        "replica is EVICTED at 30s and only RESTARTED at 90s, and for the minute in between its "
        "traffic lands on siblings converging on the same memory ceiling — one sick replica becomes "
        "a cascade (MeshWeaver#3330). Readiness answers 'can I take a request', liveness answers "
        "'am I making progress': separate paths, separate health-check tags.",
    )
elif _probe_paths["readinessProbe"] == _probe_paths["startupProbe"]:
    finding(
        f"readinessProbe probes the startupProbe's path {_probe_paths['startupProbe']}",
        "that path runs EVERY registered health check — the database and the mesh included. Under "
        "load a heavy readiness check times out, the pod is yanked from the Service endpoints, and "
        "the survivors inherit its traffic: the 2026-07-21 death spiral. The startup probe already "
        "holds readiness on the heavy path until the mesh is up; after that readiness must be cheap.",
    )

MIN_CHECKS = 5
if checks < MIN_CHECKS:
    print(
        f"::error::[{name}] only {checks} invariants were evaluated (expected at least "
        f"{MIN_CHECKS}) — the render is not the shape this check understands. Treating as FAILURE."
    )
    sys.exit(1)

if not findings:
    sys.exit(0)

for what, detail in findings:
    print(f"::error::[{name}] {what}")
    print(f"                 {detail}")
print(f"\n[{name}] {len(findings)} contradiction(s) across {checks} invariants.")
sys.exit(1)
