// Animates the logo (Components/Shared/BrandMark.razor). Four motions, picked at random for each
// play and never the same twice in a row:
//   draw  - the balance line draws itself, each value pops in, the coin lands on top;
//   roll  - the coin rolls up the line from the first value, lighting each one it passes;
//   tally - the double rule is drawn under the total, then the coin swells once;
//   flip  - the coin flips twice, like one tossed on a counter.
// The sidebar and sign-in logos play one motion when hovered or focused; a mark with
// data-motion="once" plays one shortly after it appears, and one with data-motion="loop" keeps
// playing while it's on the page. Nothing moves when the user asks for reduced motion. Only the
// Web Animations API is used: no attribute Blazor renders is changed.
(() => {
    const LOOP_PAUSE = 700;
    const reduced = matchMedia("(prefers-reduced-motion: reduce)");
    const playing = new WeakSet();
    const started = new WeakSet();
    let lastMotion = null;
    const backOut = "cubic-bezier(.34,1.56,.64,1)";

    const parts = svg => {
        const q = s => [...svg.querySelectorAll(s)];
        const fill = el => { el.style.transformBox = "fill-box"; el.style.transformOrigin = "center"; return el; };
        return {
            rules: q(".bm-rule"),
            trend: svg.querySelector(".bm-trend"),
            nodes: q(".bm-node").map(fill),
            glows: q(".bm-glow").map(fill),
            halo: fill(svg.querySelector(".bm-halo")),
            coin: fill(svg.querySelector(".bm-coin")),
            points: svg.querySelector(".bm-trend").getAttribute("points").trim().split(/\s+/).map(p => p.split(",").map(Number)),
        };
    };

    const done = anims => Promise.all(anims.map(a => a.finished.catch(() => { })));
    const drawIn = (el, opts) => {
        const len = el.getTotalLength();
        el.style.strokeDasharray = len;
        return el.animate([{ strokeDashoffset: len }, { strokeDashoffset: 0 }], { fill: "backwards", easing: "ease-in-out", ...opts });
    };
    const pop = (el, delay, duration = 420) =>
        el.animate([{ transform: "scale(0)" }, { transform: "scale(1)" }], { duration, delay, easing: backOut, fill: "backwards" });
    const glow = (el, delay, duration = 500) =>
        el.animate([{ opacity: 0 }, { opacity: 1, offset: 0.3 }, { opacity: 0 }], { duration, delay });

    const MOTIONS = {
        draw: m => done([
            drawIn(m.trend, { duration: 900 }),
            ...m.nodes.map((n, i) => pop(n, 120 + i * (700 / Math.max(1, m.nodes.length)))),
            pop(m.coin, 850, 520),
            pop(m.halo, 900, 600),
        ]),
        roll: m => {
            const [ex, ey] = m.points[m.points.length - 1];
            const last = m.points.length - 1;
            const frames = m.points.map(([x, y], i) => ({ transform: `translate(${x - ex}px, ${y - ey}px) rotate(${i * 90}deg)`, offset: i / last }));
            const step = 1500 / last;
            return done([
                m.coin.animate(frames, { duration: 1500, easing: "ease-in-out" }),
                m.halo.animate([{ opacity: 0 }, { opacity: 0, offset: 0.85 }, { opacity: 1 }], { duration: 1500 }),
                ...m.glows.map((g, i) => glow(g, i * step, 520)),
            ]);
        },
        tally: m => done([
            ...m.rules.map((r, i) => drawIn(r, { duration: 520, delay: i * 260 })),
            m.coin.animate([{ transform: "scale(1)" }, { transform: "scale(1.4)" }, { transform: "scale(1)" }], { duration: 650, delay: 760, easing: "ease-out" }),
            m.halo.animate([{ transform: "scale(1)", opacity: 1 }, { transform: "scale(1.9)", opacity: 0 }], { duration: 750, delay: 760 }),
        ]),
        flip: m => done([
            m.coin.animate([{ transform: "scaleX(1)" }, { transform: "scaleX(0.08)" }, { transform: "scaleX(1)" }, { transform: "scaleX(0.08)" }, { transform: "scaleX(1)" }], { duration: 900, easing: "ease-in-out" }),
            ...m.glows.map((g, i) => glow(g, 300 + i * 90, 400)),
        ]),
    };

    const pick = () => {
        const names = Object.keys(MOTIONS).filter(n => n !== lastMotion);
        return lastMotion = names[Math.floor(Math.random() * names.length)];
    };

    async function play(svg) {
        if (reduced.matches || playing.has(svg) || !svg.isConnected) return;
        playing.add(svg);
        try { await MOTIONS[pick()](parts(svg)); }
        catch { }
        finally { playing.delete(svg); }
    }

    async function loop(svg) {
        while (svg.isConnected && svg.dataset.motion === "loop" && !reduced.matches) {
            await play(svg);
            await new Promise(r => setTimeout(r, LOOP_PAUSE));
        }
        started.delete(svg);
    }

    function scan() {
        for (const svg of document.querySelectorAll("svg.brand-mark[data-motion]")) {
            if (started.has(svg)) continue;
            started.add(svg);
            if (svg.dataset.motion === "loop") loop(svg);
            else setTimeout(() => play(svg), 350);
        }
    }

    const trigger = e => {
        const host = e.target.closest?.(".brand, .auth-brand");
        // Only when the pointer comes in from outside, not when it moves between the logo's parts.
        if (!host || (e.relatedTarget && host.contains(e.relatedTarget))) return;
        const svg = host.querySelector("svg.brand-mark");
        if (svg) play(svg);
    };
    document.addEventListener("pointerover", trigger);
    document.addEventListener("focusin", trigger);

    new MutationObserver(scan).observe(document.documentElement, { childList: true, subtree: true });
    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", scan); else scan();
})();
