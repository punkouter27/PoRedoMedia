// A live waveform under a track while it plays (a roast, a Lyria track, any audio result). The
// page puts an empty canvas.po-wave right after each player; this fills it. Bars scroll past a
// playhead in the middle: four seconds of the track around "now", each bar as tall as the track's
// bass, mids and highs there, the part already heard in the theme's colours and the part to come
// faded. The levels come from poFx.bands (decoded once, analysed in fft-worker.js), so nothing is
// routed through the player and playback is never touched. Kicks make the playhead flare.
window.poWave = (() => {
    const WINDOW = 4;  // seconds shown across the canvas
    const BARS = 96;

    function draw(canvas, audio, state) {
        const density = window.poFx.fit(canvas, 2);
        const g = canvas.getContext('2d');
        const w = canvas.width, h = canvas.height, mid = h / 2;
        g.clearRect(0, 0, w, h);
        const at = audio.currentTime || 0;
        const levels = state.levels;
        const step = w / BARS;
        let kick = 0;
        for (let i = 0; i < BARS; i++) {
            const t = at + (i / BARS - 0.5) * WINDOW;
            let level = 0.04;
            if (levels && t >= 0) {
                const f = Math.min(levels.bass.length - 1, Math.floor(t * levels.rate));
                level = Math.max(0.04, levels.bass[f] * 0.5 + levels.mid[f] * 0.35 + levels.high[f] * 0.15);
                if (i === BARS / 2) kick = levels.kick[f] || 0;
            } else if (!levels) {
                // Still analysing: a gentle ripple, so the player does not look broken.
                level = 0.06 + 0.04 * Math.sin(performance.now() / 300 + i * 0.4);
            }
            const heard = i < BARS / 2;
            g.fillStyle = heard ? state.gradient : state.ahead;
            const bar = Math.max(1, level * (h * 0.9));
            g.fillRect(i * step + step * 0.2, mid - bar / 2, Math.max(1, step * 0.6), bar);
        }
        g.fillStyle = state.head;
        g.globalAlpha = 0.6 + kick * 0.4;
        g.fillRect(w / 2 - density, 0, density * 2 * (1 + kick * 1.5), h);
        g.globalAlpha = 1;
    }

    function attach(audio) {
        const canvas = audio.nextElementSibling;
        if (!canvas?.matches?.('canvas.po-wave') || !window.poFx.enabled()) return;
        let state = canvas._poWave;
        if (!state || state.audio !== audio) {
            const g = canvas.getContext('2d');
            const [a, b] = [window.poFx.colour('--po-brand-a', '#4340d2'), window.poFx.colour('--po-brand-b', '#7c3aed')];
            const css = (c, alpha = 1) => `rgb(${c.map((v) => Math.round(v * 255)).join(' ')} / ${alpha})`;
            const gradient = g.createLinearGradient(0, 0, canvas.clientWidth * 2, 0);
            gradient.addColorStop(0, css(a));
            gradient.addColorStop(1, css(b));
            state = canvas._poWave = { audio, frame: 0, levels: null, gradient, ahead: css(b, 0.28), head: window.poFx.dark() ? '#ffffff' : css(a) };
            const source = audio.currentSrc || audio.src;
            window.poFx.bands(source, 50).then((levels) => { state.levels = levels; }).catch(() => { state.levels = null; });
        }
        const tick = () => {
            if (!canvas.isConnected) { state.frame = 0; return; }
            draw(canvas, audio, state);
            state.frame = audio.paused || document.hidden ? 0 : requestAnimationFrame(tick);
        };
        if (!state.frame) state.frame = requestAnimationFrame(tick);
    }

    // 'play' does not bubble, so it is caught on the way down.
    document.addEventListener('play', (e) => { if (e.target instanceof HTMLAudioElement) attach(e.target); }, true);
    document.addEventListener('seeked', (e) => { if (e.target instanceof HTMLAudioElement && e.target.paused) attach(e.target); }, true);
    document.addEventListener('visibilitychange', () => {
        if (!document.hidden) document.querySelectorAll('canvas.po-wave').forEach((c) => c._poWave && !c._poWave.audio.paused && attach(c._poWave.audio));
    });

    return { attach };
})();
