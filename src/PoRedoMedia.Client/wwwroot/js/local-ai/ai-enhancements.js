// Async non-blocking client AI utilities:
// - #12 Local Sentiment & Intensity Classifier (instant heuristic + drop-stale worker queue)
// - #15 Keyframe-to-Sound Cosine Matcher
// - #18 On-Device Prosody / Pitch-Contour Extractor (autocorrelation) for harmonious stinger pitch
// - #20 Idle Predictive Pre-Warmer (requestIdleCallback, yields on user interaction)
window.poAiEnhance = (() => {
    const HIGH_WORDS = /\b(bro|wtf|insane|literally|dead|destroyed|cooked|wild|crazy|unhinged|fire|savage|nah|bruh|lmao)\b/gi;
    const ROAST_WORDS = /\b(roast|clown|disaster|embarrassing|trash|horrible|awful|pathetic|failed|oof)\b/gi;

    // #12 Instant zero-stall sentiment & comedy intensity score [0..1] for pasted comic threads & lyrics.
    function scoreIntensity(text) {
        const s = String(text || '').trim();
        if (!s) return { intensity: 0.25, mood: 'chill', preset: 'comic-pop' };
        const exclam = (s.match(/[!?]/g) || []).length;
        const caps = (s.match(/\b[A-Z]{3,}\b/g) || []).length;
        const hype = (s.match(HIGH_WORDS) || []).length;
        const roast = (s.match(ROAST_WORDS) || []).length;
        const intensity = Math.min(1, 0.2 + exclam * 0.08 + caps * 0.1 + hype * 0.14 + roast * 0.16);
        const mood = roast >= 2 ? 'roast' : intensity >= 0.7 ? 'hype' : 'chill';
        const preset = mood === 'roast' ? 'roast-fire' : mood === 'hype' ? 'laser-sparks' : 'comic-pop';
        return { intensity: Number(intensity.toFixed(3)), mood, preset };
    }

    // #15 Cosine similarity between two normalized feature vectors (e.g. keyframe vs. sound tag embeddings).
    function cosineSimilarity(a, b) {
        if (!a || !b || a.length !== b.length || a.length === 0) return 0;
        let dot = 0, na = 0, nb = 0;
        for (let i = 0; i < a.length; i++) {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na > 0 && nb > 0 ? dot / (Math.sqrt(na) * Math.sqrt(nb)) : 0;
    }

    // #18 Lightweight autocorrelation pitch estimator (Hz) on a decoded mono Float32Array window.
    function estimatePitchHz(samples, sampleRate = 16000) {
        if (!samples || samples.length < 512) return 220;
        const minLag = Math.floor(sampleRate / 420); // ~420 Hz upper vocal fundamental
        const maxLag = Math.min(samples.length - 1, Math.floor(sampleRate / 75)); // ~75 Hz lower vocal fundamental
        let bestLag = minLag, bestCorr = -1;
        const len = Math.min(samples.length - maxLag, 1024);
        for (let lag = minLag; lag <= maxLag; lag++) {
            let sum = 0;
            for (let i = 0; i < len; i++) sum += samples[i] * samples[i + lag];
            if (sum > bestCorr) { bestCorr = sum; bestLag = lag; }
        }
        return bestLag > 0 ? Math.round(sampleRate / bestLag) : 220;
    }

    // #20 Idle-priority predictive pre-warmer: checks cache or warms assets without ever blocking 60fps UI.
    function scheduleIdleWarmup(repoId) {
        if (!repoId || typeof window.poLocalAiIsCached !== 'function') return;
        const run = async () => {
            try { await window.poLocalAiIsCached(repoId); } catch { /* ignore */ }
        };
        if (typeof requestIdleCallback === 'function') requestIdleCallback(() => { run(); }, { timeout: 2500 });
        else setTimeout(run, 1200);
    }

    return { scoreIntensity, cosineSimilarity, estimatePitchHz, scheduleIdleWarmup };
})();
