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

    // Hover for the account's balance chart (Components/Shared/BalanceChart.razor): a crosshair, a dot
    // on the line and the month's balance. Reads the points from the element's data attributes on
    // every move, so it follows Blazor's re-renders; the listeners are added once.
    balanceChart: (plot) => {
        if (!plot || plot._lalira) return;
        plot._lalira = true;
        const part = c => plot.querySelector(c);
        const show = ev => {
            const labels = JSON.parse(plot.dataset.labels || "[]"), ys = JSON.parse(plot.dataset.ys || "[]");
            if (labels.length < 2) return;
            const r = plot.getBoundingClientRect();
            const x = ((ev.touches ? ev.touches[0].clientX : ev.clientX) - r.left) / r.width;
            const i = Math.max(0, Math.min(labels.length - 1, Math.round(x * (labels.length - 1))));
            const left = i * 100 / (labels.length - 1);
            const cursor = part(".bchart-cursor"), dot = part(".bchart-dot"), tip = part(".bchart-tip");
            cursor.style.left = left + "%";
            dot.style.left = left + "%";
            dot.style.top = ys[i] + "%";
            tip.textContent = labels[i];
            tip.style.left = Math.min(Math.max(left, 18), 82) + "%";
            cursor.hidden = dot.hidden = tip.hidden = false;
        };
        const hide = () => plot.querySelectorAll(".bchart-cursor, .bchart-dot, .bchart-tip").forEach(e => e.hidden = true);
        plot.addEventListener("pointermove", show);
        plot.addEventListener("pointerdown", show);
        plot.addEventListener("pointerleave", hide);
    },

    focus: (selector) => document.querySelector(selector)?.focus(),

    // Scrolls an element into view, for a year opened in the account list.
    scrollIntoView: (selector) => document.querySelector(selector)?.scrollIntoView({ block: "nearest", behavior: "smooth" }),
};
