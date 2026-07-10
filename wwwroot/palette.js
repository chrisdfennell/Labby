// Ctrl+K / Cmd+K opens the command palette (CommandPalette.razor).
window.labbyPalette = {
    init(dotnetRef) {
        this._ref = dotnetRef;
        if (this._bound) return;
        this._bound = true;
        document.addEventListener("keydown", e => {
            if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") {
                e.preventDefault();
                this._ref?.invokeMethodAsync("Open");
            }
        });
    },
    open(url) {
        window.open(url, "_blank", "noopener");
    },
};
