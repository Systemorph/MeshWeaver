---
Name: OpenStreetMap and Apple MapKit providers
Category: Feature
Description: Maps can now render with OpenStreetMap (no API key) or Apple MapKit in addition to Google Maps — a deployment picks its provider by listing one module.
Icon: Globe
Order: -20260815
---

# OpenStreetMap and Apple MapKit providers

The provider-neutral map control gains two new renderers: **OpenStreetMap** (drawn with a bundled
Leaflet — no API key and no external scripts, only the map tiles come from openstreetmap.org)
and **Apple MapKit** (needs a MapKit JS token). A deployment picks its map provider by listing
exactly one provider module in its configuration — Google Maps remains the default — and every
page showing a map stays identical across providers: same markers, circles, and click behavior.
