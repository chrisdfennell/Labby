// Wishlist embed helper. The wishlist app ships a host-page script that grows
// the frame to fit the list (no box-inside-a-box scroll) and tells the app which
// slice of the frame is on screen, so its dialogs open where the reader looks.
// A cross-origin frame cannot do either from the inside, hence the script.
//
// It lives on the wishlist's own origin, which is configuration (Wishlist:Url),
// not a constant — so read the origin off the frame and inject the script the
// first time the Wishlist page renders rather than pulling a third-party script
// into every page of Labby.
(() => {
    const loaded = new Set();

    function inject() {
        for (const frame of document.querySelectorAll("iframe[data-wishlist]")) {
            if (!frame.src) continue;
            const origin = new URL(frame.src, location.href).origin;
            if (loaded.has(origin)) continue;
            loaded.add(origin);

            const script = document.createElement("script");
            script.src = `${origin}/static/wishlist-embed.js`;
            document.body.appendChild(script);
        }
    }

    inject();
    // Static SSR + enhanced navigation: the frame arrives with the swapped DOM.
    // The helper's listeners outlive the swap, so injecting once is enough.
    window.Blazor?.addEventListener("enhancedload", inject);
})();
