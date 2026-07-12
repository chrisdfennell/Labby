// Top-nav overflow handling: the menu scrolls horizontally when it doesn't fit,
// so translate the mouse wheel into horizontal scrolling, toggle the edge-fade
// hints, and keep the active page's link scrolled into view.
(() => {
    const nav = () => document.querySelector(".topnav");

    function updateFades() {
        const el = nav();
        if (!el) return;
        const max = el.scrollWidth - el.clientWidth;
        el.parentElement.classList.toggle("fade-left", el.scrollLeft > 4);
        el.parentElement.classList.toggle("fade-right", el.scrollLeft < max - 4);
    }

    document.addEventListener("wheel", e => {
        const el = e.target instanceof Element && e.target.closest(".topnav");
        if (!el || el.scrollWidth <= el.clientWidth) return;
        if (Math.abs(e.deltaX) > Math.abs(e.deltaY)) return; // already horizontal (trackpad)
        el.scrollLeft += e.deltaMode === WheelEvent.DOM_DELTA_LINE ? e.deltaY * 24 : e.deltaY;
        e.preventDefault();
    }, { passive: false });

    document.addEventListener("scroll", e => {
        if (e.target instanceof Element && e.target.classList.contains("topnav")) updateFades();
    }, true);

    window.addEventListener("resize", updateFades);

    function showActive() {
        const active = nav()?.querySelector(".nav-link.active");
        if (active) active.scrollIntoView({ block: "nearest", inline: "nearest" });
        updateFades();
    }

    showActive();
    // Enhanced navigation patches the nav DOM (active link moves, scroll may reset).
    window.Blazor?.addEventListener("enhancedload", showActive);
})();
