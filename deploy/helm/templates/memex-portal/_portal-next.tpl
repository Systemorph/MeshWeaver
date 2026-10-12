{{- /*
  🚂 How /next is served: "off", "bundled" or "image".

  BUNDLED is the normal shape (policy `portal-next-in-platform-image`). The Next.js front end ships INSIDE the platform portal
  image — core's main-cd builds clients/portal-next into memex-portal-ai as /app/portal-next.zip —
  and the portal process unpacks and starts it on port 3000 with a clean environment
  (MeshWeaver.Plugins src/Memex.Portal.Distributed/PortalNextProcess.cs). So /next runs whatever
  build the portal runs: one image, one version, one `3-latest` pointer, and no path that moves the
  portal's image (a helm render, a `set image memex-portal=`, a strategic-merge patch) can leave it
  behind. The chart renders PortalNext__Bundled into the portal config, a `next` port on the portal
  container and Service, and routes /next to it.

  IMAGE is the developer override: `portalNext.image` set renders the old shape — a separately
  built image in its own Deployment + Service (templates/memex-portal/portal-next.yaml). memex-local
  uses it for a locally built image. A deployment record has no use for it: an image there is a pin,
  which is exactly what the bundle removed.
*/ -}}
{{- define "memex.portalNext.mode" -}}
{{- $next := .Values.portalNext | default dict -}}
{{- if not $next.enabled -}}off
{{- else if $next.image -}}image
{{- else -}}bundled
{{- end -}}
{{- end -}}

{{- /* The port the bundled server binds inside the portal container. */ -}}
{{- define "memex.portalNext.port" -}}3000{{- end -}}
