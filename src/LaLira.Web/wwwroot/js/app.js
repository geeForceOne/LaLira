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
    // every move, so it follows Blazor's re-renders; the listeners are added once. The tip is fixed
    // to the window (the boxes around a chart clip what sticks out) and kept inside it.
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
            // Stacked charts: the month and total, then each account with a color dot.
            const heads = plot.dataset.heads ? JSON.parse(plot.dataset.heads) : null;
            if (heads) {
                const rows = JSON.parse(plot.dataset.rows || "[]")[i] || [];
                const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; };
                const head = el("div", "tip-head");
                head.append(el("span", null, heads[i][0]), el("span", "num", heads[i][1]));
                const grid = el("div", "tip-rows");
                for (const [name, color, value] of rows) {
                    const label = el("span", "tip-row");
                    label.style.setProperty("--acct", `var(--c-${color})`);
                    label.append(el("i"), el("span", null, name));
                    grid.append(label, el("span", "num", value));
                }
                tip.replaceChildren(head, grid);
            } else {
                tip.textContent = labels[i];
            }
            cursor.hidden = dot.hidden = tip.hidden = false;
            const margin = 8, gap = 12, w = tip.offsetWidth, h = tip.offsetHeight;
            const px = r.left + left / 100 * r.width;
            let tx, ty;
            if (heads) {
                // Too tall to sit above the chart: beside the cursor, on the side with room.
                tx = px + gap + w <= innerWidth - margin ? px + gap : px - gap - w;
                ty = r.top;
            } else {
                // Centered above the point.
                tx = px - w / 2;
                ty = r.top + ys[i] / 100 * r.height - h - gap;
                if (ty < margin) ty = r.top + ys[i] / 100 * r.height + gap;
            }
            tip.style.left = Math.max(margin, Math.min(tx, innerWidth - margin - w)) + "px";
            tip.style.top = Math.max(margin, Math.min(ty, innerHeight - margin - h)) + "px";
        };
        const hide = () => plot.querySelectorAll(".bchart-cursor, .bchart-dot, .bchart-tip").forEach(e => e.hidden = true);
        plot.addEventListener("pointermove", show);
        plot.addEventListener("pointerdown", show);
        plot.addEventListener("pointerleave", hide);
        addEventListener("scroll", hide, { passive: true });
    },

    // Calls the page's OnVisibleAsync whenever its tab is shown again, so what changed meanwhile
    // (in another tab, by the daily price update) appears. Ends by itself once the page is gone.
    onVisible: (page) => {
        const changed = () => {
            if (document.visibilityState !== "visible") return;
            page.invokeMethodAsync("OnVisibleAsync").catch(() => document.removeEventListener("visibilitychange", changed));
        };
        document.addEventListener("visibilitychange", changed);
    },

    focus: (selector) => document.querySelector(selector)?.focus(),

    // Scrolls an element into view, for a year opened in the account list.
    scrollIntoView: (selector) => document.querySelector(selector)?.scrollIntoView({ block: "nearest", behavior: "smooth" }),
};
