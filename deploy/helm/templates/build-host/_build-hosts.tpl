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
  * a channel's tester/portal must be a sha256 DIGEST — a run records the set that compiled it,
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
{{- $runner := required "buildHosts.runnerImage is required when buildHosts.enabled — the container with bash, python3, git and the .NET SDK the stages run in" $b.runnerImage -}}
{{- $testerRepo := required "buildHosts.testerRepository is required when buildHosts.enabled (e.g. cr.example.com/mw-plugin-test)" $b.testerRepository -}}
{{- $portalRepo := required "buildHosts.portalRepository is required when buildHosts.enabled (e.g. cr.example.com/memex-portal-ai)" $b.portalRepository -}}
{{- range (list $testerRepo $portalRepo) -}}
{{- if or (contains "@" .) (regexMatch ":[^/]*$" .) -}}
{{- fail (printf "buildHosts.testerRepository / portalRepository %q must be a repository without a tag or digest — each channel names its digest" .) -}}
{{- end -}}
{{- end -}}
{{- $tok := ($b.workerTokenSecret | default dict) -}}
{{- $tokName := required "buildHosts.workerTokenSecret.name is required — the Secret holding the worker token (Hosting:Builds:WorkerToken)" $tok.name -}}
{{- $tokKey := required "buildHosts.workerTokenSecret.key is required" $tok.key -}}
{{- $cache := required "buildHosts.cacheClaimName is required — the shared RWX build-cache claim" $b.cacheClaimName -}}
{{- $pg := ($b.postgres | default dict) -}}
{{- if and $pg.enabled (not $pg.image) -}}
{{- fail "buildHosts.postgres.enabled needs buildHosts.postgres.image — the substrate suites' Postgres sidecar, from the fleet's mirror" -}}
{{- end -}}
{{- $channels := list -}}
{{- range $name := (list "latest" "stable") -}}
{{- $c := (index $b $name | default dict) -}}
{{- if $c.enabled -}}
{{- range $k := (list "testerDigest" "portalDigest") -}}
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
      "testerDigest" $c.testerDigest "portalDigest" $c.portalDigest) -}}
{{- end -}}
{{- end -}}
{{- if not $channels -}}
{{- fail "buildHosts.enabled is true but no channel is enabled (buildHosts.latest.enabled / buildHosts.stable.enabled)" -}}
{{- end -}}
{{- $np := ($b.networkPolicy | default dict) -}}
{{- toYaml (dict "enabled" true "apiUrl" $api "runnerImage" $runner "tokenSecret" $tokName "tokenKey" $tokKey
      "cacheClaim" $cache "imagePullSecret" ($b.imagePullSecret | default "")
      "pluginsRegistry" ($b.pluginsRegistry | default "") "pluginsSource" ($b.pluginsSource | default "plugins")
      "pluginsPullSecret" ($b.pluginsPullSecret | default "") "orasImage" (required "buildHosts.orasImage is required" $b.orasImage)
      "nodeSelector" ($b.nodeSelector | default dict) "tolerations" ($b.tolerations | default list)
      "resources" ($b.resources | default dict) "workSizeLimit" ($b.workSizeLimit | default "60Gi")
      "grace" (int ($b.terminationGracePeriodSeconds | default 3600))
      "pollingInterval" (int ($b.pollingInterval | default 15)) "cooldownPeriod" (int ($b.cooldownPeriod | default 600))
      "networkPolicy" (dict "enabled" ($np.enabled | default false) "apiPodSelector" ($np.apiPodSelector | default dict)
                            "apiPort" (int ($np.apiPort | default 8080)) "privateCidrs" ($np.privateCidrs | default list))
      "postgres" (dict "enabled" ($pg.enabled | default false) "image" ($pg.image | default ""))
      "channels" $channels) -}}
{{- end -}}
{{- end -}}
