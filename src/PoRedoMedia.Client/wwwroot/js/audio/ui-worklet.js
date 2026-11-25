// Low-latency AudioWorkletProcessor for procedural tactile UI feedback:
// 6ms wooden/glass hover ticks, slider detents, toggle clicks, and pentatonic chimes.
class PoUiFeedbackProcessor extends AudioWorkletProcessor {
    constructor() {
        super();
        this.voices = [];
        this.port.onmessage = (event) => {
            const { kind = 'tick', freq = 880, gain = 0.08 } = event.data || {};
            const duration = kind === 'chime' ? 0.22 : kind === 'detent' ? 0.014 : 0.008;
            this.voices.push({
                kind,
                freq,
                gain,
                phase: 0,
                samples: 0,
                total: Math.max(1, Math.floor(sampleRate * duration)),
            });
            if (this.voices.length > 16) this.voices.shift();
        };
    }

    process(_inputs, outputs) {
        const out = outputs[0]?.[0];
        if (!out) return true;
        out.fill(0);
        for (let v = this.voices.length - 1; v >= 0; v--) {
            const voice = this.voices[v];
            for (let i = 0; i < out.length; i++) {
                if (voice.samples >= voice.total) break;
                const t = voice.samples / voice.total;
                const env = Math.pow(1 - t, voice.kind === 'chime' ? 2.4 : 3.5);
                const sig = voice.kind === 'chime'
                    ? Math.sin(voice.phase) * 0.75 + Math.sin(voice.phase * 2.0) * 0.25
                    : Math.sin(voice.phase) * 0.65 + (Math.random() * 2 - 1) * 0.2;
                out[i] += sig * env * voice.gain;
                voice.phase += (2 * Math.PI * voice.freq) / sampleRate;
                voice.samples++;
            }
            if (voice.samples >= voice.total) this.voices.splice(v, 1);
        }
        return true;
    }
}

registerProcessor('po-ui-feedback', PoUiFeedbackProcessor);
