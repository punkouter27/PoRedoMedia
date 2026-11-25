// Tactical Stage Entity FSM + Steering Behaviors Web Worker (60 Hz, <0.4 ms/frame).
// Uses Structure-of-Arrays (SoA) Float32Arrays for cache-friendly Idle -> Flank -> Drop -> Scatter
// movement synced to RoastStage beat energy and kick impulses.
const COUNT = 64;
const STATE_IDLE = 0, STATE_FLANK = 1, STATE_DROP = 2, STATE_SCATTER = 3;

const posX = new Float32Array(COUNT);
const posY = new Float32Array(COUNT);
const velX = new Float32Array(COUNT);
const velY = new Float32Array(COUNT);
const state = new Uint8Array(COUNT);

for (let i = 0; i < COUNT; i++) {
    posX[i] = Math.random();
    posY[i] = Math.random();
    state[i] = STATE_IDLE;
}

self.onmessage = (event) => {
    const { dt = 0.016, beat = 0, kick = false, targetX = 0.5, targetY = 0.45 } = event.data || {};
    const step = Math.min(0.05, Math.max(0.004, dt));

    for (let i = 0; i < COUNT; i++) {
        // FSM transitions driven by audio beat & kick events.
        if (kick) state[i] = i % 3 === 0 ? STATE_SCATTER : STATE_DROP;
        else if (beat > 0.55) state[i] = STATE_FLANK;
        else if (beat < 0.2) state[i] = STATE_IDLE;

        let ax = 0, ay = 0;
        const dx = targetX - posX[i];
        const dy = targetY - posY[i];
        const dist = Math.hypot(dx, dy) + 0.001;

        if (state[i] === STATE_FLANK) {
            // Orbit-flank around the active lyric line.
            const tx = -dy / dist, ty = dx / dist;
            ax = (dx / dist) * (dist - 0.24) * 3.5 + tx * 2.2;
            ay = (dy / dist) * (dist - 0.24) * 3.5 + ty * 2.2;
        } else if (state[i] === STATE_DROP) {
            ax = (dx / dist) * 4.0;
            ay = (dy / dist) * 4.0 + 1.8;
        } else if (state[i] === STATE_SCATTER) {
            ax = -(dx / dist) * 5.2;
            ay = -(dy / dist) * 5.2;
        } else {
            ax = Math.sin(posY[i] * 6.28 + i) * 0.4;
            ay = Math.cos(posX[i] * 6.28 + i) * 0.4;
        }

        velX[i] = (velX[i] + ax * step) * 0.92;
        velY[i] = (velY[i] + ay * step) * 0.92;
        posX[i] = Math.max(0.02, Math.min(0.98, posX[i] + velX[i] * step));
        posY[i] = Math.max(0.02, Math.min(0.98, posY[i] + velY[i] * step));
    }

    const packed = new Float32Array(COUNT * 3);
    for (let i = 0; i < COUNT; i++) {
        packed[i * 3] = posX[i];
        packed[i * 3 + 1] = posY[i];
        packed[i * 3 + 2] = state[i];
    }
    self.postMessage({ entities: packed }, [packed.buffer]);
};
