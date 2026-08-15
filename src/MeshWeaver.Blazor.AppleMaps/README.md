# MeshWeaver.Blazor.AppleMaps

The Apple MapKit provider of MeshWeaver's provider-neutral `MapControl`: a Blazor Server view
pack rendering with **MapKit JS** (loaded from Apple's CDN, per Apple's terms — the same shape as
the Google provider loading `maps.googleapis.com`).

## Activation

List the DLL under the deployment's module list and provide a MapKit JS token:

```json
"Modules": { "Assemblies": [ "MeshWeaver.Blazor.AppleMaps.dll" ] },
"AppleMaps": { "Token": "<MapKit JS JWT minted from an Apple Developer MapKit key>" }
```

Without a token the view logs a warning and renders empty — never a broken script load. View
maps are first-match-wins: a deployment lists exactly ONE map provider module
(`MeshWeaver.Blazor.GoogleMaps`, `MeshWeaver.Blazor.OpenStreetMap`, or this pack). Layout areas
emitting `MapControl` need no change when the provider swaps.

## Surface

Renders `MapControl` from `MeshWeaver.Maps`: center/zoom (zoom is mapped onto a MapKit
coordinate span), markers (title, glyph label, draggable, click events) and circles (fill/stroke
styling, click events). `MapTypeId` maps `satellite`/`hybrid` onto MapKit's map types;
Street-View-specific options are ignored.
