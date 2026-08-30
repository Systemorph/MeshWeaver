# _audit.jq — the detection behind hosting-audit. Pure: manifest objects + live objects in, one
# LiveOnlyConfigReport out. Sourced by hosting-audit, never run on its own.
#
# Inputs (jq -n … -f _audit.jq):
#   $manifest[0]  the helm manifest + hooks as an ARRAY of Kubernetes objects (the truth)
#   $live[0]      every live object in the namespace, every kind, as ONE array
#   $namespace $release $deployment $container $revision $chart $auditedAt $skipped
#
# 🚨 NAMES, NEVER VALUES. A ConfigMap value is compared here and never emitted; an env entry's
# `value` decides whether it is a plain value and is never emitted. The report is written to a node
# and shown on a page — a value leaking through it would be the secret-in-the-pod-spec finding all
# over again, one level up.

def oname: "\(.kind)/\(.metadata.name)";
def owned: ((.metadata.ownerReferences // []) | length) > 0;
def csi_synced: (((.metadata.labels // {})["secrets-store.csi.k8s.io/managed"]) // "") == "true";

# What is NOT configuration anyone put on the cluster, and so is not a finding:
#   owned objects (ReplicaSets, CronJob-spawned Jobs, CSI-synced Secrets) exist because of their
#   owner; helm's own release records and ServiceAccount tokens are the machinery; kube-root-ca.crt
#   and the `default` ServiceAccount are created by Kubernetes in every namespace.
def excluded:
    owned
    or (.kind == "Secret" and ((.type // "") | (. == "helm.sh/release.v1" or . == "kubernetes.io/service-account-token")))
    or csi_synced
    or (.kind == "ConfigMap" and .metadata.name == "kube-root-ca.crt")
    or (.kind == "ServiceAccount" and .metadata.name == "default");

# A name that looks like it carries a secret. Name-based on purpose: the VALUE is what must not
# be read, so the name is the only honest signal. A flag that merely ends in Key is a false
# positive the record can rename; a token that slipped through would be an outage.
def secretish: test("token|secret|key|password"; "i");

# a minus b, as sorted unique names
def only(a; b): [a[] | select(. as $x | (b | index($x)) == null)] | unique;

# Kubernetes serialises quantities and ports as strings or numbers depending on who wrote them
# (`cpu: 4` in a template, `"4"` back from the API). Compare shapes, not spellings.
def norm: walk(if type == "number" then tostring else . end);
def compact: tojson | if length > 120 then .[:117] + "…" else . end;

def container($c): ((.spec.template.spec.containers // []) | map(select(.name == $c)) | .[0]);
def env_names: (.env // []) | map(.name);
def env_from: (.envFrom // []) | map(.secretRef.name // .configMapRef.name // empty);
def mounts: (.volumeMounts // []) | map(.mountPath);
def volumes: (.spec.template.spec.volumes // []) | map(.name);
def containers: ((.spec.template.spec.containers // []) | map(.name)) + ((.spec.template.spec.initContainers // []) | map(.name));
def annotations: (.spec.template.metadata.annotations // {}) | keys;

($manifest[0]) as $m
| ($live[0]) as $l
| ($m | map(oname)) as $managed
| ($m | map(select(.kind == "Deployment" and .metadata.name == $deployment)) | .[0]) as $md
| ($l | map(select(.kind == "Deployment" and .metadata.name == $deployment)) | .[0]) as $ld
| if $md == null then error("the manifest of release " + $release + " renders no Deployment/" + $deployment + " — pass --deployment naming the portal Deployment the chart renders") else . end
| if $ld == null then error("no live Deployment/" + $deployment + " in " + $namespace + " — the release is deployed but its portal is not running, which is a finding this audit cannot express as drift") else . end
| ($md | container($container)) as $mc
| ($ld | container($container)) as $lc
| if $mc == null then error("the manifest's Deployment/" + $deployment + " has no container named " + $container) else . end
| if $lc == null then error("the live Deployment/" + $deployment + " has no container named " + $container + " — a container renamed live is a finding this audit cannot express as drift") else . end

# ── the portal Deployment, field by field ──────────────────────────────────────────────────────
| only($lc | env_names; $mc | env_names) as $envLiveOnly
| only($mc | env_names; $lc | env_names) as $envManifestOnly
| only($lc | env_from; $mc | env_from) as $envFromLiveOnly
| only($ld | volumes; $md | volumes) as $volumesLiveOnly
| only($lc | mounts; $mc | mounts) as $mountsLiveOnly
| only($ld | containers; $md | containers) as $containersLiveOnly
| only($ld | annotations; $md | annotations) as $podAnnotationsLiveOnly

# replicas belong to the autoscaler when the manifest renders one for this Deployment — the chart
# then deliberately writes no replicas field, and comparing would blame KEDA for doing its job.
| ($m | map(select((.kind == "ScaledObject" or .kind == "HorizontalPodAutoscaler")
                   and (.spec.scaleTargetRef.name // "") == $deployment)) | length > 0) as $autoscaled
| ([
    (if $autoscaled then empty else
      {field: ".spec.replicas", live: ($ld.spec.replicas // 1), manifest: ($md.spec.replicas // 1)} end),
    {field: ".spec.template.spec.nodeSelector", live: ($ld.spec.template.spec.nodeSelector // {}), manifest: ($md.spec.template.spec.nodeSelector // {})},
    {field: ".spec.template.spec.tolerations", live: ($ld.spec.template.spec.tolerations // []), manifest: ($md.spec.template.spec.tolerations // [])},
    {field: ".spec.template.spec.affinity", live: ($ld.spec.template.spec.affinity // {}), manifest: ($md.spec.template.spec.affinity // {})},
    {field: ".spec.template.spec.serviceAccountName", live: ($ld.spec.template.spec.serviceAccountName // "default"), manifest: ($md.spec.template.spec.serviceAccountName // "default")},
    {field: ".spec.template.spec.terminationGracePeriodSeconds", live: ($ld.spec.template.spec.terminationGracePeriodSeconds // 30), manifest: ($md.spec.template.spec.terminationGracePeriodSeconds // 30)},
    {field: "containers[\($container)].resources", live: ($lc.resources // {}), manifest: ($mc.resources // {})},
    {field: "containers[\($container)].lifecycle", live: ($lc.lifecycle // {}), manifest: ($mc.lifecycle // {})},
    {field: "containers[\($container)].startupProbe.periodSeconds", live: ($lc.startupProbe.periodSeconds // 10), manifest: ($mc.startupProbe.periodSeconds // 10)},
    {field: "containers[\($container)].startupProbe.failureThreshold", live: ($lc.startupProbe.failureThreshold // 3), manifest: ($mc.startupProbe.failureThreshold // 3)}
  ]
  | map(select((.live | norm) != (.manifest | norm)))
  | map({field, live: (.live | compact), manifest: (.manifest | compact)})) as $podSpecDiffs

# ── the chart's ConfigMaps ─────────────────────────────────────────────────────────────────────
| ($m | map(select(.kind == "ConfigMap")) | map(
    .metadata.name as $n
    | (.data // {}) as $mdata
    | ($l | map(select(.kind == "ConfigMap" and .metadata.name == $n)) | .[0]) as $lcm
    | if $lcm == null then
        {name: $n, missingLive: true, liveOnlyKeys: [], manifestOnlyKeys: ($mdata | keys), differingKeys: []}
      else
        (($lcm.data // {}) as $ldata
        | {name: $n, missingLive: false,
           liveOnlyKeys: only($ldata | keys; $mdata | keys),
           manifestOnlyKeys: only($mdata | keys; $ldata | keys),
           differingKeys: ([($ldata | keys[]) | select(($mdata[.] != null) and ($ldata[.] != $mdata[.]))] | sort)})
      end)) as $configMaps
| ($configMaps | map((.liveOnlyKeys | length) + (.manifestOnlyKeys | length) + (.differingKeys | length)
                     + (if .missingLive then 1 else 0 end)) | add // 0) as $configMapCount

# ── objects the chart does not own ─────────────────────────────────────────────────────────────
| ($l | map(select(excluded | not))
      | map(select(oname as $n | ($managed | index($n)) == null))
      | group_by(.kind)
      | map({kind: .[0].kind, names: (map(.metadata.name) | sort | unique)})) as $unmanagedObjects
| ($unmanagedObjects | map(.names | length) | add // 0) as $unmanagedCount

# ── secrets the live pod reads that nobody renders ─────────────────────────────────────────────
| ([ ($ld.spec.template.spec.containers // [])[]
     | ((.envFrom // [])[] | .secretRef.name // empty), ((.env // [])[] | .valueFrom.secretKeyRef.name // empty) ]
   + [ ($ld.spec.template.spec.volumes // [])[] | .secret.secretName // empty ]
   | unique
   | map(select(. as $s | ($managed | index("Secret/" + $s)) == null))
   | map(select(. as $s | ($l | map(select(.kind == "Secret" and .metadata.name == $s and csi_synced)) | length) == 0))) as $unmanagedSecrets

# ── secret-shaped names carried as plain values ────────────────────────────────────────────────
| ([ ($ld.spec.template.spec.containers // [])[] | .name as $c
     | (.env // [])[] | select(.value != null) | select(.name | secretish) | "env:\($c):\(.name)" ]
   + [ $l[] | select(.kind == "ConfigMap") | select(oname as $n | ($managed | index($n)) != null)
       | .metadata.name as $n | ((.data // {}) | keys[]) | select(secretish) | "configmap:\($n)/\(.)" ]
   | unique) as $plainSecretEntries

# ── the verdict — derived from the lists, and re-derived by the mesh ───────────────────────────
| (($envLiveOnly | length) + ($envManifestOnly | length) + ($envFromLiveOnly | length)
   + ($volumesLiveOnly | length) + ($mountsLiveOnly | length) + ($containersLiveOnly | length)
   + ($podAnnotationsLiveOnly | length) + ($podSpecDiffs | length) + $configMapCount
   + $unmanagedCount + ($unmanagedSecrets | length) + ($plainSecretEntries | length)) as $findingCount
| {
    namespace: $namespace,
    release: $release,
    deploymentName: $deployment,
    containerName: $container,
    helmRevision: $revision,
    chartVersion: (if $chart == "" then null else $chart end),
    auditedAt: $auditedAt,
    managedObjectCount: ($managed | length),
    runningImage: ($lc.image // null),
    manifestImage: ($mc.image // null),
    skippedKinds: ($skipped | split(" ") | map(select(length > 0))),
    envLiveOnly: $envLiveOnly,
    envManifestOnly: $envManifestOnly,
    envFromLiveOnly: $envFromLiveOnly,
    volumesLiveOnly: $volumesLiveOnly,
    mountsLiveOnly: $mountsLiveOnly,
    containersLiveOnly: $containersLiveOnly,
    podAnnotationsLiveOnly: $podAnnotationsLiveOnly,
    podSpecDiffs: $podSpecDiffs,
    configMaps: $configMaps,
    unmanagedObjects: $unmanagedObjects,
    unmanagedSecrets: $unmanagedSecrets,
    plainSecretEntries: $plainSecretEntries,
    findingCount: $findingCount,
    verdict: (if $findingCount == 0 then "clean" else "drift" end)
  }
