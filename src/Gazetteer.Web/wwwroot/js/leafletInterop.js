window.leafletInterop = {
    map: null,
    marker: null,
    geoJsonLayer: null,

    initialize: function (elementId, lat, lon, zoom) {
        if (this.map) return;
        this.map = L.map(elementId).setView([lat, lon], zoom);
        L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
            attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
            maxZoom: 19
        }).addTo(this.map);
    },

    setMarker: function (lat, lon, popup) {
        if (this.marker) {
            this.map.removeLayer(this.marker);
        }
        this.marker = L.marker([lat, lon]).addTo(this.map);
        if (popup) {
            this.marker.bindPopup(popup).openPopup();
        }
    },

    showGeoJson: function (geoJson) {
        if (this.geoJsonLayer) {
            this.map.removeLayer(this.geoJsonLayer);
        }
        this.geoJsonLayer = L.geoJSON(geoJson, {
            style: {
                color: '#3388ff',
                weight: 2,
                opacity: 0.8,
                fillOpacity: 0.15
            }
        }).addTo(this.map);
        this.map.fitBounds(this.geoJsonLayer.getBounds(), { padding: [20, 20] });
    },

    flyTo: function (lat, lon, zoom) {
        if (this.map) {
            this.map.flyTo([lat, lon], zoom);
        }
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
