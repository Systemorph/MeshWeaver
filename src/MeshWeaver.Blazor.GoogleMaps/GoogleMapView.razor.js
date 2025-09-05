const googleMaps = {
    maps: {},
    markers: {},
    markerClickCallbacks: {},
    apiLoaded: false,

    loadGoogleMapsAPI: function(apiKey) {
        return new Promise((resolve, reject) => {
            if (this.apiLoaded || (typeof google !== 'undefined' && typeof google.maps !== 'undefined')) {
                this.apiLoaded = true;
                resolve();
                return;
            }

            
            const script = document.createElement('script');
            script.src = `https://maps.googleapis.com/maps/api/js?key=${apiKey}&libraries=places&v=3&loading=async`;
            script.async = true;
            script.defer = true;
            
            script.onload = () => {
                this.apiLoaded = true;
                resolve();
            };
            
            script.onerror = () => {
                console.error('Failed to load Google Maps API script');
                reject('Failed to load Google Maps API script');
            };
            
            document.head.appendChild(script);
        });
    },

    initializeMap: function (mapId, options, apiKey) {
        return new Promise(async (resolve, reject) => {
            try {
                // Load Google Maps API first
                await this.loadGoogleMapsAPI(apiKey);
                
                // Wait a bit for the API to fully initialize
                await new Promise(resolve => setTimeout(resolve, 100));
                
                if (typeof google === 'undefined' || typeof google.maps === 'undefined') {
                    reject('Google Maps API not loaded after script load');
                    return;
                }

                // Find the container and create our own map div
                const containerId = `map-container-${mapId}`;
                const containerElement = document.getElementById(containerId);
                if (!containerElement) {
                    reject(`Container element with id '${containerId}' not found`);
                    return;
                }


                // Create a fresh map div that inherits container dimensions
                const mapElement = document.createElement('div');
                mapElement.id = mapId;
                mapElement.style.width = '100%';
                mapElement.style.height = '100%';
                mapElement.style.display = 'block';
                mapElement.style.position = 'relative';

                // Clear container and add our map div
                containerElement.innerHTML = '';
                containerElement.appendChild(mapElement);

                // Force a layout recalculation by accessing offsetWidth/Height
                containerElement.offsetHeight; // Force reflow
                mapElement.offsetHeight; // Force reflow
                
                // Wait for element to be fully rendered
                await new Promise(resolve => setTimeout(resolve, 50));

                const mapOptions = {
                    zoom: options.zoom || 15,
                    center: { lat: options.center?.lat || 0, lng: options.center?.lng || 0 },
                    mapTypeId: options.mapTypeId || google.maps.MapTypeId.ROADMAP,
                    disableDefaultUI: options.disableDefaultUI || false,
                    zoomControl: options.zoomControl !== false,
                    mapTypeControl: options.mapTypeControl !== false,
                    scaleControl: options.scaleControl || false,
                    streetViewControl: options.streetViewControl !== false,
                    rotateControl: options.rotateControl || false,
                    fullscreenControl: options.fullscreenControl || false
                };

                if (mapElement.offsetWidth === 0 || mapElement.offsetHeight === 0) {
                    throw new Error(`Map element has invalid dimensions: ${mapElement.offsetWidth} x ${mapElement.offsetHeight}`);
                }

                const map = new google.maps.Map(mapElement, mapOptions);
                this.maps[mapId] = map;
                this.markers[mapId] = [];

                // Force a resize in case of timing issues
                setTimeout(() => {
                    google.maps.event.trigger(map, 'resize');
                    map.setCenter(mapOptions.center);
                }, 500);
                
                resolve(mapId);
            } catch (error) {
                reject(error.message);
            }
        });
    },

    addMarker: function (mapId, markerOptions) {
        return new Promise((resolve, reject) => {
            if (!this.maps[mapId]) {
                reject(`Map '${mapId}' not found`);
                return;
            }

            try {
                const marker = new google.maps.Marker({
                    position: { lat: markerOptions.position.lat, lng: markerOptions.position.lng },
                    map: this.maps[mapId],
                    title: markerOptions.title || '',
                    label: markerOptions.label || '',
                    draggable: markerOptions.draggable || false,
                    icon: markerOptions.icon || null
                });

                const markerId = markerOptions.id || `marker_${Date.now()}_${Math.random()}`;
                
                // Add click listener if callback is available
                if (this.markerClickCallbacks[mapId]) {
                    marker.addListener('click', () => {
                        const position = marker.getPosition();
                        this.markerClickCallbacks[mapId].invokeMethodAsync('OnMarkerClicked', 
                            markerId, 
                            markerOptions.title || '', 
                            position.lat(), 
                            position.lng()
                        );
                    });
                }
                
                this.markers[mapId].push({ id: markerId, marker: marker });

                resolve(markerId);
            } catch (error) {
                reject(error.message);
            }
        });
    },

    clearMarkers: function (mapId) {
        return new Promise((resolve, reject) => {
            if (!this.maps[mapId]) {
                reject(`Map '${mapId}' not found`);
                return;
            }

            try {
                const markers = this.markers[mapId] || [];
                markers.forEach(markerData => {
                    markerData.marker.setMap(null);
                });
                this.markers[mapId] = [];
                resolve(true);
            } catch (error) {
                reject(error.message);
            }
        });
    },

    updateMarkers: function (mapId, markerConfigs) {
        return new Promise(async (resolve, reject) => {
            try {
                // Clear existing markers
                await this.clearMarkers(mapId);

                // Add new markers
                const markerPromises = markerConfigs.map(config => 
                    this.addMarker(mapId, config)
                );

                await Promise.all(markerPromises);
                resolve(true);
            } catch (error) {
                reject(error.message);
            }
        });
    },

    setMapCenter: function (mapId, lat, lng) {
        return new Promise((resolve, reject) => {
            if (!this.maps[mapId]) {
                reject(`Map '${mapId}' not found`);
                return;
            }

            try {
                this.maps[mapId].setCenter({ lat: lat, lng: lng });
                resolve(true);
            } catch (error) {
                reject(error.message);
            }
        });
    },

    setMapZoom: function (mapId, zoom) {
        return new Promise((resolve, reject) => {
            if (!this.maps[mapId]) {
                reject(`Map '${mapId}' not found`);
                return;
            }

            try {
                this.maps[mapId].setZoom(zoom);
                resolve(true);
            } catch (error) {
                reject(error.message);
            }
        });
    },

    setMarkerClickCallback: function (mapId, dotNetObjectReference) {
        this.markerClickCallbacks[mapId] = dotNetObjectReference;
    },

    isGoogleMapsLoaded: function () {
        return this.apiLoaded && typeof google !== 'undefined' && typeof google.maps !== 'undefined';
    }
};

// Export the individual functions for ES6 module usage
export function initializeMap(mapId, options, apiKey) {
    return googleMaps.initializeMap(mapId, options, apiKey);
}

export function addMarker(mapId, markerOptions) {
    return googleMaps.addMarker(mapId, markerOptions);
}

export function clearMarkers(mapId) {
    return googleMaps.clearMarkers(mapId);
}

export function updateMarkers(mapId, markerConfigs) {
    return googleMaps.updateMarkers(mapId, markerConfigs);
}

export function setMapCenter(mapId, lat, lng) {
    return googleMaps.setMapCenter(mapId, lat, lng);
}

export function setMapZoom(mapId, zoom) {
    return googleMaps.setMapZoom(mapId, zoom);
}

export function setMarkerClickCallback(mapId, dotNetObjectReference) {
    return googleMaps.setMarkerClickCallback(mapId, dotNetObjectReference);
}

export function isGoogleMapsLoaded() {
    return googleMaps.isGoogleMapsLoaded();
}