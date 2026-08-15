---
Name: Maps become provider-neutral
Category: Feature
Description: The map layout-area control is now provider-neutral MapControl — Google Maps is one renderer among several, with OpenStreetMap and Apple MapKit providers on the way.
Icon: Globe
Order: -20260815
---

# Maps become provider-neutral

The map control for layout areas is now called `MapControl` and lives in `MeshWeaver.Maps` —
it carries pure geographic data (center, zoom, markers, circles) with no tie to any map engine.
Google Maps remains the portal's renderer, and the native app already renders the same control
with Apple MapKit. Because the control no longer names a provider, OpenStreetMap and Apple
MapKit JS renderers can plug in next to Google — your layout areas won't change when they do.
