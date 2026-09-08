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
        for d in load_all(pvc_path) + by_kind("PersistentVolumeClaim")
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
    "ContainerImages__CacheMaxBytes":
        "binds to a long (ContainerImageOptions.CacheMaxBytes, the read-through cache's byte "
        "budget). Absent leaves the image default (20 GiB); an empty string fails the binder at "
        "startup. Rendered only when containerImages.cacheMaxBytes is set.",
    "SelfUpdate__Registry":
        "binds to SelfUpdateOptions.Registry, whose default is the upstream ACR and whose value "
        "names the host of EVERY image the self-updater rolls to. Blank is not inert here — it is "
        "a roll to '/memex-portal-ai:<tag>'. Rendered only when selfUpdate.registry is set.",
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

# ---------------------------------------------------------------------------
# 11. The platform-image pull secret is on BOTH pods that pull a platform image, or on neither.
#
# MeshWeaver#3353: an installation that pulls from the mirror instead of ACR needs a
# kubernetes.io/dockerconfigjson credential on every pod that pulls memex-portal-ai or
# memex-migration. The chart renders `portal.imagePullSecret` onto the portal Deployment AND the
# migration Job; a template edit that dropped it from one would leave a Job in ImagePullBackOff
# while the portal rolls — and helm upgrade waits on that Job, so the deploy hangs on the one
# object nobody looks at. The two pod specs are asserted against each other, per render.
# ---------------------------------------------------------------------------
checks += 1
# The Job is named per release revision (memex-migration-<n>), so it is found by its component
# label, and its ABSENCE is a finding: the chart always renders one, so a render without it is not
# the shape this check understands, and "no Job to compare" must never read as "they agree".
migration = next(
    (d for d in by_kind("Job")
     if ((d.get("metadata") or {}).get("labels") or {}).get("app.kubernetes.io/component") == "memex-migration"),
    None)
if migration is None:
    finding(
        "the render contains no Job labelled app.kubernetes.io/component=memex-migration",
        "the chart always renders the migration Job (memex-migration/job.yaml), and invariant 11 "
        "compares its pod spec against the portal's — with no Job there is nothing to compare, "
        "which must not read as agreement.",
    )
else:
    _portal_pull = {s.get("name") for s in (pod.get("imagePullSecrets") or [])}
    _mig_pod = (((migration.get("spec") or {}).get("template") or {}).get("spec")) or {}
    _mig_pull = {s.get("name") for s in (_mig_pod.get("imagePullSecrets") or [])}
    if _portal_pull != _mig_pull:
        finding(
            f"imagePullSecrets differ between the portal Deployment ({sorted(_portal_pull) or 'none'}) "
            f"and the migration Job ({sorted(_mig_pull) or 'none'})",
            "both pull a platform image from the same registry (portal.imagePullSecret). The one "
            "without the credential is stuck in ImagePullBackOff while the other rolls — for the "
            "Job, that is a helm upgrade that never completes.",
        )

# ---------------------------------------------------------------------------
# 12. A chart-created PersistentVolumeClaim is sized, classed, kept, and actually mounted.
#
# MeshWeaver#3353: templates/memex-portal/pvc.yaml renders a claim for each persistence.<name>
# with `create: true`. Its `required` calls refuse a missing size or class at render time, so a
# rendered claim carrying neither can only mean the template lost them; `helm.sh/resource-policy:
# keep` is what stands between a helm uninstall and the data; and a claim the portal pod does not
# mount is storage nobody uses — the values entry that created it named a claimName the volumes
# block did not, i.e. the two halves of one entry disagree.
# ---------------------------------------------------------------------------
checks += 1
_mounted_claims = {
    v["persistentVolumeClaim"]["claimName"]
    for v in (pod.get("volumes") or [])
    if isinstance(v.get("persistentVolumeClaim"), dict)
    and v["persistentVolumeClaim"].get("claimName")
}
for _pvc in by_kind("PersistentVolumeClaim"):
    _pvc_name = (_pvc.get("metadata") or {}).get("name")
    _pvc_spec = _pvc.get("spec") or {}
    _size = ((_pvc_spec.get("resources") or {}).get("requests") or {}).get("storage")
    _keep = ((_pvc.get("metadata") or {}).get("annotations") or {}).get("helm.sh/resource-policy")
    if not _size or not _pvc_spec.get("storageClassName"):
        finding(
            f"chart-created PVC '{_pvc_name}' has no size or no storageClassName",
            "pvc.yaml marks both `required`; a rendered claim without them means the template "
            "changed shape. A claim of default size on the default class is a claim on the wrong tier.",
        )
    if _keep != "keep":
        finding(
            f"chart-created PVC '{_pvc_name}' is not annotated helm.sh/resource-policy: keep",
            "without it a helm uninstall — or `create` flipped back to false — deletes the claim and "
            "its data as a side effect of a release going away. Deleting data is an operator's "
            "explicit act.",
        )
    if _pvc_name not in _mounted_claims:
        finding(
            f"chart-created PVC '{_pvc_name}' is not mounted by the portal pod",
            "the persistence entry that created it names a claimName the volumes block does not use "
            "— the two halves of one entry disagree, and the storage is provisioned for nobody.",
        )

