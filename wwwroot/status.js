// Favicon down-badge: polls the status summary and overlays a red dot on the
// tab icon whenever any monitored service is down.
(() => {
    let badged = false;
    let baseImage = null;

    const links = () => document.querySelectorAll('link[rel="icon"], link[rel="alternate icon"]');
    const originals = [];
    document.addEventListener("DOMContentLoaded", () => {
        links().forEach(l => originals.push({ el: l, href: l.href, type: l.type }));
    });

    function setBadge(on) {
        if (on === badged) return;
        badged = on;
        if (!on) {
            originals.forEach(o => { o.el.href = o.href; if (o.type) o.el.type = o.type; });
            return;
        }
        if (!baseImage) {
            baseImage = new Image();
            baseImage.src = "/favicon.png";
            baseImage.onload = () => badged && draw();
            return;
        }
        draw();
    }

    function draw() {
        const canvas = document.createElement("canvas");
        canvas.width = canvas.height = 64;
        const ctx = canvas.getContext("2d");
        ctx.drawImage(baseImage, 0, 0, 64, 64);
        ctx.beginPath();
        ctx.arc(50, 14, 12, 0, Math.PI * 2);
        ctx.fillStyle = "#f4587a";
        ctx.fill();
        ctx.lineWidth = 3;
        ctx.strokeStyle = "#0d131b";
        ctx.stroke();
        const url = canvas.toDataURL("image/png");
        links().forEach(l => { l.href = url; l.type = "image/png"; });
    }

    async function poll() {
        try {
            const res = await fetch("/api/status/summary");
            if (!res.ok) return;
            const { down } = await res.json();
            setBadge(down > 0);
        } catch { /* offline or navigating — try again next tick */ }
    }

    poll();
    setInterval(poll, 60000);
})();
