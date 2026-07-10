// Accent theme persistence — applied before first paint to avoid a flash.
window.labbyTheme = {
    apply(name) {
        document.documentElement.dataset.accent = name || "green";
    },
    set(name) {
        localStorage.setItem("labby-accent", name);
        this.apply(name);
    },
    get() {
        return localStorage.getItem("labby-accent") || "green";
    },
};
window.labbyTheme.apply(window.labbyTheme.get());
