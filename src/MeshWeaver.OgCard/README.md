# MeshWeaver.OgCard

The `OgCard` link-preview layout area: picture + title + description, the whole card a link —
for external pages (their Open Graph head fetched server-side via the core
`OpenGraphPreviewService`) or same-mesh nodes (read live off the node stream). Several targets
compose into one responsive grid. Embedded from markdown with the standard `@@` layout-area
reference; the `/og-card` skill documents the authoring rules.

## Activation

A module — list the DLL under the deployment's module list:

```json
"Modules": { "Assemblies": [ "MeshWeaver.OgCard.dll" ] }
```

The area registers on every per-node hub (the markdown embed resolves it on the embedding
document's hub). Delisting removes the outbound server-side URL-fetch surface; existing embeds
render the standard area-not-found placeholder.
