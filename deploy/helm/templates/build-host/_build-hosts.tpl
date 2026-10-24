{{- /*
The standing build hosts (Doc/Architecture/BuildHosts; MeshWeaver.Plugins Hosting/StandingBuildHosts):
one warm Deployment + KEDA ScaledObject per release channel, rendered on the operations instance's
record only.

🚨 THIS HELPER IS THE GATE, the same way `memex.artifactStore` is for the store's jobs: the
templates read `buildHosts.*` only through it, so a half-declared block fails `helm template` HERE
naming the key — never a host that renders, starts, and claims runs on a compiler nobody named.
With `buildHosts.enabled` false (the default) it answers `enabled: false` and NOTHING renders, so
every estate that has not opted in renders byte-identically.

The refusals, and why each one exists:
  * a channel's tester/portal/runner must be a sha256 DIGEST — a run records the set that compiled it,
    and a tag can name different bytes tomorrow;
  * a channel needs its platformVersion — `claim` stamps it on the run as the host's set;
  * the API URL must be https, or http to a cluster-local host (no dot, or *.svc / *.cluster.local)
    — the worker token rides on it;
  * at least one channel must be enabled — an enabled block that renders no host is a typo.
*/ -}}
{{- define "memex.buildHosts" -}}
{{- $b := (.Values.buildHosts | default dict) -}}
{{- if not $b.enabled -}}
{{- toYaml (dict "enabled" false) -}}
{{- else -}}
{{- $api := required "buildHosts.apiUrl is required when buildHosts.enabled — the operations instance's build API the hosts claim from (in-cluster, e.g. http://memex-portal-service:8080/api/builds)" $b.apiUrl -}}
{{- $api = trimSuffix "/" $api -}}
{{- $host := regexReplaceAll "^[a-z]+://([^/:]+).*$" $api "${1}" -}}
{{- $local := or (not (contains "." $host)) (hasSuffix ".svc" $host) (hasSuffix ".cluster.local" $host) -}}
{{- if not (or (hasPrefix "https://" $api) (and (hasPrefix "http://" $api) $local)) -}}
{{- fail (printf "buildHosts.apiUrl %q must be https, or http to a cluster-local host — the worker token rides on it" $api) -}}
{{- end -}}
{{- $runnerRepo := required "buildHosts.runnerRepository is required when buildHosts.enabled — the mw-build-host image (bash, python3, git, bubblewrap, the .NET SDK, build-run.py); each channel names its digest" $b.runnerRepository -}}
{{- $testerRepo := required "buildHosts.testerRepository is required when buildHosts.enabled (e.g. cr.example.com/mw-plugin-test)" $b.testerRepository -}}
{{- $portalRepo := required "buildHosts.portalRepository is required when buildHosts.enabled (e.g. cr.example.com/memex-portal-ai)" $b.portalRepository -}}
{{- range (list $testerRepo $portalRepo $runnerRepo) -}}
{{- if or (contains "@" .) (regexMatch ":[^/]*$" .) -}}
{{- fail (printf "buildHosts.testerRepository / portalRepository / runnerRepository %q must be a repository without a tag or digest — each channel names its digest" .) -}}
{{- end -}}
{{- end -}}
{{- $tok := ($b.workerTokenSecret | default dict) -}}
{{- $tokName := required "buildHosts.workerTokenSecret.name is required — the Secret holding the worker token (Hosting:Builds:WorkerToken)" $tok.name -}}
{{- $tokKey := required "buildHosts.workerTokenSecret.key is required" $tok.key -}}
{{- if $b.pluginsPullSecret -}}
{{- fail "buildHosts.pluginsPullSecret is not supported — a build host holds NO registry credential (Hosting/StandingBuildHosts §2); the plugins publication is pulled without one" -}}
{{- end -}}
{{- if $b.bakeShares -}}
{{- fail "buildHosts.bakeShares is not supported — a build host mounts NO bake share; publication is the publish lane's, on the aggregate check build" -}}
{{- end -}}
{{- $cache := required "buildHosts.cacheClaimName is required — the shared RWX build-cache claim" $b.cacheClaimName -}}
{{- if ($b.postgres | default dict).enabled -}}
{{- fail "buildHosts.postgres is not supported — a build host runs NO shared database; a stage that needs PostgreSQL starts a disposable one inside its own sandbox (sandbox-postgres.sh)" -}}
{{- end -}}
{{- $sb := ($b.sandbox | default dict) -}}
{{- $sandbox := dict "seccompProfile" ($sb.seccompProfile | default "") "seccompLocalhostProfile" ($sb.seccompLocalhostProfile | default "")
      "appArmorProfile" ($sb.appArmorProfile | default "") "appArmorLocalhostProfile" ($sb.appArmorLocalhostProfile | default "") -}}
{{- range $kind := (list "seccomp" "appArmor") -}}
{{- $t := index $sandbox (printf "%sProfile" $kind) -}}
{{- if and $t (not (has $t (list "RuntimeDefault" "Unconfined" "Localhost"))) -}}
{{- fail (printf "buildHosts.sandbox.%sProfile %q must be RuntimeDefault, Unconfined or Localhost" $kind $t) -}}
{{- end -}}
{{- if and (eq $t "Localhost") (not (index $sandbox (printf "%sLocalhostProfile" $kind))) -}}
{{- fail (printf "buildHosts.sandbox.%sProfile is Localhost but buildHosts.sandbox.%sLocalhostProfile names no profile — Kubernetes rejects the pod without it" $kind $kind) -}}
{{- end -}}
{{- end -}}
{{- $as := ($b.artifactStore | default dict) -}}
{{- $asClaim := ($as.claimName | default "") -}}
{{- $asSpec := ($as.spec | default (ternary "file:/ci-artifacts" "" (ne $asClaim ""))) -}}
{{- if and $asSpec (not (hasPrefix "file:" $asSpec)) -}}
{{- fail (printf "buildHosts.artifactStore.spec %q must be a file: store — the runner image carries no az CLI, and the host holds no cloud credential to write a blob container" $asSpec) -}}
{{- end -}}
{{- $reg := ($b.registry | default dict) -}}
{{- $regUrl := trimSuffix "/" ($reg.url | default "") -}}
{{- $regSecret := "" -}}
{{- if $regUrl -}}
{{- if not (hasPrefix "https://" $regUrl) -}}
{{- fail (printf "buildHosts.registry.url %q must be https — the instance key rides on it" $regUrl) -}}
{{- end -}}
{{- $regSecret = required "buildHosts.registry.instanceKeySecret.name is required with buildHosts.registry.url — the Secret holding the read-only mwi_ instance key" ($reg.instanceKeySecret | default dict).name -}}
{{- end -}}
{{- $channels := list -}}
{{- range $name := (list "latest" "stable") -}}
{{- $c := (index $b $name | default dict) -}}
{{- if $c.enabled -}}
{{- range $k := (list "testerDigest" "portalDigest" "runnerDigest") -}}
{{- $d := (index $c $k | default "") -}}
{{- if not (regexMatch "^sha256:[0-9a-f]{64}$" $d) -}}
{{- fail (printf "buildHosts.%s.%s %q must be a sha256:<64 hex> digest — a host runs ONE immutable set; core's CD writes it into the record" $name $k $d) -}}
{{- end -}}
{{- end -}}
{{- $pv := required (printf "buildHosts.%s.platformVersion is required when that channel is enabled — the set the host stamps on every run it claims" $name) $c.platformVersion -}}
{{- $max := int ($c.maxReplicas | default 1) -}}
{{- if lt $max 1 -}}
{{- fail (printf "buildHosts.%s.maxReplicas must be at least 1" $name) -}}
{{- end -}}
{{- $channels = append $channels (dict "name" $name "platformVersion" $pv "maxReplicas" $max
      "tester" (printf "%s@%s" $testerRepo $c.testerDigest) "portal" (printf "%s@%s" $portalRepo $c.portalDigest)
      "runner" (printf "%s@%s" $runnerRepo $c.runnerDigest)
      "testerDigest" $c.testerDigest "portalDigest" $c.portalDigest "runnerDigest" $c.runnerDigest) -}}
{{- end -}}
{{- end -}}
{{- if not $channels -}}
{{- fail "buildHosts.enabled is true but no channel is enabled (buildHosts.latest.enabled / buildHosts.stable.enabled)" -}}
{{- end -}}
{{- $np := ($b.networkPolicy | default dict) -}}
{{- $wi := ($b.workloadIdentity | default dict) -}}
{{- toYaml (dict "enabled" true "apiUrl" $api "tokenSecret" $tokName "tokenKey" $tokKey
      "workloadIdentityClientId" (trim ($wi.clientId | default ""))
      "cacheClaim" $cache "imagePullSecret" ($b.imagePullSecret | default "")
      "pluginsRegistry" ($b.pluginsRegistry | default "") "pluginsSource" ($b.pluginsSource | default "plugins")
      "orasImage" (required "buildHosts.orasImage is required" $b.orasImage)
      "nodeSelector" ($b.nodeSelector | default dict) "tolerations" ($b.tolerations | default list)
      "resources" ($b.resources | default dict) "workSizeLimit" ($b.workSizeLimit | default "60Gi")
      "runDeadline" (int ($b.runDeadlineSeconds | default 3600))
      "scaleDownStabilizationSeconds" (int ($b.scaleDownStabilizationSeconds | default 300))
      "sandbox" $sandbox
      "pollingInterval" (int ($b.pollingInterval | default 15)) "cooldownPeriod" (int ($b.cooldownPeriod | default 600))
      "networkPolicy" (dict "enabled" ($np.enabled | default false) "apiPodSelector" ($np.apiPodSelector | default dict)
                            "dnsNamespaceSelector" ($np.dnsNamespaceSelector | default (dict "kubernetes.io/metadata.name" "kube-system"))
                            "dnsPodSelector" ($np.dnsPodSelector | default (dict "k8s-app" "kube-dns"))
                            "apiPort" (int ($np.apiPort | default 8080)) "privateCidrs" ($np.privateCidrs | default list))
      "artifactStore" (dict "spec" $asSpec "claimName" $asClaim)
      "registry" (dict "url" $regUrl "keySecret" $regSecret "keySecretKey" ((($reg.instanceKeySecret | default dict).key) | default "key"))
      "channels" $channels) -}}
{{- end -}}
{{- end -}}
