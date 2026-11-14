/**
 * Speech worker — Whisper through transformers.js: base on a GPU (WebGPU), where it runs about as
 * fast as tiny does on the CPU and mishears far less; tiny on the CPU, where base is too slow.
 *
 * Receives { samples } (mono, 16 kHz, Float32Array) and emits
 * { type: 'status', percent } while the model downloads, then
 * { type: 'complete', segments: [{ startSeconds, endSeconds, text }] } or { type: 'error', reason }.
 */

// Pinned for the same reason as the vision worker: an unpinned import would let a release change
// behaviour without a deploy.
import { pipeline, env } from 'https://cdn.jsdelivr.net/npm/@huggingface/transformers@3.7.6';

env.allowLocalModels = false;

async function gpu() {
    try {
        return !!(navigator.gpu && await navigator.gpu.requestAdapter());
    } catch {
        return false;
    }
}

const progress_callback = (p) => {
    if (p && p.status === 'progress' && typeof p.progress === 'number') {
        self.postMessage({ type: 'status', percent: Math.round(p.progress) });
    }
};

async function hear(samples, webgpu) {
    const transcriber = await pipeline('automatic-speech-recognition', webgpu ? 'onnx-community/whisper-base' : 'Xenova/whisper-tiny', {
        // The encoder loses accuracy when quantised; the decoder does not, and is most of the download.
        ...(webgpu ? { device: 'webgpu', dtype: { encoder_model: 'fp32', decoder_model_merged: 'q4' } } : { dtype: 'q8' }),
        progress_callback,
    });
    self.postMessage({ type: 'status', percent: -1 });
    return transcriber(samples, { return_timestamps: true, chunk_length_s: 30, stride_length_s: 5 });
}

self.onmessage = async (event) => {
    try {
        // Some GPUs offer WebGPU but fail to load or run the model; the CPU then hears it with tiny.
        // The samples are sliced so a failed GPU attempt cannot leave them detached.
        let result;
        if (await gpu()) {
            try {
                result = await hear(event.data.samples.slice(), true);
            } catch (err) {
                console.warn('[whisper] WebGPU failed, hearing it on the CPU instead:', err?.message ?? err);
            }
        }
        result ??= await hear(event.data.samples, false);
        const segments = (result.chunks || [])
            .filter((c) => c.text && c.text.trim() && c.timestamp && c.timestamp[0] !== null)
            .map((c) => ({
                startSeconds: c.timestamp[0],
                // The last chunk of a clip can come back without an end.
                endSeconds: c.timestamp[1] ?? c.timestamp[0] + 3,
                text: c.text.trim(),
            }));
        self.postMessage({ type: 'complete', segments });
    } catch (err) {
        self.postMessage({ type: 'error', reason: err?.message ?? String(err) });
    }
};
