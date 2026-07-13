// Pinned-note collapse persistence — same pattern as labbyTheme.
window.labbyPinned = {
    setCollapsed(value) {
        localStorage.setItem("labby-pinned-collapsed", value ? "1" : "0");
    },
    collapsed() {
        return localStorage.getItem("labby-pinned-collapsed") === "1";
    },
};
