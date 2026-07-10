// Kiosk / TV mode: hides the chrome and rotates through the configured pages.
// Started from Settings; Esc exits.
window.labbyKiosk = {
    _timer: null,

    start(pages, seconds) {
        localStorage.setItem("labby-kiosk", JSON.stringify({ pages, seconds }));
        location.assign("/" + (pages[0] ?? ""));
    },

    stop() {
        localStorage.removeItem("labby-kiosk");
        if (this._timer) clearTimeout(this._timer);
        document.documentElement.classList.remove("kiosk");
    },

    active() {
        return localStorage.getItem("labby-kiosk") !== null;
    },

    _boot() {
        const raw = localStorage.getItem("labby-kiosk");
        if (!raw) return;
        let config;
        try { config = JSON.parse(raw); } catch { return; }
        if (!config.pages?.length) return;

        document.documentElement.classList.add("kiosk");
        const current = location.pathname.replace(/^\//, "");
        const index = config.pages.indexOf(current);
        const next = config.pages[(index + 1) % config.pages.length];
        this._timer = setTimeout(() => location.assign("/" + next), Math.max(5, config.seconds || 20) * 1000);

        document.addEventListener("keydown", e => {
            if (e.key === "Escape") {
                this.stop();
                location.reload();
            }
        });
    },
};
window.labbyKiosk._boot();
