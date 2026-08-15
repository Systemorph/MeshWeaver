// OpenStreetMap renderer for MapControl, drawn with the VENDORED Leaflet 1.9.4 under
// ./leaflet/ (no CDN — the portal's script world stays self-contained; only the tile
// requests go to tile.openstreetmap.org). The exported surface mirrors
// GoogleMapView.razor.js — initializeMap / updateMarkers / updateCircles / the two
// click-callback registrations — so the .cs side of every provider stays structurally
// identical and the deployment picks a provider by module list alone.
const osmMaps = {
    maps: {},
    markers: {},
    circles: {},
    clickCallbacks: {},
    leafletLoaded: null,

    loadLeaflet: function () {
        if (this.leafletLoaded)
            return this.leafletLoaded;
        this.leafletLoaded = new Promise((resolve, reject) => {
            if (typeof L !== 'undefined') {
                resolve();
                return;
            }
            const base = './_content/MeshWeaver.Blazor.OpenStreetMap/leaflet/';

            const css = document.createElement('link');
            css.rel = 'stylesheet';
            css.href = base + 'leaflet.css';
            document.head.appendChild(css);

            const script = document.createElement('script');
            script.src = base + 'leaflet.js';
            script.async = true;
            script.onload = () => {
                // Leaflet's default marker icons resolve relative to the css URL by
                // heuristic; pin them to the vendored folder so they never 404.
                L.Icon.Default.imagePath = base + 'images/';
                resolve();
            };
            script.onerror = () => reject('Failed to load the vendored Leaflet script');
            document.head.appendChild(script);
        });
        return this.leafletLoaded;
    }
};

export async function initializeMap(mapId, options) {
    await osmMaps.loadLeaflet();
    const container = document.getElementById(`map-container-${mapId}`);
    if (!container) {
        console.error(`Map container map-container-${mapId} not found`);
        return;
    }
    // A re-render can call initialize again for the same id — Leaflet refuses to
    // re-own an initialized container, so tear the previous instance down first.
    if (osmMaps.maps[mapId]) {
        osmMaps.maps[mapId].remove();
        delete osmMaps.maps[mapId];
    }

    const map = L.map(container, {
        center: [options.center.lat, options.center.lng],
        zoom: options.zoom,
        zoomControl: options.zoomControl && !options.disableDefaultUI
    });
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
        maxZoom: 19,
        // Required by the OSM tile usage policy.
        attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
    }).addTo(map);

    osmMaps.maps[mapId] = map;
    osmMaps.markers[mapId] = {};
    osmMaps.circles[mapId] = {};
}

export function setMarkerClickCallback(mapId, dotNetRef) {
    osmMaps.clickCallbacks[mapId] = dotNetRef;
}

// One callback registry serves markers and circles alike; the second registration is
// kept so the provider surfaces stay call-compatible.
export function setCircleClickCallback(mapId, dotNetRef) {
    osmMaps.clickCallbacks[mapId] = dotNetRef;
}

function notifyClicked(mapId, id) {
    const callback = osmMaps.clickCallbacks[mapId];
    if (callback)
        callback.invokeMethodAsync('OnClicked', id);
}

export function updateMarkers(mapId, markerConfigs) {
    const map = osmMaps.maps[mapId];
    if (!map) return;

    const current = osmMaps.markers[mapId];
    for (const id of Object.keys(current)) {
        map.removeLayer(current[id]);
        delete current[id];
    }

    for (const config of markerConfigs) {
        const markerOptions = {
            title: config.title,
            draggable: config.draggable
        };
        if (config.icon)
            markerOptions.icon = L.icon({ iconUrl: config.icon });
        const marker = L.marker([config.position.lat, config.position.lng], markerOptions).addTo(map);
        const tooltip = config.label || config.title;
        if (tooltip)
            marker.bindTooltip(tooltip);
        marker.on('click', () => notifyClicked(mapId, config.id));
        current[config.id] = marker;
    }
}

export function updateCircles(mapId, circleConfigs) {
    const map = osmMaps.maps[mapId];
    if (!map) return;

    const current = osmMaps.circles[mapId];
    for (const id of Object.keys(current)) {
        map.removeLayer(current[id]);
        delete current[id];
    }

    for (const config of circleConfigs) {
        const circle = L.circle([config.center.lat, config.center.lng], {
            radius: config.radius,
            color: config.strokeColor,
            opacity: config.strokeOpacity,
            weight: config.strokeWeight,
            fillColor: config.fillColor,
            fillOpacity: config.fillOpacity
        }).addTo(map);
        circle.on('click', () => notifyClicked(mapId, config.id));
        current[config.id] = circle;
    }
}
