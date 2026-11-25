// GPU transform-feedback particle studio (poNebula): themed meme particle bursts (roast-fire,
// comic-pop, cash-rain, laser-sparks) rendered on a single WebGL2 overlay canvas.
// Automatically gated by window.poFx.enabled() (respects prefers-reduced-motion).
window.poNebula = (() => {
    const MAX = 1024;
    const updateVs = `#version 300 es
        precision highp float;
        in vec2 pos; in vec2 vel; in vec4 color; in vec2 life; // life.x=age, life.y=maxLife
        uniform float dt; uniform float gravity;
        out vec2 nextPos; out vec2 nextVel; out vec4 nextColor; out vec2 nextLife;
        void main() {
            float age = life.x + dt;
            vec2 v = vel + vec2(0.0, gravity) * dt;
            vec2 p = pos + v * dt;
            nextPos = p;
            nextVel = v * (1.0 - min(0.8, dt * 1.4));
            nextColor = color;
            nextLife = vec2(age, life.y);
        }`;
    const updateFs = `#version 300 es
        precision highp float; out vec4 o; void main() { o = vec4(0.0); }`;

    const drawVs = `#version 300 es
        precision highp float;
        in vec2 pos; in vec2 vel; in vec4 color; in vec2 life;
        uniform vec2 screen; uniform float dpr;
        out vec4 vColor; out float vAlpha;
        void main() {
            float t = clamp(life.x / max(0.001, life.y), 0.0, 1.0);
            vAlpha = (1.0 - t) * step(life.x, life.y);
            vColor = color;
            vec2 clip = (pos / screen) * 2.0 - 1.0;
            gl_Position = vec4(clip.x, -clip.y, 0.0, 1.0);
            gl_PointSize = mix(10.0, 2.5, t) * dpr * vAlpha;
        }`;
    const drawFs = `#version 300 es
        precision highp float;
        in vec4 vColor; in float vAlpha;
        out vec4 frag;
        void main() {
            if (vAlpha <= 0.01) discard;
            float d = length(gl_PointCoord - 0.5) * 2.0;
            if (d > 1.0) discard;
            float soft = smoothstep(1.0, 0.15, d);
            frag = vec4(vColor.rgb * soft * vAlpha, soft * vAlpha);
        }`;

    const PRESETS = {
        'roast-fire': { gravity: -240, speed: [90, 260], life: [0.45, 0.95], colors: [[1.0, 0.35, 0.1], [1.0, 0.72, 0.15], [0.95, 0.15, 0.25]] },
        'comic-pop': { gravity: 320, speed: [160, 420], life: [0.55, 1.15], colors: [[0.98, 0.85, 0.12], [0.22, 0.74, 0.98], [0.96, 0.25, 0.52]] },
        'cash-rain': { gravity: 280, speed: [60, 220], life: [0.8, 1.5], colors: [[0.13, 0.77, 0.37], [0.52, 0.93, 0.6], [0.98, 0.82, 0.2]] },
        'laser-sparks': { gravity: 180, speed: [220, 520], life: [0.35, 0.75], colors: [[1.0, 0.12, 0.2], [1.0, 0.92, 0.85], [0.72, 0.32, 1.0]] },
    };

    const STRIDE = 10; // pos(2) vel(2) color(4) life(2)
    let gl, canvas, updateProg, drawProg, bufs, vaos, tfs;
    let cur = 0, writeIdx = 0, raf = 0, last = 0, activeUntil = 0, currentGravity = 220, broken = false;

    function init() {
        if (gl || broken) return Boolean(gl);
        try {
            canvas = window.poFx.layer('nebula', 1150);
            gl = canvas.getContext('webgl2', { premultipliedAlpha: true, alpha: true });
            if (!gl) { broken = true; return false; }
            updateProg = window.poFx.program(gl, updateVs, updateFs, ['nextPos', 'nextVel', 'nextColor', 'nextLife']);
            drawProg = window.poFx.program(gl, drawVs, drawFs);

            const empty = new Float32Array(MAX * STRIDE);
            for (let i = 0; i < MAX; i++) { empty[i * STRIDE + 8] = 99; empty[i * STRIDE + 9] = 1; }
            bufs = [gl.createBuffer(), gl.createBuffer()];
            vaos = [gl.createVertexArray(), gl.createVertexArray()];
            tfs = [gl.createTransformFeedback(), gl.createTransformFeedback()];

            for (let i = 0; i < 2; i++) {
                gl.bindBuffer(gl.ARRAY_BUFFER, bufs[i]);
                gl.bufferData(gl.ARRAY_BUFFER, empty, gl.DYNAMIC_COPY);
                gl.bindVertexArray(vaos[i]);
                const byteStride = STRIDE * 4;
                const bindAttr = (loc, size, offset) => {
                    if (loc < 0) return;
                    gl.enableVertexAttribArray(loc);
                    gl.vertexAttribPointer(loc, size, gl.FLOAT, false, byteStride, offset * 4);
                };
                bindAttr(0, 2, 0); bindAttr(1, 2, 2); bindAttr(2, 4, 4); bindAttr(3, 2, 8);
                gl.bindTransformFeedback(gl.TRANSFORM_FEEDBACK, tfs[i]);
                gl.bindBufferBase(gl.TRANSFORM_FEEDBACK_BUFFER, 0, bufs[i]);
            }
            gl.bindVertexArray(null);
            gl.bindTransformFeedback(gl.TRANSFORM_FEEDBACK, null);
            return true;
        } catch {
            broken = true;
            return false;
        }
    }

    function step(now) {
        if (now > activeUntil) {
            gl.clearColor(0, 0, 0, 0);
            gl.clear(gl.COLOR_BUFFER_BIT);
            raf = 0;
            return;
        }
        const dt = Math.min(0.05, Math.max(0.001, (now - last) / 1000));
        last = now;
        const dpr = window.poFx.fit(canvas, 2);
        const next = 1 - cur;

        gl.useProgram(updateProg);
        gl.uniform1f(gl.getUniformLocation(updateProg, 'dt'), dt);
        gl.uniform1f(gl.getUniformLocation(updateProg, 'gravity'), currentGravity);
        gl.enable(gl.RASTERIZER_DISCARD);
        gl.bindVertexArray(vaos[cur]);
        gl.bindTransformFeedback(gl.TRANSFORM_FEEDBACK, tfs[next]);
        gl.beginTransformFeedback(gl.POINTS);
        gl.drawArrays(gl.POINTS, 0, MAX);
        gl.endTransformFeedback();
        gl.bindTransformFeedback(gl.TRANSFORM_FEEDBACK, null);
        gl.disable(gl.RASTERIZER_DISCARD);

        cur = next;

        gl.viewport(0, 0, canvas.width, canvas.height);
        gl.clearColor(0, 0, 0, 0);
        gl.clear(gl.COLOR_BUFFER_BIT);
        gl.enable(gl.BLEND);
        gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
        gl.useProgram(drawProg);
        gl.uniform2f(gl.getUniformLocation(drawProg, 'screen'), canvas.clientWidth || window.innerWidth, canvas.clientHeight || window.innerHeight);
        gl.uniform1f(gl.getUniformLocation(drawProg, 'dpr'), dpr);
        gl.bindVertexArray(vaos[cur]);
        gl.drawArrays(gl.POINTS, 0, MAX);
        gl.bindVertexArray(null);

        raf = requestAnimationFrame(step);
    }

    function burst(presetName = 'comic-pop', x = window.innerWidth * 0.5, y = window.innerHeight * 0.4, count = 48) {
        if (!window.poFx?.enabled?.() || !init()) return;
        const p = PRESETS[presetName] || PRESETS['comic-pop'];
        currentGravity = p.gravity;
        const n = Math.min(MAX, Math.max(1, count));
        const chunk = new Float32Array(n * STRIDE);
        for (let i = 0; i < n; i++) {
            const angle = Math.random() * Math.PI * 2;
            const spd = p.speed[0] + Math.random() * (p.speed[1] - p.speed[0]);
            const c = p.colors[i % p.colors.length];
            const o = i * STRIDE;
            chunk[o] = x + (Math.random() - 0.5) * 24;
            chunk[o + 1] = y + (Math.random() - 0.5) * 24;
            chunk[o + 2] = Math.cos(angle) * spd;
            chunk[o + 3] = Math.sin(angle) * spd - (p.gravity > 0 ? 120 : 40);
            chunk[o + 4] = c[0]; chunk[o + 5] = c[1]; chunk[o + 6] = c[2]; chunk[o + 7] = 1;
            chunk[o + 8] = 0;
            chunk[o + 9] = p.life[0] + Math.random() * (p.life[1] - p.life[0]);
        }

        gl.bindBuffer(gl.ARRAY_BUFFER, bufs[cur]);
        const firstPart = Math.min(n, MAX - writeIdx);
        gl.bufferSubData(gl.ARRAY_BUFFER, writeIdx * STRIDE * 4, chunk.subarray(0, firstPart * STRIDE));
        if (firstPart < n) {
            gl.bufferSubData(gl.ARRAY_BUFFER, 0, chunk.subarray(firstPart * STRIDE));
        }
        writeIdx = (writeIdx + n) % MAX;

        const now = performance.now();
        activeUntil = Math.max(activeUntil, now + p.life[1] * 1000 + 250);
        if (!raf) { last = now; raf = requestAnimationFrame(step); }
    }

    return { burst };
})();
