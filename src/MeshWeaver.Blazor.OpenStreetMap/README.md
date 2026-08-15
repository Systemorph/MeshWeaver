# MeshWeaver.Blazor.OpenStreetMap

The OpenStreetMap provider of MeshWeaver's provider-neutral `MapControl`: a Blazor Server view
pack rendering with the **vendored Leaflet 1.9.4** (`wwwroot/leaflet/` — no CDN; only tile
requests go to `tile.openstreetmap.org`, with the attribution the OSM tile usage policy
requires). No API key.

## Activation

List the DLL under the deployment's module list — that is the complete activation:

```json
"Modules": { "Assemblies": [ "MeshWeaver.Blazor.OpenStreetMap.dll" ] }
```

View maps are first-match-wins: a deployment lists exactly ONE map provider module
(`MeshWeaver.Blazor.GoogleMaps`, this pack, or the Apple MapKit pack). Layout areas emitting
`MapControl` need no change when the provider swaps.

## Surface

Renders `MapControl` from `MeshWeaver.Maps`: center/zoom, markers (title, label, draggable,
custom icon, click events) and circles (fill/stroke styling, click events). Google-specific
`MapOptions` fields (`MapTypeId`, `StreetViewControl`, …) are ignored — OSM serves one base
layer.
