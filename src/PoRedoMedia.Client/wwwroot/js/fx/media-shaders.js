// Real-time WebGL2 shader preview for VisualEffectType (DeepFry, MotionBlur, Zoom, VHS,
// ComicHalftone, CRT) on any photo or video element in MediaPanel before queuing an FFmpeg run.
window.poMediaShaders = (() => {
    const vertex = `#version 300 es
        in vec2 p; out vec2 uv;
        void main() { uv = vec2(p.x * 0.5 + 0.5, 0.5 - p.y * 0.5); gl_Position = vec4(p, 0.0, 1.0); }`;

    const fragment = `#version 300 es
        precision highp float;
        in vec2 uv;
        uniform sampler2D tex;
        uniform int mode; // 0=none, 1=deepfry, 2=zoom, 3=vhs, 4=comichalftone, 5=crt
        uniform float time;
        uniform vec2 size;
        out vec4 outColor;

        float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }

        void main() {
            vec2 q = uv;
            if (mode == 2) { // Zoom punch-in
                q = (q - 0.5) / 1.3 + 0.5;
            } else if (mode == 3) { // VHS tracking wobble
                q.x += sin(q.y * 42.0 + time * 9.0) * 0.003;
            }

            vec3 c;
            if (mode == 3) {
                float shift = 0.007;
                c = vec3(texture(tex, q + vec2(-shift, 0.0)).r, texture(tex, q).g, texture(tex, q + vec2(shift, 0.0)).b);
                c += (hash(q * size + fract(time)) - 0.5) * 0.14;
            } else {
                c = texture(tex, q).rgb;
            }

            if (mode == 1) { // DeepFry
                float luma = dot(c, vec3(0.299, 0.587, 0.114));
                c = mix(vec3(luma), c, 3.0);
                c = (c - 0.5) * 1.55 + 0.55;
            } else if (mode == 4) { // ComicHalftone
                c = floor(c * 5.0 + 0.5) / 5.0;
                float luma = dot(c, vec3(0.299, 0.587, 0.114));
                vec2 cell = fract(q * size * 0.18) - 0.5;
                float dotMask = smoothstep(0.45, 0.25, length(cell) + luma * 0.28);
                c = mix(c, c * 0.35, dotMask * 0.55);
            } else if (mode == 5) { // CRT
                float scan = 0.86 + 0.14 * sin(q.y * size.y * 1.6);
                vec2 d = q - 0.5;
                float vig = 1.0 - dot(d, d) * 1.25;
                c = c * scan * vig;
            }

            outColor = vec4(clamp(c, 0.0, 1.0), 1.0);
        }`;

    const MODES = { none: 0, deepfry: 1, zoom: 2, vhs: 3, comichalftone: 4, crt: 5 };
    const active = new WeakMap();

    function apply(container, effectName) {
        if (!container) return;
        const mode = MODES[(effectName || 'none').toLowerCase()] ?? 0;
        if (mode === 0) { detach(container); return; }

        const source = container.querySelector('img, video');
        if (!source) return;

        let state = active.get(container);
        if (!state) {
            const canvas = document.createElement('canvas');
            canvas.className = 'po-shader-preview';
            canvas.style.cssText = 'position:absolute;inset:0;width:100%;height:100%;pointer-events:none;border-radius:inherit;';
            const gl = canvas.getContext('webgl2', { premultipliedAlpha: false });
            if (!gl) return;
            const prog = window.poFx.program(gl, vertex, fragment);
            const buf = gl.createBuffer();
            gl.bindBuffer(gl.ARRAY_BUFFER, buf);
            gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, -1, 1, 1, -1, 1, 1]), gl.STATIC_DRAW);
            const tex = gl.createTexture();
            gl.bindTexture(gl.TEXTURE_2D, tex);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);

            container.style.position = container.style.position || 'relative';
            container.appendChild(canvas);
            state = {
                canvas, gl, prog, buf, tex, source, mode, raf: 0, start: performance.now(),
                uMode: gl.getUniformLocation(prog, 'mode'),
                uTime: gl.getUniformLocation(prog, 'time'),
                uSize: gl.getUniformLocation(prog, 'size'),
                aPos: gl.getAttribLocation(prog, 'p'),
            };
            active.set(container, state);

            const draw = (now) => {
                if (!active.has(container)) return;
                const s = state;
                const ready = s.source.tagName === 'VIDEO' ? s.source.readyState >= 2 : s.source.complete && s.source.naturalWidth > 0;
                if (ready) {
                    window.poFx.fit(s.canvas, 1.5);
                    s.gl.viewport(0, 0, s.canvas.width, s.canvas.height);
                    s.gl.useProgram(s.prog);
                    s.gl.bindBuffer(s.gl.ARRAY_BUFFER, s.buf);
                    s.gl.enableVertexAttribArray(s.aPos);
                    s.gl.vertexAttribPointer(s.aPos, 2, s.gl.FLOAT, false, 0, 0);
                    s.gl.bindTexture(s.gl.TEXTURE_2D, s.tex);
                    try {
                        s.gl.texImage2D(s.gl.TEXTURE_2D, 0, s.gl.RGBA, s.gl.RGBA, s.gl.UNSIGNED_BYTE, s.source);
                    } catch { /* cross-origin media falls back silently */ detach(container); return; }
                    s.gl.uniform1i(s.uMode, s.mode);
                    s.gl.uniform1f(s.uTime, (now - s.start) / 1000);
                    s.gl.uniform2f(s.uSize, s.canvas.width, s.canvas.height);
                    s.gl.drawArrays(s.gl.TRIANGLES, 0, 6);
                }
                s.raf = requestAnimationFrame(draw);
            };
            state.raf = requestAnimationFrame(draw);
        }
        state.mode = mode;
    }

    function detach(container) {
        const state = active.get(container);
        if (!state) return;
        cancelAnimationFrame(state.raf);
        state.canvas.remove();
        active.delete(container);
    }

    return { apply, detach };
})();
