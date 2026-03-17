window.leafletInterop = {
    map: null,
    marker: null,
    geoJsonLayer: null,

    initialize: function (elementId, lat, lon, zoom) {
        if (this.map) {
            this.map.remove();
        }

        this.map = L.map(elementId).setView([lat, lon], zoom);

        L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
            attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
            maxZoom: 19
        }).addTo(this.map);
    },

    setMarker: function (lat, lon, label) {
        if (!this.map) return;

        this.clearLayers();

        this.marker = L.marker([lat, lon])
            .addTo(this.map)
            .bindPopup(label)
            .openPopup();

        this.map.flyTo([lat, lon], 13, { duration: 1 });
    },

    showGeoJson: function (geoJson) {
        if (!this.map) return;

        this.clearLayers();

        this.geoJsonLayer = L.geoJSON(geoJson, {
            style: {
                color: '#3388ff',
                weight: 2,
                fillOpacity: 0.15
            },
            onEachFeature: function (feature, layer) {
                if (feature.properties && feature.properties.name) {
                    layer.bindPopup(feature.properties.name);
                }
            }
        }).addTo(this.map);

        this.map.fitBounds(this.geoJsonLayer.getBounds(), { padding: [20, 20] });
    },

    flyTo: function (lat, lon, zoom) {
        if (!this.map) return;
        this.map.flyTo([lat, lon], zoom, { duration: 1 });
    },

    clearLayers: function () {
        if (this.marker) {
            this.map.removeLayer(this.marker);
            this.marker = null;
        }
        if (this.geoJsonLayer) {
            this.map.removeLayer(this.geoJsonLayer);
            this.geoJsonLayer = null;
        }
    }
};
