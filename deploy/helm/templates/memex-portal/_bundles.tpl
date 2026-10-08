{{- /*
Bundle materialisation before the portal starts (Doc/Architecture/PluginBundlesInTheRegistry →
"Who pulls what"): the `bundle-fetch` init container pulls the sealed publication of every
configured source for THIS image's framework identity from the fleet registry with ORAS, into the
directory the pre-warm already reads (`PreWarm__PrebuiltBundleRoot`), in exactly the layout
`ShippedPrebuiltBundles` reads from the share — `<root>/<identity>/<source>/…`, `_complete`
gating it. The portal keeps reading a filesystem; it needs no registry client and no network to
seed.

🚨 THIS HELPER IS THE GATE, the same way `memex.registry` is for templates/registry/: the
Deployment reads `bundles.*` only through it, so a half-declared block fails `helm template` HERE
naming the key — never an init container that renders, runs, and exits 0 having fetched nothing.

Rendered ONLY when `bundles.registry` is set (`enabled: false` otherwise), so every environment
that has not opted in renders byte-identically — the chart's neutrality rule.

The credential is the pod's imagePullSecret (`portal.imagePullSecret`), mounted as a docker
config.json: the same instance key that pulls the platform image pulls the bundles, and there is
no second secret.
*/ -}}
{{- define "memex.bundles" -}}
{{- $b := (.Values.bundles | default dict) -}}
{{- $registry := ($b.registry | default "") -}}
{{- if not $registry -}}
{{- toYaml (dict "enabled" false) -}}
{{- else -}}
{{- $sources := ($b.sources | default list) -}}
{{- if not $sources -}}
{{- fail "bundles.sources is required when bundles.registry is set — the source names to materialise (as the publisher named them, e.g. [plugins]); an empty list would run the init container and fetch nothing" -}}
{{- end -}}
{{- $identity := ($b.identity | default "") -}}
{{- $identityFile := ($b.identityFile | default "") -}}
{{- /* 🚨 MeshWeaver#6052 ask 3: the identity is READ FROM THE PORTAL IMAGE by default — the image
       carries `meshweaver-framework.identity` (written by MeshWeaverSurfaceManifest.targets), and the
       `bundle-identity` init container copies it out before `bundle-fetch` runs. `bundles.identity`
       is no longer the source: it is the FALLBACK for an image that predates the file. Only an
       explicit `bundles.identityFile` (an operator-owned file on the data volume) turns the
       derivation off. */ -}}
{{- $fromImage := not $identityFile -}}
{{- $imageIdentityPath := ($b.imageIdentityPath | default "/app/meshweaver-framework.identity") -}}
{{- if and $fromImage (not (hasPrefix "/" $imageIdentityPath)) -}}
{{- fail (printf "bundles.imageIdentityPath must be an absolute path inside the portal image (got %q)" $imageIdentityPath) -}}
{{- end -}}
{{- $image := required "bundles.image is required when bundles.registry is set — the ORAS image, pinned by digest (ghcr.io/oras-project/oras:v1.3.4@sha256:…)" $b.image -}}
{{- $root := ($b.root | default ((.Values.config).memex_portal).PreWarm__PrebuiltBundleRoot | default "") -}}
{{- if not $root -}}
{{- fail "bundles.root (or config.memex_portal.PreWarm__PrebuiltBundleRoot, which it defaults to) is required when bundles.registry is set — it is the directory the init container fills AND the one the pre-warm reads; with neither set the fetch lands where nothing looks" -}}
{{- end -}}
{{- $pullSecret := required "portal.imagePullSecret is required when bundles.registry is set — the kubernetes.io/dockerconfigjson Secret whose credential pulls the platform image is the one the init container pulls bundles with" (.Values.portal).imagePullSecret -}}
{{- $out := dict
      "enabled" true
      "registry" $registry
      "sources" $sources
      "identity" $identity
      "identityFile" $identityFile
      "fromImage" $fromImage
      "imageIdentityPath" $imageIdentityPath
      "portalImage" ((.Values.portal).image | default "")
      "image" $image
      "root" $root
      "pullSecret" $pullSecret
      "resources" ($b.resources | default (dict "requests" (dict "cpu" "50m" "memory" "64Mi") "limits" (dict "memory" "256Mi")))
-}}
{{- toYaml $out -}}
{{- end -}}
{{- end -}}
