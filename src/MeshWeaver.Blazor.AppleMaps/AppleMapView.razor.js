// Apple MapKit JS renderer for MapControl. Apple's script loads from Apple's CDN (their terms —
// same shape as the Google provider loading maps.googleapis.com); everything else stays local.
// The exported surface mirrors the other providers — initializeMap / updateMarkers /
// updateCircles / the click-callback registrations — so the .cs sides stay structurally
// identical and a deployment picks its provider by module list alone.
const appleMaps = {
    maps: {},
    markers: {},
    circles: {},
    clickCallbacks: {},
    mapkitLoaded: null,

    loadMapKit: function (token) {
        if (this.mapkitLoaded)
            return this.mapkitLoaded;
        this.mapkitLoaded = new Promise((resolve, reject) => {
            if (typeof mapkit !== 'undefined') {
                resolve();
                return;
            }
            const script = document.createElement('script');
            script.src = 'https://cdn.apple-mapkit.com/mk/5.x.x/mapkit.js';
            script.crossOrigin = 'anonymous';
            script.async = true;
            script.onload = () => {
                mapkit.init({
                    authorizationCallback: done => done(token)
                });
                resolve();
            };
            script.onerror = () => reject('Failed to load the MapKit JS script');
            document.head.appendChild(script);
        });
        return this.mapkitLoaded;
    }
};

// MapKit has no numeric zoom; derive a visible span from the Web-Mercator-ish zoom heuristic the
// MAUI MapView uses (higher zoom → smaller span).
function spanForZoom(zoom) {
    const km = Math.max(0.2, 40000.0 / Math.pow(2, zoom));
    const degrees = km / 111.0;
    return new mapkit.CoordinateSpan(degrees, degrees);
}

export async function initializeMap(mapId, options, token) {
    await appleMaps.loadMapKit(token);
    const container = document.getElementById(`map-container-${mapId}`);
    if (!container) {
        console.error(`Map container map-container-${mapId} not found`);
        return;
    }
    if (appleMaps.maps[mapId]) {
        appleMaps.maps[mapId].destroy();
        delete appleMaps.maps[mapId];
    }

    const center = new mapkit.Coordinate(options.center.lat, options.center.lng);
    const map = new mapkit.Map(container, {
        region: new mapkit.CoordinateRegion(center, spanForZoom(options.zoom)),
        showsZoomControl: options.zoomControl && !options.disableDefaultUI,
        showsMapTypeControl: options.mapTypeControl && !options.disableDefaultUI,
        mapType: options.mapTypeId === 'satellite' ? mapkit.Map.MapTypes.Satellite
            : options.mapTypeId === 'hybrid' ? mapkit.Map.MapTypes.Hybrid
                : mapkit.Map.MapTypes.Standard
    });

    appleMaps.maps[mapId] = map;
    appleMaps.markers[mapId] = {};
    appleMaps.circles[mapId] = {};
}

export function setMarkerClickCallback(mapId, dotNetRef) {
    appleMaps.clickCallbacks[mapId] = dotNetRef;
}

// One callback registry serves markers and circles alike; the second registration is kept so the
// provider surfaces stay call-compatible.
export function setCircleClickCallback(mapId, dotNetRef) {
    appleMaps.clickCallbacks[mapId] = dotNetRef;
}

function notifyClicked(mapId, id) {
    const callback = appleMaps.clickCallbacks[mapId];
    if (callback)
        callback.invokeMethodAsync('OnClicked', id);
}

export function updateMarkers(mapId, markerConfigs) {
    const map = appleMaps.maps[mapId];
    if (!map) return;

    const current = appleMaps.markers[mapId];
    for (const id of Object.keys(current)) {
        map.removeAnnotation(current[id]);
        delete current[id];
    }

    for (const config of markerConfigs) {
        const annotation = new mapkit.MarkerAnnotation(
            new mapkit.Coordinate(config.position.lat, config.position.lng), {
                title: config.title,
                glyphText: config.label || '',
                draggable: config.draggable
            });
        annotation.addEventListener('select', () => notifyClicked(mapId, config.id));
        map.addAnnotation(annotation);
        current[config.id] = annotation;
    }
}

export function updateCircles(mapId, circleConfigs) {
    const map = appleMaps.maps[mapId];
    if (!map) return;

    const current = appleMaps.circles[mapId];
    for (const id of Object.keys(current)) {
        map.removeOverlay(current[id]);
        delete current[id];
    }

    for (const config of circleConfigs) {
        const overlay = new mapkit.CircleOverlay(
            new mapkit.Coordinate(config.center.lat, config.center.lng),
            config.radius,
            {
                style: new mapkit.Style({
                    fillColor: config.fillColor,
                    fillOpacity: config.fillOpacity,
                    strokeColor: config.strokeColor,
                    strokeOpacity: config.strokeOpacity,
                    lineWidth: config.strokeWeight
                })
            });
        overlay.addEventListener('select', () => notifyClicked(mapId, config.id));
        map.addOverlay(overlay);
        current[config.id] = overlay;
    }
}
