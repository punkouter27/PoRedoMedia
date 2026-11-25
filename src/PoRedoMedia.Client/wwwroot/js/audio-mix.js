// 3D HRTF spatial audio & 4-bus dynamic mixer (voice, memeSfx, music, ui) with sidechain ducking
// matching RunOptions.VideoVoiceFirst. Synthesizes instant meme preview stingers (vine-boom,
// airhorn, scratch, ding) positioned in 3D space without shipping extra audio files.
window.poAudioMix = (() => {
    let ctx = null;
    let buses = null;
    let duckingRaf = 0;
    let voiceFirst = false;

    function ensure() {
        if (!ctx) {
            ctx = new AudioContext();
            const limiter = ctx.createDynamicsCompressor();
            limiter.threshold.value = -12;
            limiter.ratio.value = 10;
            limiter.attack.value = 0.004;
            limiter.release.value = 0.12;
            limiter.connect(ctx.destination);

            const voice = ctx.createGain();
            const voiceAnalyser = ctx.createAnalyser();
            voiceAnalyser.fftSize = 256;
            voice.connect(voiceAnalyser);
            voice.connect(limiter);

            const memeSfx = ctx.createGain();
            const memeDuck = ctx.createGain();
            memeSfx.connect(memeDuck).connect(limiter);

            const music = ctx.createGain();
            const musicDuck = ctx.createGain();
            music.connect(musicDuck).connect(limiter);

            const ui = ctx.createGain();
            ui.connect(limiter);

            buses = { voice, voiceAnalyser, memeSfx, memeDuck, music, musicDuck, ui, limiter, buf: new Float32Array(256) };
            startSidechain();
        }
        if (ctx.state === 'suspended') ctx.resume();
        return ctx;
    }

    function startSidechain() {
        if (duckingRaf) return;
        const tick = () => {
            if (!buses || !ctx) { duckingRaf = 0; return; }
            buses.voiceAnalyser.getFloatTimeDomainData(buses.buf);
            let sum = 0;
            for (let i = 0; i < buses.buf.length; i++) sum += buses.buf[i] * buses.buf[i];
            const rms = Math.sqrt(sum / buses.buf.length);
            const duck = voiceFirst && rms > 0.02 ? Math.max(0.22, 1.0 - rms * 4.5) : 1.0;
            buses.memeDuck.gain.setTargetAtTime(duck, ctx.currentTime, 0.035);
            buses.musicDuck.gain.setTargetAtTime(Math.min(1.0, duck * 0.85), ctx.currentTime, 0.05);
            duckingRaf = requestAnimationFrame(tick);
        };
        duckingRaf = requestAnimationFrame(tick);
    }

    function createSpatialPanner(normX = 0.5, normY = 0.5) {
        const panner = ctx.createPanner();
        panner.panningModel = 'HRTF';
        panner.distanceModel = 'inverse';
        panner.refDistance = 1;
        panner.maxDistance = 10;
        const x = (Math.max(0, Math.min(1, normX)) - 0.5) * 4.0;
        const y = (0.5 - Math.max(0, Math.min(1, normY))) * 2.0;
        panner.positionX.setValueAtTime(x, ctx.currentTime);
        panner.positionY.setValueAtTime(y, ctx.currentTime);
        panner.positionZ.setValueAtTime(-1.2, ctx.currentTime);
        return panner;
    }

    function setBusLevels(voice = 1.0, memeSfx = 0.85, music = 0.5, isVoiceFirst = false) {
        ensure();
        voiceFirst = Boolean(isVoiceFirst);
        const t = ctx.currentTime;
        buses.voice.gain.setTargetAtTime(Math.max(0, Math.min(1.5, voice)), t, 0.03);
        buses.memeSfx.gain.setTargetAtTime(Math.max(0, Math.min(1.5, memeSfx)), t, 0.03);
        buses.music.gain.setTargetAtTime(Math.max(0, Math.min(1.5, music)), t, 0.03);
    }

    function playStinger(kind = 'boom', normX = 0.5, normY = 0.5, level = 0.8) {
        ensure();
        const at = ctx.currentTime;
        const panner = createSpatialPanner(normX, normY);
        panner.connect(buses.memeSfx);

        if (kind === 'boom') {
            const osc = ctx.createOscillator();
            const g = ctx.createGain();
            osc.type = 'sine';
            osc.frequency.setValueAtTime(140, at);
            osc.frequency.exponentialRampToValueAtTime(34, at + 0.55);
            g.gain.setValueAtTime(0.001, at);
            g.gain.linearRampToValueAtTime(level * 0.9, at + 0.012);
            g.gain.exponentialRampToValueAtTime(0.001, at + 0.65);
            osc.connect(g).connect(panner);
            osc.start(at);
            osc.stop(at + 0.7);
        } else if (kind === 'horn') {
            for (const freq of [440, 554.37, 659.25]) {
                const osc = ctx.createOscillator();
                const g = ctx.createGain();
                osc.type = 'sawtooth';
                osc.frequency.setValueAtTime(freq, at);
                g.gain.setValueAtTime(0.001, at);
                g.gain.linearRampToValueAtTime(level * 0.18, at + 0.02);
                g.gain.exponentialRampToValueAtTime(0.001, at + 0.32);
                osc.connect(g).connect(panner);
                osc.start(at);
                osc.stop(at + 0.35);
            }
        } else {
            const osc = ctx.createOscillator();
            const g = ctx.createGain();
            osc.type = 'triangle';
            osc.frequency.setValueAtTime(784, at);
            osc.frequency.exponentialRampToValueAtTime(1174.66, at + 0.16);
            g.gain.setValueAtTime(0.001, at);
            g.gain.linearRampToValueAtTime(level * 0.45, at + 0.01);
            g.gain.exponentialRampToValueAtTime(0.001, at + 0.28);
            osc.connect(g).connect(panner);
            osc.start(at);
            osc.stop(at + 0.3);
        }
    }

    // Plays a short 3D spatialized preview of the current MemeMix levels.
    function previewMix(roastLevel = 2, memeLevel = 2, isVoiceFirst = false) {
        const sfxVol = Math.max(0.15, Math.min(1.0, (Number(memeLevel) + 1) / 4));
        const voiceVol = Math.max(0.2, Math.min(1.0, (Number(roastLevel) + 1) / 4));
        setBusLevels(voiceVol, sfxVol, 0.45, isVoiceFirst);
        // Short voice-bus formant pulse to demonstrate sidechain ducking when voiceFirst is on.
        const at = ctx.currentTime;
        const vOsc = ctx.createOscillator();
        const vGain = ctx.createGain();
        vOsc.type = 'triangle';
        vOsc.frequency.setValueAtTime(220, at);
        vGain.gain.setValueAtTime(0.001, at);
        vGain.gain.linearRampToValueAtTime(voiceVol * 0.35, at + 0.02);
        vGain.gain.exponentialRampToValueAtTime(0.001, at + 0.25);
        vOsc.connect(vGain).connect(buses.voice);
        vOsc.start(at);
        vOsc.stop(at + 0.28);

        playStinger('boom', 0.25, 0.4, sfxVol);
        setTimeout(() => playStinger(roastLevel >= 2 ? 'horn' : 'ding', 0.78, 0.35, sfxVol * 0.85), 220);
    }

    return { setBusLevels, playStinger, previewMix };
})();
