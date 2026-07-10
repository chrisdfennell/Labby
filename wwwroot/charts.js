// Crosshair + tooltip for the .lab-chart SVGs rendered by LineChart.razor.
// Event delegation on document so it survives Blazor re-renders and navigation.
(() => {
    let tip = null;
    let active = null;

    function tooltip() {
        if (!tip) {
            tip = document.createElement("div");
            tip.className = "chart-tooltip";
            document.body.appendChild(tip);
        }
        return tip;
    }

    function hide() {
        if (active) {
            active.querySelector(".chart-cursor")?.setAttribute("opacity", "0");
            active.querySelectorAll(".chart-cursor-dot").forEach(d => d.setAttribute("opacity", "0"));
            active = null;
        }
        if (tip) tip.style.display = "none";
    }

    document.addEventListener("pointermove", e => {
        const chart = e.target.closest?.(".lab-chart");
        if (!chart) { hide(); return; }
        if (active && active !== chart) hide();
        active = chart;

        let meta;
        try { meta = JSON.parse(chart.dataset.chart); } catch { return; }
        const svg = chart.querySelector("svg");
        if (!svg || !meta.t?.length) return;

        // The crosshair finds the X: snap the pointer to the nearest sample.
        const rect = svg.getBoundingClientRect();
        const vx = (e.clientX - rect.left) / (rect.width / meta.w);
        const plotW = meta.w - meta.l - meta.r;
        const n = meta.t.length;
        const i = Math.max(0, Math.min(n - 1, Math.round((vx - meta.l) / plotW * (n - 1))));
        const x = meta.l + plotW * i / (n - 1);
        const yFor = v => meta.h - meta.pb - (v - meta.min) / (meta.max - meta.min) * (meta.h - meta.pt - meta.pb);

        const cursor = svg.querySelector(".chart-cursor");
        cursor.setAttribute("x1", x);
        cursor.setAttribute("x2", x);
        cursor.setAttribute("opacity", "1");

        const dots = svg.querySelectorAll(".chart-cursor-dot");
        meta.v.forEach((series, s) => {
            const dot = dots[s];
            if (!dot) return;
            const val = series[i];
            if (val === null || val === undefined) {
                dot.setAttribute("opacity", "0");
                return;
            }
            dot.setAttribute("cx", x);
            dot.setAttribute("cy", yFor(val));
            dot.setAttribute("opacity", "1");
        });

        // One tooltip, every series; values lead, labels follow; names via textContent.
        const t = tooltip();
        t.replaceChildren();
        const when = document.createElement("div");
        when.className = "chart-tooltip-time";
        when.textContent = new Date(meta.t[i]).toLocaleString([], meta.wide
            ? { weekday: "short", hour: "2-digit", minute: "2-digit" }
            : { hour: "2-digit", minute: "2-digit" });
        t.appendChild(when);
        if (meta.pl && meta.pl[i]) {
            const extra = document.createElement("div");
            extra.className = "chart-tooltip-time";
            extra.textContent = meta.pl[i];
            t.appendChild(extra);
        }
        meta.s.forEach((name, s) => {
            const row = document.createElement("div");
            row.className = "chart-tooltip-row";
            const key = document.createElement("span");
            key.className = "chart-key-line";
            key.style.background = meta.c[s];
            const value = document.createElement("strong");
            const val = meta.v[s][i];
            value.textContent = (val === null || val === undefined)
                ? "—" : `${Math.round(val * 10) / 10}${meta.u}`;
            const label = document.createElement("span");
            label.className = "chart-tooltip-label";
            label.textContent = name;
            row.append(key, value, label);
            t.appendChild(row);
        });
        t.style.display = "block";
        const tw = t.offsetWidth;
        const flip = e.clientX + 18 + tw > window.innerWidth;
        t.style.left = `${flip ? e.clientX - tw - 14 : e.clientX + 14}px`;
        t.style.top = `${e.clientY - 12}px`;
    });

    document.addEventListener("pointerdown", e => {
        if (!e.target.closest?.(".lab-chart")) hide();
    });
})();
