// In-browser container shell: xterm.js on top of the /ws/shell/{id} bridge.
// Binary frames carry TTY bytes both ways; a JSON text frame carries resize.
window.labbyShell = {
    _ws: null,
    _term: null,

    init(elementId, containerId) {
        this.dispose();
        const el = document.getElementById(elementId);
        if (!el || typeof Terminal === "undefined")
            return;

        const term = new Terminal({
            cursorBlink: true,
            fontSize: 14,
            fontFamily: "'Cascadia Mono', 'JetBrains Mono', Consolas, monospace",
            theme: {
                background: "#060a0f",
                foreground: "#d8e2ec",
                cursor: "#12a37e",
                selectionBackground: "#1d4c40",
            },
        });
        const fit = new FitAddon.FitAddon();
        term.loadAddon(fit);
        term.open(el);
        fit.fit();
        term.focus();

        const proto = location.protocol === "https:" ? "wss" : "ws";
        const ws = new WebSocket(`${proto}://${location.host}/ws/shell/${encodeURIComponent(containerId)}`);
        ws.binaryType = "arraybuffer";

        const sendResize = () => {
            if (ws.readyState === WebSocket.OPEN)
                ws.send(JSON.stringify({ cols: term.cols, rows: term.rows }));
        };

        ws.onopen = () => sendResize();
        ws.onmessage = e => term.write(new Uint8Array(e.data));
        ws.onclose = () => term.write("\r\n\x1b[90m[session ended — reload the page for a new shell]\x1b[0m\r\n");
        ws.onerror = () => term.write("\r\n\x1b[31m[connection failed]\x1b[0m\r\n");

        const encoder = new TextEncoder();
        term.onData(data => {
            if (ws.readyState === WebSocket.OPEN)
                ws.send(encoder.encode(data));
        });
        term.onResize(sendResize);

        this._onWindowResize = () => { fit.fit(); };
        window.addEventListener("resize", this._onWindowResize);
        this._ws = ws;
        this._term = term;
    },

    dispose() {
        if (this._onWindowResize)
            window.removeEventListener("resize", this._onWindowResize);
        try { this._ws?.close(); } catch { /* already closed */ }
        this._term?.dispose();
        this._ws = null;
        this._term = null;
    },
};
