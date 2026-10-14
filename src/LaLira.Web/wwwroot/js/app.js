// Small helpers called from Blazor through JS interop.
window.lalira = {
    scrollToTop: () => window.scrollTo({ top: 0, behavior: "instant" }),

    // Per-browser preferences. Storage can be unavailable (private mode, blocked site data),
    // so failures fall back to the default instead of breaking the page.
    getPref: (key) => {
        try { return localStorage.getItem("lalira." + key); } catch { return null; }
    },
    setPref: (key, value) => {
        try { localStorage.setItem("lalira." + key, value); } catch { }
    },

    // The browser's language and time zone, for a new user's first settings.
    browserLocale: () => [
        navigator.language || "en",
        (() => { try { return Intl.DateTimeFormat().resolvedOptions().timeZone || ""; } catch { return ""; } })(),
    ],

    // True on an iPhone or iPad in Safari that isn't running LaLira from the home screen yet, so the
    // page can explain "Add to Home Screen" (iOS has no install prompt of its own). iPadOS reports
    // itself as a Mac, so a touch-capable "Mac" counts too.
    isIosBrowser: () => {
        const ios = /iPhone|iPad|iPod/.test(navigator.userAgent)
            || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);
        return ios && !navigator.standalone;
    },

    // Resolves to false when the browser refuses (no permission, insecure context).
    copyText: async (text) => {
        try { await navigator.clipboard.writeText(text); return true; } catch { return false; }
    },

    // Saves a file the server sent as a stream (CSV export, backup download).
    download: async (fileName, contentType, streamRef) => {
        const buffer = await streamRef.arrayBuffer();
        const url = URL.createObjectURL(new Blob([buffer], { type: contentType }));
        const a = document.createElement("a");
        a.href = url;
        a.download = fileName;
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(() => URL.revokeObjectURL(url), 10000);
    },

    // Opens the browser's date picker of a (hidden) date input; older browsers just focus it.
    showPicker: (input) => {
        try { input.showPicker(); } catch { input.focus(); }
    },

    focus: (selector) => document.querySelector(selector)?.focus(),

    // Scrolls an element into view, for a year opened in the account list.
    scrollIntoView: (selector) => document.querySelector(selector)?.scrollIntoView({ block: "nearest", behavior: "smooth" }),
};
