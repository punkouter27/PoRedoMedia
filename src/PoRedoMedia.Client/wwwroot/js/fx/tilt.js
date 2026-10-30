// Library cards lean toward a mouse pointer and catch a light where it is. This only sets where
// the pointer is over the card (--tilt-x, --tilt-y, --light-x, --light-y) and marks the card
// .po-tilt; the lean and the light are drawn by the stylesheet, which also leaves them out for
// reduced motion and on screens without hover. One listener on the document serves every card.
window.poTilt = (() => {
    const MOST = 6;  // degrees of lean at the card's edge
    let card = null, pointer = null, frame = 0;

    function apply() {
        frame = 0;
        if (!card || !pointer) return;
        const r = card.getBoundingClientRect();
        const x = Math.min(1, Math.max(0, (pointer.clientX - r.left) / r.width));
        const y = Math.min(1, Math.max(0, (pointer.clientY - r.top) / r.height));
        // A re-render may have rewritten the card's classes.
        card.classList.add('po-tilt');
        card.style.setProperty('--tilt-x', `${((0.5 - y) * MOST * 2).toFixed(2)}deg`);
        card.style.setProperty('--tilt-y', `${((x - 0.5) * MOST * 2).toFixed(2)}deg`);
        card.style.setProperty('--light-x', `${(x * 100).toFixed(1)}%`);
        card.style.setProperty('--light-y', `${(y * 100).toFixed(1)}%`);
    }

    function release() {
        if (!card) return;
        card.classList.remove('po-tilt');
        for (const name of ['--tilt-x', '--tilt-y', '--light-x', '--light-y']) card.style.removeProperty(name);
        card = null;
    }

    document.addEventListener('pointermove', (e) => {
        if (e.pointerType !== 'mouse' || !window.poFx.enabled()) { release(); return; }
        const hit = e.target.closest?.('.gallery-card') ?? null;
        if (hit !== card) { release(); card = hit; }
        pointer = e;
        if (card && !frame) frame = requestAnimationFrame(apply);
    }, { passive: true });
    // Leaving the window.
    document.addEventListener('mouseout', (e) => { if (!e.relatedTarget) release(); });

    return { release };
})();
