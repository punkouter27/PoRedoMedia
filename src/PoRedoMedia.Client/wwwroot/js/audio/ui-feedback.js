// Spatial screen-anchored AudioWorklet UI feedback + FLIP grid motion helper.
// Respects the app's muted default ('poredomedia:sfx:muted' !== '0') and prefers-reduced-motion.
window.poUiAudio = (() => {
    let ctx = null, node = null, panner = null, ready = false, loading = null;
    const SCALE = [523.25, 587.33, 659.25, 783.99, 880, 1046.5];
    let stepIdx = 0;

    const muted = () => window.poMedia?.read?.('poredomedia:sfx:muted') !== '0';

    async function ensure() {
        if (muted() || typeof AudioWorkletNode === 'undefined') return false;
        if (ready && ctx) {
            if (ctx.state === 'suspended') ctx.resume().catch(() => { });
            return true;
        }
        loading ??= (async () => {
            try {
                ctx = new AudioContext();
                await ctx.audioWorklet.addModule('js/audio/ui-worklet.js');
                node = new AudioWorkletNode(ctx, 'po-ui-feedback');
                panner = ctx.createPanner();
                panner.panningModel = 'HRTF';
                panner.positionZ.value = -1.0;
                node.connect(panner).connect(ctx.destination);
                ready = true;
            } catch {
                ready = false;
            }
        })();
        await loading;
        return ready;
    }

    function panTo(xRatio = 0.5) {
        if (!panner || !ctx) return;
        const x = (Math.max(0, Math.min(1, xRatio)) - 0.5) * 3.0;
        panner.positionX.setTargetAtTime(x, ctx.currentTime, 0.01);
    }

    async function trigger(kind, xRatio = 0.5, freq = 920, gain = 0.07) {
        if (!(await ensure()) || !node) return;
        panTo(xRatio);
        node.port.postMessage({ kind, freq, gain });
    }

    function ratioFor(el) {
        if (!el || !el.getBoundingClientRect) return 0.5;
        const r = el.getBoundingClientRect();
        return (r.left + r.width * 0.5) / Math.max(1, window.innerWidth);
    }

    // Passive tactile cues on Radzen buttons & switches (never blocks UI).
    document.addEventListener('pointerdown', (e) => {
        const hit = e.target?.closest?.('.rz-button, .rz-switch, .rz-chkbox');
        if (!hit || hit.classList.contains('rz-state-disabled')) return;
        trigger('detent', ratioFor(hit), 740, 0.06);
    }, { passive: true });

    function chimeAt(xRatio = 0.5) {
        const freq = SCALE[stepIdx++ % SCALE.length];
        trigger('chime', xRatio, freq, 0.09);
    }

    return { trigger, chimeAt };
})();

// Lightweight FLIP animation helper for LibraryGrid filter/sort transitions.
window.poFlip = (() => {
    const rects = new Map();
    function capture(selector) {
        rects.clear();
        if (!window.poFx?.enabled?.()) return;
        for (const el of document.querySelectorAll(selector)) {
            const id = el.dataset.id;
            if (id) rects.set(id, el.getBoundingClientRect());
        }
    }
    function play(selector) {
        if (!window.poFx?.enabled?.() || rects.size === 0) return;
        for (const el of document.querySelectorAll(selector)) {
            const prev = rects.get(el.dataset.id);
            if (!prev) continue;
            const next = el.getBoundingClientRect();
            const dx = prev.left - next.left, dy = prev.top - next.top;
            if (Math.abs(dx) < 1 && Math.abs(dy) < 1) continue;
            el.animate([
                { transform: `translate3d(${dx}px, ${dy}px, 0)` },
                { transform: 'translate3d(0, 0, 0)' },
            ], { duration: 240, easing: 'cubic-bezier(0.22, 1, 0.36, 1)' });
        }
        rects.clear();
    }
    return { capture, play };
})();
