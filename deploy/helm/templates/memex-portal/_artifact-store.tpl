{{- /*
The shared artifact store's OWNER jobs (Doc/Architecture/SharedArtifactStore, increments 4 and 5):
the one-way upstream sync and the local-source builds, both CronJobs in the control instance's
namespace writing into the store claim it owns.

🚨 THIS HELPER IS THE GATE, the same way `memex.bundles` is for the bundle fetch: the jobs read
`artifactStore.*` only through it, so a half-declared block fails `helm template` HERE naming the
key — never a CronJob that renders, runs, and writes nothing.

Rendered ONLY when `artifactStore.upstream.registry` or `artifactStore.localBuild.sources` is set
(`enabled: false` otherwise), so every environment that has not opted in renders byte-identically.
*/ -}}
{{- define "memex.artifactStore" -}}
{{- $a := (.Values.artifactStore | default dict) -}}
{{- $up := ($a.upstream | default dict) -}}
{{- $lb := ($a.localBuild | default dict) -}}
{{- $registry := ($up.registry | default "") -}}
{{- $locals := ($lb.sources | default list) -}}
{{- if and (not $registry) (not $locals) -}}
{{- toYaml (dict "enabled" false) -}}
{{- else -}}
{{- $claim := required "artifactStore.claimName is required when artifactStore.upstream or artifactStore.localBuild is set — the OWNER's store claim the jobs write into" $a.claimName -}}
{{- $mount := ($a.mountPath | default "/artifacts") -}}
{{- if not (hasPrefix "/" $mount) -}}
{{- fail (printf "artifactStore.mountPath must be absolute (got %q)" $mount) -}}
{{- end -}}
{{- $rid := ($a.runtimeIdentifier | default "linux-x64") -}}
{{- if not (regexMatch "^[a-z0-9]+-[a-z0-9]+$" $rid) -}}
{{- fail (printf "artifactStore.runtimeIdentifier %q is not a runtime identifier (<os>-<arch>, e.g. linux-x64)" $rid) -}}
{{- end -}}
{{- $root := printf "%s/%s/prebuilt-bundles" (trimSuffix "/" $mount) $rid -}}
{{- $sync := dict "enabled" false -}}
{{- if $registry -}}
{{- if not (regexMatch "^[A-Za-z0-9.-]+(:[0-9]+)?$" $registry) -}}
{{- fail (printf "artifactStore.upstream.registry %q is not a registry host (host[:port], no scheme, no path)" $registry) -}}
{{- end -}}
{{- $sources := ($up.sources | default list) -}}
{{- if not $sources -}}
{{- fail "artifactStore.upstream.sources is required when artifactStore.upstream.registry is set — the publications to replicate (e.g. [plugins])" -}}
{{- end -}}
{{- range $sources -}}
{{- if not (regexMatch "^[a-z0-9._-]+$" .) -}}
{{- fail (printf "artifactStore.upstream.sources entry %q is not a registry name component" .) -}}
{{- end -}}
{{- end -}}
{{- $pull := required "artifactStore.upstream.pullSecret is required when artifactStore.upstream.registry is set — the kubernetes.io/dockerconfigjson Secret holding THIS environment's pull credential for the upstream registry" $up.pullSecret -}}
{{- range ($up.identities | default list) -}}
{{- if not (regexMatch "^[A-Za-z0-9]+$" .) -}}
{{- fail (printf "artifactStore.upstream.identities entry %q is not a framework identity" .) -}}
{{- end -}}
{{- end -}}
{{- $sync = dict "enabled" true "registry" $registry "sources" $sources "pullSecret" $pull "identities" ($up.identities | default list) "schedule" ($up.schedule | default "*/30 * * * *") -}}
{{- end -}}
{{- $build := dict "enabled" false -}}
{{- if $locals -}}
{{- $portalImage := required "portal.image is required for artifactStore.localBuild — the build runs in the portal image" (.Values.portal).image -}}
{{- $testerRepo := required "artifactStore.localBuild.testerImageRepository is required when artifactStore.localBuild.sources is set — the platform tester whose compile verb produces the bundles (its tag follows portal.image)" $lb.testerImageRepository -}}
{{- if or (contains "@" $testerRepo) (regexMatch ":[^/]*$" $testerRepo) -}}
{{- fail (printf "artifactStore.localBuild.testerImageRepository %q must be a repository without a tag or digest — the tag follows portal.image" $testerRepo) -}}
{{- end -}}
{{- if or (contains "@" $portalImage) (not (regexMatch ":[^/:]+$" $portalImage)) -}}
{{- fail (printf "artifactStore.localBuild needs portal.image to carry a TAG (got %q) — the tester runs at the portal's tag, so the compiler is the platform the portals run; a digest names no tester" $portalImage) -}}
{{- end -}}
{{- $tag := regexFind "[^:]+$" $portalImage -}}
{{- $seen := dict -}}
{{- $entries := list -}}
{{- range $locals -}}
{{- $src := required "artifactStore.localBuild.sources[].source is required" .source -}}
{{- if not (regexMatch "^[a-z0-9]([a-z0-9-]{0,34}[a-z0-9])?$" $src) -}}
{{- fail (printf "artifactStore.localBuild.sources entry %q must be a lowercase DNS label of at most 36 characters — it names the CronJob (artifact-build-<source>, within the 52-character CronJob limit)" $src) -}}
{{- end -}}
{{- if hasKey $seen $src -}}
{{- fail (printf "artifactStore.localBuild.sources names %q twice — one source, one producer" $src) -}}
{{- end -}}
{{- if has $src ($sync.sources | default list) -}}
{{- fail (printf "artifactStore.localBuild.sources names %q, which artifactStore.upstream also replicates — a local build would race the sync on one prefix" $src) -}}
{{- end -}}
{{- $_ := set $seen $src true -}}
{{- $repo := required (printf "artifactStore.localBuild.sources[%s].repository is required — the https clone URL" $src) .repository -}}
{{- if not (regexMatch "^https://[^[:space:]]+$" $repo) -}}
{{- fail (printf "artifactStore.localBuild.sources[%s].repository %q is not an https URL" $src $repo) -}}
{{- end -}}
{{- $ref := (.ref | default "main") -}}
{{- if not (regexMatch "^[A-Za-z0-9._/-]+$" $ref) -}}
{{- fail (printf "artifactStore.localBuild.sources[%s].ref %q is not a branch or tag name" $src $ref) -}}
{{- end -}}
{{- $entries = append $entries (dict "source" $src "repository" $repo "ref" $ref "credentialSecret" (.credentialSecret | default "")) -}}
{{- end -}}
{{- $build = dict "enabled" true "sources" $entries "portalImage" $portalImage "testerImage" (printf "%s:%s" $testerRepo $tag) "schedule" ($lb.schedule | default "*/30 * * * *") "upstreams" ($sync.sources | default list) -}}
{{- end -}}
{{- $out := dict
      "enabled" true
      "claimName" $claim
      "mountPath" $mount
      "runtimeIdentifier" $rid
      "root" $root
      "image" (required "artifactStore.image is required — the pinned ORAS image the sync runs in" $a.image)
      "portalImage" ((.Values.portal).image | default "")
      "imagePullSecret" ((.Values.portal).imagePullSecret | default "")
      "resources" ($a.resources | default (dict "requests" (dict "cpu" "100m" "memory" "256Mi") "limits" (dict "memory" "1Gi")))
      "buildResources" ($a.buildResources | default (dict "requests" (dict "cpu" "1" "memory" "2Gi") "limits" (dict "memory" "6Gi")))
      "sync" $sync
      "build" $build
-}}
{{- toYaml $out -}}
{{- end -}}
{{- end -}}
