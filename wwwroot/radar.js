// Animated precipitation radar for the Weather page: Leaflet + CARTO dark
// basemap + RainViewer frames (last 2h of radar plus a short nowcast).
window.labbyRadar = {
    _map: null,
    _timer: null,

    async init(elementId, lat, lon) {
        this.destroy();
        const el = document.getElementById(elementId);
        if (!el || typeof L === "undefined") return;

        const map = L.map(el, { zoomControl: true, attributionControl: true }).setView([lat, lon], 7);
        this._map = map;

        L.tileLayer("https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png", {
            attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OSM</a> &copy; <a href="https://carto.com/attributions">CARTO</a> · <a href="https://rainviewer.com">RainViewer</a>',
            subdomains: "abcd",
            maxZoom: 12,
        }).addTo(map);

        L.circleMarker([lat, lon], {
            radius: 6, color: "#2dd4a7", fillColor: "#2dd4a7", fillOpacity: 0.9, weight: 2,
        }).addTo(map).bindTooltip("Weather station");

        let frames;
        try {
            const res = await fetch("https://api.rainviewer.com/public/weather-maps.json");
            const data = await res.json();
            // RainViewer's free tile cache tops out at z7; upscale past that.
            frames = [...(data.radar?.past ?? []), ...(data.radar?.nowcast ?? [])]
                .map(f => ({ time: f.time, layer: L.tileLayer(`${data.host}${f.path}/256/{z}/{x}/{y}/2/1_1.png`, { opacity: 0, maxNativeZoom: 7, maxZoom: 12 }) }));
            frames.forEach(f => f.layer.addTo(map));
        } catch {
            el.insertAdjacentHTML("beforeend",
                '<div class="radar-error">RainViewer is unreachable — radar frames unavailable.</div>');
            return;
        }
        if (frames.length === 0) return;

        const label = document.getElementById(elementId + "-time");
        const button = document.getElementById(elementId + "-play");
        let index = frames.length - 1;
        let playing = true;

        const show = i => {
            frames.forEach((f, n) => f.layer.setOpacity(n === i ? 0.75 : 0));
            if (label) {
                const when = new Date(frames[i].time * 1000);
                const isForecast = when > new Date();
                label.textContent = `${when.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}${isForecast ? " (forecast)" : ""}`;
            }
        };
        show(index);

        const tick = () => { index = (index + 1) % frames.length; show(index); };
        this._timer = setInterval(tick, 600);

        button?.addEventListener("click", () => {
            playing = !playing;
            button.textContent = playing ? "⏸" : "▶";
            if (playing) this._timer = setInterval(tick, 600);
            else { clearInterval(this._timer); this._timer = null; }
        });
    },

    destroy() {
        if (this._timer) { clearInterval(this._timer); this._timer = null; }
        if (this._map) { this._map.remove(); this._map = null; }
    },
};
