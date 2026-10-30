// The fluid backdrop: with effects on and a WebGPU device, coloured dye drifts behind the page on
// a slow, swirling flow and the pointer stirs it. It takes the glass backdrop's place while it
// runs (html.po-fx-fluid hides that canvas and backdrop.js stops drawing); without WebGPU, or
// when the device is lost, the glass backdrop simply stays.
//
// Each frame is two render passes. The first moves the dye: every pixel looks back along the
// flow (semi-Lagrangian advection) on a small texture, the flow being the curl of a noise field,
// so it swirls without sources or sinks, plus a drag where the pointer moves. A slow noise feeds
// new colour in and old colour fades. The second paints the page from that texture.
// Kept cheap: the dye is at most 320 pixels on its longer side, the canvas is half the page's
// CSS resolution whatever the screen density, frames are capped at about 30 a second while
// nothing is happening, and nothing draws while the tab is hidden.
window.poFluid = (() => {
    const SIM = 320;          // the dye texture's longer side, in texels
    const VIEW = 0.5;         // canvas pixels per CSS pixel
    const VIEW_MOST = 1280;   // the canvas's longer side at most

    const shader = /* wgsl */ `
        struct U { a: vec4f, b: vec4f, base: vec4f, pointer: vec2f, motion: vec2f, sim: vec2f, time: f32, dt: f32, energy: f32, splat: f32, pad0: f32, pad1: f32 };
        @group(0) @binding(0) var<uniform> u: U;
        @group(0) @binding(1) var dye: texture_2d<f32>;
        @group(0) @binding(2) var smp: sampler;

        struct V { @builtin(position) at: vec4f, @location(0) uv: vec2f };
        @vertex
        fn vs(@builtin(vertex_index) i: u32) -> V {
            var corners = array<vec2f, 3>(vec2f(-1.0, -1.0), vec2f(3.0, -1.0), vec2f(-1.0, 3.0));
            let p = corners[i];
            var out: V;
            out.at = vec4f(p, 0.0, 1.0);
            out.uv = vec2f(p.x * 0.5 + 0.5, 0.5 - p.y * 0.5);
            return out;
        }

        fn hash(p: vec2f) -> f32 { return fract(sin(dot(p, vec2f(127.1, 311.7))) * 43758.5453); }
        fn noise(p: vec2f) -> f32 {
            let i = floor(p);
            let f = fract(p);
            let s = f * f * (3.0 - 2.0 * f);
            return mix(mix(hash(i), hash(i + vec2f(1.0, 0.0)), s.x), mix(hash(i + vec2f(0.0, 1.0)), hash(i + vec2f(1.0, 1.0)), s.x), s.y);
        }
        fn field(p: vec2f) -> f32 { return noise(p) * 0.65 + noise(p * 2.3 + 7.1) * 0.35; }

        // The flow at p, in texture widths a second: curl noise, and a drag where the pointer moves.
        fn flow(p: vec2f) -> vec2f {
            let aspect = u.sim.x / u.sim.y;
            let q = vec2f(p.x * aspect, p.y) * 2.4 + vec2f(u.time * 0.03, -u.time * 0.02);
            let e = 0.02;
            let dx = field(q + vec2f(e, 0.0)) - field(q - vec2f(e, 0.0));
            let dy = field(q + vec2f(0.0, e)) - field(q - vec2f(0.0, e));
            var v = vec2f(dy, -dx) / (2.0 * e) * (0.035 + u.energy * 0.05);
            v.x = v.x / aspect;
            let d = (p - u.pointer) * vec2f(aspect, 1.0);
            return v + u.motion * exp(-dot(d, d) / 0.006) * 0.9;
        }

        @fragment
        fn advect(v: V) -> @location(0) vec4f {
            let p = v.uv;
            var c = textureSampleLevel(dye, smp, p - flow(p) * u.dt, 0.0).rgb * (1.0 - 0.25 * u.dt);
            let aspect = u.sim.x / u.sim.y;
            let q = vec2f(p.x * aspect, p.y) * 1.6 + vec2f(-u.time * 0.02, u.time * 0.015);
            let source = smoothstep(0.55, 0.85, field(q));
            c += mix(u.a.rgb, u.b.rgb, field(q * 0.7 + 3.0)) * source * u.dt * (0.35 + u.energy * 0.6);
            let d = (p - u.pointer) * vec2f(aspect, 1.0);
            c += mix(u.b.rgb, u.a.rgb, 0.5 + 0.5 * sin(u.time * 0.7)) * exp(-dot(d, d) / 0.0012) * u.splat * u.dt * 6.0;
            return vec4f(min(c, vec3f(1.0)), 1.0);
        }

        // The page: its own background, tinted by the dye. Normalising the dye keeps its hue and
        // uses its amount as the strength, so it reads the same on a light page and a dark one.
        @fragment
        fn show(v: V) -> @location(0) vec4f {
            let d = textureSampleLevel(dye, smp, v.uv, 0.0).rgb;
            let strength = clamp(max(d.r, max(d.g, d.b)), 0.0, 1.0);
            let hue = d / max(strength, 0.001);
            let rest = mix(u.base.rgb, mix(u.a.rgb, u.b.rgb, v.uv.x), 0.06);
            return vec4f(mix(rest, hue, strength * (0.22 + u.energy * 0.2 + u.base.w * 0.08)), 1.0);
        }`;

    let device = null, context = null, canvas = null, format = null;
    let advect = null, show = null, sampler = null, uniform = null, textures = [], groups = null, current = 0;
    let frame = 0, last = 0, began = 0, energy = 0, target = 0, starting = null, running = false;
    const values = new Float32Array(24);
    const pointer = { x: 0.7, y: 0.2, vx: 0, vy: 0, splat: 0, at: 0 };

    const wanted = () => Boolean(navigator.gpu) && window.poFx.enabled();

    function retint() {
        if (!running) return;
        setTimeout(() => {
            const page = getComputedStyle(document.body).backgroundColor.match(/[\d.]+/g) || [];
            const dark = window.poFx.dark();
            const base = page.length >= 3 && page[3] !== '0'
                ? page.slice(0, 3).map((v) => parseFloat(v) / 255)
                : (dark ? [0.07, 0.07, 0.09] : [0.96, 0.96, 0.97]);
            values.set([...window.poFx.colour('--po-brand-a', '#4340d2'), 1], 0);
            values.set([...window.poFx.colour('--po-brand-b', '#7c3aed'), 1], 4);
            values.set([...base, dark ? 1 : 0], 8);
        }, 250);
    }

    // The dye textures and their bind groups, made again when the window's shape changes.
    function size() {
        const cw = window.innerWidth, ch = window.innerHeight;
        const scale = Math.min(VIEW, VIEW_MOST / Math.max(cw, ch));
        const w = Math.max(1, Math.round(cw * scale)), h = Math.max(1, Math.round(ch * scale));
        if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h; }
        const fit = SIM / Math.max(cw, ch);
        const sw = Math.max(8, Math.round(cw * fit)), sh = Math.max(8, Math.round(ch * fit));
        if (textures[0]?.width === sw && textures[0]?.height === sh) return;
        textures.forEach((t) => t.destroy());
        textures = [0, 1].map(() => device.createTexture({
            size: [sw, sh], format: 'rgba16float',
            usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.TEXTURE_BINDING,
        }));
        const group = (pipeline, texture) => device.createBindGroup({
            layout: pipeline.getBindGroupLayout(0),
            entries: [{ binding: 0, resource: { buffer: uniform } }, { binding: 1, resource: texture.createView() }, { binding: 2, resource: sampler }],
        });
        groups = { advect: textures.map((t) => group(advect, t)), show: textures.map((t) => group(show, t)) };
        values.set([sw, sh], 16);
        current = 0;
    }

    function draw(now) {
        const dt = Math.min(0.05, (now - (last || now)) / 1000) || 0.016;
        last = now;
        energy += (target - energy) * Math.min(1, dt * 3);
        // The pointer's speed fades once it stops.
        const still = Math.exp(-dt * 6);
        pointer.vx *= still; pointer.vy *= still; pointer.splat *= Math.exp(-dt * 3);
        size();
        values.set([pointer.x, pointer.y, pointer.vx, pointer.vy], 12);
        values[18] = (now - began) / 1000;
        values[19] = dt;
        values[20] = energy;
        values[21] = pointer.splat;
        device.queue.writeBuffer(uniform, 0, values);

        const encoder = device.createCommandEncoder();
        const into = textures[1 - current];
        const move = encoder.beginRenderPass({ colorAttachments: [{ view: into.createView(), loadOp: 'clear', clearValue: [0, 0, 0, 1], storeOp: 'store' }] });
        move.setPipeline(advect);
        move.setBindGroup(0, groups.advect[current]);
        move.draw(3);
        move.end();
        const paint = encoder.beginRenderPass({ colorAttachments: [{ view: context.getCurrentTexture().createView(), loadOp: 'clear', clearValue: [0, 0, 0, 1], storeOp: 'store' }] });
        paint.setPipeline(show);
        paint.setBindGroup(0, groups.show[1 - current]);
        paint.draw(3);
        paint.end();
        device.queue.submit([encoder.finish()]);
        current = 1 - current;
    }

    function loop(now) {
        frame = 0;
        if (!running || document.hidden) return;
        // About 30 frames a second while calm; every frame while busy or stirred.
        const calm = energy < 0.05 && pointer.splat < 0.05;
        if (!calm || now - last > 30) draw(now);
        frame = requestAnimationFrame(loop);
    }

    async function build() {
        const found = await window.poFx.device();
        if (!found || !wanted() || running) return;
        device = found;
        canvas = document.createElement('canvas');
        canvas.id = 'po-fluid';
        canvas.setAttribute('aria-hidden', 'true');
        context = canvas.getContext('webgpu');
        if (!context) { canvas = null; return; }
        format = navigator.gpu.getPreferredCanvasFormat();
        context.configure({ device, format, alphaMode: 'opaque' });
        const module = device.createShaderModule({ code: shader });
        advect = device.createRenderPipeline({
            layout: 'auto',
            vertex: { module, entryPoint: 'vs' },
            fragment: { module, entryPoint: 'advect', targets: [{ format: 'rgba16float' }] },
        });
        show = device.createRenderPipeline({
            layout: 'auto',
            vertex: { module, entryPoint: 'vs' },
            fragment: { module, entryPoint: 'show', targets: [{ format }] },
        });
        sampler = device.createSampler({ magFilter: 'linear', minFilter: 'linear', addressModeU: 'clamp-to-edge', addressModeV: 'clamp-to-edge' });
        uniform = device.createBuffer({ size: values.byteLength, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
        found.lost.then(() => { if (device === found) stop(); });

        document.body.prepend(canvas);
        running = true;
        began = performance.now();
        last = 0;
        retint();
        document.documentElement.classList.add('po-fx-fluid');
        window.poBackdrop?.refresh?.();
        frame = requestAnimationFrame(loop);
    }

    function start() {
        if (running || starting) return;
        starting = build()
            .catch((err) => { console.warn('[poFluid]', err?.message ?? err); stop(); })
            .finally(() => { starting = null; });
    }

    function stop() {
        const was = running || canvas;
        running = false;
        if (frame) cancelAnimationFrame(frame);
        frame = 0;
        textures.forEach((t) => t.destroy());
        textures = [];
        uniform?.destroy();
        uniform = null;
        canvas?.remove();
        canvas = null;
        context = null;
        device = null;
        document.documentElement.classList.remove('po-fx-fluid');
        if (was) window.poBackdrop?.refresh?.();
    }

    function refresh() {
        if (wanted()) start();
        else stop();
    }

    window.addEventListener('pointermove', (e) => {
        if (!running) return;
        const now = performance.now();
        const x = e.clientX / window.innerWidth, y = e.clientY / window.innerHeight;
        const dt = Math.max(0.008, (now - (pointer.at || now)) / 1000);
        if (pointer.at && now - pointer.at < 100) {
            // Smoothed, and capped so a flick does not tear the dye apart.
            pointer.vx = Math.max(-1.5, Math.min(1.5, pointer.vx * 0.5 + (x - pointer.x) / dt * 0.5));
            pointer.vy = Math.max(-1.5, Math.min(1.5, pointer.vy * 0.5 + (y - pointer.y) / dt * 0.5));
            pointer.splat = Math.min(1, pointer.splat + Math.hypot(x - pointer.x, y - pointer.y) * 8);
        }
        Object.assign(pointer, { x, y, at: now });
    }, { passive: true });
    document.addEventListener('visibilitychange', () => { if (running && !document.hidden && !frame) frame = requestAnimationFrame(loop); });
    // The settings switch and a change to reduced motion reach this through poFx.refresh.

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', refresh);
    else refresh();

    return {
        refresh,
        retint,
        /** 0 is the resting page, 1 is "something is happening". */
        pulse(level) { target = level; },
        active: () => running,
    };
})();