# ---------------------------------------------------------------------------
# 13. The bundle-fetch init container fills the shelf the portal reads, with the pod's credential.
#
# Doc/Architecture/PluginBundlesInTheRegistry: when `bundles.registry` is set, an init container
# pulls the sealed publications into `PreWarm__PrebuiltBundleRoot` before the portal starts. Four
# things must agree for that to do anything, and a template edit can silently break each:
#   * the init container and the portal container mount the SAME volume at the SAME path, and
#     that path IS the ConfigMap's PreWarm__PrebuiltBundleRoot — a fetch that lands where the
#     pre-warm does not look is an init container that exits 0 having achieved nothing;
#   * BUNDLES_ROOT (what the script writes under) equals that path;
#   * the projected docker config comes from the Secret named in the pod's imagePullSecrets —
#     the design's "one credential" is a fact about the render, not a comment;
#   * the script ConfigMap it mounts is rendered, and the pod template hashes it.
# Absent init container = the feature is off for this combination, which is legal; the checks
# run only when it is rendered, and the combination list guarantees one render has it.
# ---------------------------------------------------------------------------
checks += 1
_fetch = next((c for c in (pod.get("initContainers") or []) if c.get("name") == "bundle-fetch"), None)
if _fetch is not None:
    _root_cfg = str(cfg_data.get("PreWarm__PrebuiltBundleRoot", "")).strip()
    _env = {e.get("name"): e.get("value") for e in (_fetch.get("env") or [])}
    _fetch_mounts = {m.get("mountPath"): m.get("name") for m in (_fetch.get("volumeMounts") or [])}
    _portal_mounts = {m.get("mountPath"): m.get("name") for m in (portal.get("volumeMounts") or [])}
    _root_env = _env.get("BUNDLES_ROOT")
    if not _root_cfg:
        finding(
            "a bundle-fetch init container is rendered but PreWarm__PrebuiltBundleRoot is blank",
            "the init container fills a directory the pre-warm never reads. bundles.root defaults "
            "to that key; one of them must name the shelf.",
        )
    elif _root_env != _root_cfg:
        finding(
            f"bundle-fetch writes under BUNDLES_ROOT={_root_env!r} but the portal reads "
            f"PreWarm__PrebuiltBundleRoot={_root_cfg!r}",
            "the two must be one path, or the fetch lands where nothing looks.",
        )
    else:
        if _fetch_mounts.get(_root_cfg) != "memex-bundles":
            finding(
                f"bundle-fetch does not mount the 'memex-bundles' volume at {_root_cfg}",
                f"it mounts {sorted(_fetch_mounts.items())}; the shelf must be the volume the "
                f"portal reads at the same path, or the fetch is lost with the container.",
            )
        if _portal_mounts.get(_root_cfg) != "memex-bundles":
            finding(
                f"the portal container does not mount the 'memex-bundles' volume at {_root_cfg}",
                f"it mounts {sorted(_portal_mounts.items())}; the init container's fetch is "
                f"invisible to the pre-warm.",
            )
    _pull_names = {s.get("name") for s in (pod.get("imagePullSecrets") or [])}
    _volumes = {v.get("name"): v for v in (pod.get("volumes") or [])}
    _cred = ((_volumes.get("registry-pull-config") or {}).get("secret") or {})
    if _cred.get("secretName") not in _pull_names:
        finding(
            f"bundle-fetch's registry credential comes from Secret {_cred.get('secretName')!r}, "
            f"which is not among the pod's imagePullSecrets {sorted(_pull_names) or 'none'}",
            "the design is ONE credential: the Secret that pulls the platform image is the one "
            "that pulls the bundles. A different Secret is a second credential to provision and "
            "rotate, and a missing one is an init container that cannot authenticate.",
        )
    _items = {i.get("key"): i.get("path") for i in (_cred.get("items") or [])}
    if _items.get(".dockerconfigjson") != "config.json":
        finding(
            "the projected pull secret does not map '.dockerconfigjson' to 'config.json'",
            "ORAS reads a docker config.json; the kubernetes.io/dockerconfigjson Secret keeps it "
            "under the '.dockerconfigjson' key, so without the item mapping the file is not there.",
        )
    if _env.get("BUNDLES_REGISTRY_CONFIG", "").rsplit("/", 1)[0] not in _fetch_mounts \
            or _fetch_mounts.get(_env.get("BUNDLES_REGISTRY_CONFIG", "").rsplit("/", 1)[0]) != "registry-pull-config":
        finding(
            f"BUNDLES_REGISTRY_CONFIG={_env.get('BUNDLES_REGISTRY_CONFIG')!r} is not inside the "
            f"'registry-pull-config' mount",
            f"the script would look for the credential where nothing is mounted "
            f"(mounts: {sorted(_fetch_mounts.items())}).",
        )
    if not _env.get("BUNDLES_IDENTITY") and not _env.get("BUNDLES_IDENTITY_FILE"):
        finding(
            "bundle-fetch has neither BUNDLES_IDENTITY nor BUNDLES_IDENTITY_FILE",
            "the publication is addressed by framework identity; without one the script exits 1 "
            "on every boot.",
        )
    if not (_env.get("BUNDLES_SOURCES") or "").strip():
        finding(
            "bundle-fetch has an empty BUNDLES_SOURCES",
            "an init container that fetches nothing and exits 0 is the skip-trapdoor shape.",
        )
    _script = next(iter(by_kind("ConfigMap", "memex-bundle-fetch")), None)
    if _script is None or not ((_script.get("data") or {}).get("bundle-fetch.sh") or "").strip():
        finding(
            "the memex-bundle-fetch ConfigMap is absent or carries no bundle-fetch.sh",
            "the init container mounts it; without it the pod never leaves ContainerCreating.",
        )
    _annotations = ((spec.get("template") or {}).get("metadata") or {}).get("annotations") or {}
    if not _annotations.get("checksum/bundle-fetch"):
        finding(
            "the pod template carries no checksum/bundle-fetch annotation",
            "an edit to the fetch script would then roll nothing — the same shape checksum/config "
            "exists to prevent.",
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
