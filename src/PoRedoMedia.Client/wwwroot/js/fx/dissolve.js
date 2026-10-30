// The shader reveal: a new picture burns through the one it was made from. One WebGL2 fragment
// shader holds both pictures as textures; a field of layered noise, tilted so the change sweeps
// down and across, decides when each pixel turns over. Along the moving edge the old picture is
// pulled sideways like heat haze and a hot rim glows in the theme's colours, with a white core
// where the edge is thinnest. When the edge has passed, the real picture fades in under the canvas
// and the canvas goes. poMorph.start tries this first and keeps its pixel flow as the fallback.
window.poDissolve = (() => {
    const SPAN = 1.7;   // seconds for the edge to cross the picture
    const FADE = 0.35;  // seconds for the canvas to hand over to the picture
    const MARGIN = 24;  // CSS pixels around the picture the glow may spill into

    const vertex = `#version 300 es
        in vec2 p; void main() { gl_Position = vec4(p, 0.0, 1.0); }`;

    const fragment = `#version 300 es
        precision highp float;
        uniform sampler2D before; uniform sampler2D after;
        uniform vec2 size; uniform vec2 margin; uniform vec2 beforeScale;
        uniform float progress; uniform float time; uniform float fade;
        uniform vec3 hot; uniform vec3 warm;
        out vec4 colour;

        float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
        float noise(vec2 p) {
            vec2 i = floor(p), f = fract(p);
            vec2 u = f * f * (3.0 - 2.0 * f);
            return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
        }
        float fbm(vec2 p) {
            float v = 0.0, amp = 0.5;
            for (int i = 0; i < 5; i++) { v += amp * noise(p); p = p * 2.07 + vec2(3.1, 1.7); amp *= 0.5; }
            return v;
        }

        void main() {
            // Top-left origin, to match the textures' first row.
            vec2 uv = vec2(gl_FragCoord.x, size.y - gl_FragCoord.y) / size;
            vec2 inner = (uv - margin) / (1.0 - 2.0 * margin);
            bool inside = all(greaterThanEqual(inner, vec2(0.0))) && all(lessThanEqual(inner, vec2(1.0)));

            // When this pixel turns over, from 0 to 1: noise, plus a sweep from the top left.
            float n = fbm(inner * vec2(5.0, 5.0 * size.y / size.x) + time * 0.05) * 0.7 + (inner.y * 0.7 + inner.x * 0.3) * 0.45;
            float edge = progress * 1.35 - 0.2;
            float d = n - edge;                         // below 0: the new picture shows
            float band = 1.0 - smoothstep(0.0, 0.07, abs(d));

            // Heat haze: near the edge the old picture is pulled sideways.
            vec2 haze = (vec2(noise(inner * 18.0 + time), noise(inner * 18.0 - time)) - 0.5) * 0.04 * band;
            vec4 old = texture(before, clamp((inner + haze - 0.5) * beforeScale + 0.5, 0.0, 1.0));
            vec4 next = texture(after, clamp(inner, 0.0, 1.0));
            vec3 c = mix(next.rgb, old.rgb, smoothstep(-0.008, 0.008, d));
            float alpha = inside ? 1.0 : 0.0;

            // The burning rim: theme colour outside, warm in the middle, white at the very edge.
            vec3 rim = mix(hot, warm, smoothstep(0.03, 0.0, abs(d))) + vec3(1.0) * smoothstep(0.012, 0.0, abs(d));
            float glow = band * (inside ? 1.0 : 0.0) + (inside ? 0.0 : exp(-length(max(abs(inner - 0.5) - 0.5, 0.0)) * 40.0) * band * 0.6);
            c = inside ? c + rim * band * 1.2 : rim;
            alpha = max(alpha, clamp(glow, 0.0, 1.0));
            colour = vec4(c * alpha * fade, alpha * fade);
        }`;

    /** A bitmap no bigger than `most` pixels on its longer side: texture memory, not detail, is the limit. */
    async function sized(picture, most) {
        const scale = most / Math.max(picture.width, picture.height);
        if (scale >= 1) return picture;
        try {
            const smaller = await createImageBitmap(picture, { resizeWidth: Math.round(picture.width * scale), resizeHeight: Math.round(picture.height * scale), resizeQuality: 'high' });
            picture.close();
            return smaller;
        } catch {
            return picture;
        }
    }

    function texture(gl, unit, picture) {
        gl.activeTexture(gl.TEXTURE0 + unit);
        gl.bindTexture(gl.TEXTURE_2D, gl.createTexture());
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, picture);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    }

    /**
     * Reveals the picture in `figure`. Resolves true once it has been shown (or there was nothing
     * to do), false when this browser cannot draw it, so the caller can try another way.
     */
    async function play(figure, fromUrl, toUrl) {
        const picture = figure?.querySelector('img');
        if (!picture || !window.poFx.enabled() || !fromUrl || !toUrl) return true;
        const probe = document.createElement('canvas');
        const gl = probe.getContext('webgl2', { premultipliedAlpha: true, antialias: false });
        if (!gl) return false;

        picture.style.transition = 'opacity 350ms';
        picture.style.opacity = '0';
        let from = null, to = null;
        try {
            const most = Math.min(2048, gl.getParameter(gl.MAX_TEXTURE_SIZE));
            [from, to] = await Promise.all([window.poFx.bitmap(fromUrl), window.poFx.bitmap(toUrl)]);
            [from, to] = await Promise.all([sized(from, most), sized(to, most)]);
            await picture.decode().catch(() => { });
            await Promise.all(figure.getAnimations({ subtree: true }).map((a) => a.finished)).catch(() => { });

            // Where the picture is drawn inside its box (object-fit: contain), relative to the figure.
            const box = picture.getBoundingClientRect(), frame = figure.getBoundingClientRect();
            const ratio = Math.min(box.width / to.width, box.height / to.height);
            const width = to.width * ratio, height = to.height * ratio;
            probe.className = 'po-morph';
            probe.setAttribute('aria-hidden', 'true');
            Object.assign(probe.style, {
                left: `${box.left - frame.left + (box.width - width) / 2 - MARGIN}px`,
                top: `${box.top - frame.top + (box.height - height) / 2 - MARGIN}px`,
                width: `${width + MARGIN * 2}px`,
                height: `${height + MARGIN * 2}px`,
            });
            figure.appendChild(probe);
            window.poFx.fit(probe, 2);

            const program = window.poFx.program(gl, vertex, fragment);
            gl.useProgram(program);
            gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
            gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
            const at = gl.getAttribLocation(program, 'p');
            gl.enableVertexAttribArray(at);
            gl.vertexAttribPointer(at, 2, gl.FLOAT, false, 0, 0);
            texture(gl, 0, from);
            texture(gl, 1, to);
            const u = (name) => gl.getUniformLocation(program, name);
            gl.uniform1i(u('before'), 0);
            gl.uniform1i(u('after'), 1);
            gl.uniform2f(u('size'), probe.width, probe.height);
            gl.uniform2f(u('margin'), MARGIN / (width + MARGIN * 2), MARGIN / (height + MARGIN * 2));
            // The old picture covers the new one's box, cropped like object-fit: cover.
            const rf = from.width / from.height, rt = to.width / to.height;
            gl.uniform2f(u('beforeScale'), rf > rt ? rt / rf : 1, rf > rt ? 1 : rf / rt);
            gl.uniform3fv(u('hot'), window.poFx.colour('--po-brand-b', '#7c3aed'));
            gl.uniform3fv(u('warm'), [1, 0.62, 0.25]);
            const progress = u('progress'), time = u('time'), fade = u('fade');
            gl.viewport(0, 0, probe.width, probe.height);

            window.poSfx?.playAt('shimmer', figure);
            await new Promise((resolve) => {
                let began = 0, shown = false;
                const tick = (now) => {
                    began ||= now;
                    const t = (now - began) / 1000;
                    const p = Math.min(1, t / SPAN);
                    const f = t < SPAN ? 1 : Math.max(0, 1 - (t - SPAN) / FADE);
                    if (!shown && t >= SPAN) { shown = true; picture.style.opacity = '1'; }
                    gl.clearColor(0, 0, 0, 0);
                    gl.clear(gl.COLOR_BUFFER_BIT);
                    gl.uniform1f(progress, p * p * (3 - 2 * p));
                    gl.uniform1f(time, t);
                    gl.uniform1f(fade, f);
                    gl.drawArrays(gl.TRIANGLES, 0, 3);
                    if (f <= 0 || !probe.isConnected) resolve();
                    else requestAnimationFrame(tick);
                };
                requestAnimationFrame(tick);
            });
            return true;
        } catch (err) {
            console.warn('[poDissolve]', err?.message ?? err);
            return false;
        } finally {
            picture.style.opacity = '1';
            from?.close();
            to?.close();
            gl.getExtension('WEBGL_lose_context')?.loseContext();
            probe.remove();
        }
    }

    return { play };
})();
