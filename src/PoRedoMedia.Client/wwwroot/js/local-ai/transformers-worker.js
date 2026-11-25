/**
 * Vision worker — transformers.js / ONNX Runtime Web.
 *
 * Receives {repoId, dtype, device, prompt, image} (image: the picture's bytes, a Uint8Array) and emits status/complete/error.
 * It attempts exactly ONE dtype: the fallback chain is owned by C# (DtypeChain), so a failure here
 * is reported rather than retried.
 */

// Pinned to a specific version: an unpinned CDN import would let a transformers.js release change
// inference behaviour without a deploy. Vendor this file to wwwroot to drop the CDN entirely.
// Florence-2 has its own model class. The generic AutoModelForVision2Seq does not know the
// "florence2" model type and fails with "Unsupported model type" before any weights load.
import {
    AutoProcessor, AutoTokenizer, Florence2ForConditionalGeneration, RawImage, env,
} from 'https://cdn.jsdelivr.net/npm/@huggingface/transformers@3.7.6';

// The worker fetches weights itself; disabling the local-model path stops it probing a /models/
// route that this app does not serve and logging a 404 for every shard.
env.allowLocalModels = false;

let cached = null; // { key, processor, tokenizer, model }

async function release(why) {
    if (!cached) return;
    try {
        console.log(`[LocalAI/transformers] Releasing '${cached.key}' — ${why}`);
        await cached.model?.dispose?.();
    } catch (err) {
        console.warn('[LocalAI/transformers] Dispose failed:', err?.message ?? err);
    } finally {
        cached = null;
    }
}

self.onmessage = async (event) => {
    const { repoId, dtype, device, prompt, image: bytes } = event.data || {};
    const key = `${repoId}|${dtype}|${device}`;

    try {
        if (!cached || cached.key !== key) {
            // Free the previous model FIRST. Reassigning does not release its GPU buffers, and
            // stacking them is what produces "device was lost" on the second model load.
            await release('variant or model changed');

            post({ type: 'status', stage: 'Downloading', detail: `Fetching ${repoId} (${dtype})`, loadPercent: 0 });

            const onProgress = (p) => {
                if (p && p.status === 'progress' && typeof p.progress === 'number') {
                    post({
                        type: 'status',
                        stage: 'Downloading',
                        detail: p.file ? `Downloading ${p.file}` : 'Downloading weights',
                        loadPercent: Math.round(p.progress),
                    });
                }
            };

            const [processor, tokenizer, model] = await Promise.all([
                AutoProcessor.from_pretrained(repoId, { progress_callback: onProgress }),
                AutoTokenizer.from_pretrained(repoId, { progress_callback: onProgress }),
                Florence2ForConditionalGeneration.from_pretrained(repoId, {
                    dtype,
                    device: device === 'WebGpu' ? 'webgpu' : 'wasm',
                    progress_callback: onProgress,
                }),
            ]);

            cached = { key, processor, tokenizer, model };
        }

        post({ type: 'status', stage: 'Running', detail: 'Describing the image' });

        if (!bytes) throw new Error('No picture was handed over');
        const image = await RawImage.fromBlob(new Blob([bytes]));
        const task = prompt && prompt.trim() ? prompt.trim() : '<MORE_DETAILED_CAPTION>';
        const baseTask = task.startsWith('<CAPTION_TO_PHRASE_GROUNDING>') ? '<CAPTION_TO_PHRASE_GROUNDING>' : task;

        // Florence-2 is driven by a task token, which the processor expands into the prompt the
        // model was trained on. Text and picture are prepared separately and generated together.
        const textInputs = cached.tokenizer(cached.processor.construct_prompts(task));
        const visionInputs = await cached.processor(image);
        const generated = await cached.model.generate({ ...textInputs, ...visionInputs, max_new_tokens: 256 });
        const raw = cached.tokenizer.batch_decode(generated, { skip_special_tokens: false })[0];
        const parsed = cached.processor.post_process_generation(raw, baseTask, image.size);
        const result = parsed?.[baseTask];

        // Object detection / phrase grounding returns { bboxes: [[x1,y1,x2,y2], ...], labels: [...] }.
        // Normalise into 0..1 VisionBoxDto[] JSON so the renderer can aim stickers at real subjects.
        let text;
        if (result && Array.isArray(result.bboxes)) {
            const [w, h] = Array.isArray(image.size) ? image.size : [image.width || 1, image.height || 1];
            const sw = Math.max(1, Number(w) || 1);
            const sh = Math.max(1, Number(h) || 1);
            const labels = Array.isArray(result.labels) ? result.labels : [];
            const boxes = result.bboxes
                .filter((b) => Array.isArray(b) && b.length >= 4)
                .map((b, i) => {
                    const x1 = Math.max(0, Math.min(1, b[0] / sw));
                    const y1 = Math.max(0, Math.min(1, b[1] / sh));
                    const x2 = Math.max(0, Math.min(1, b[2] / sw));
                    const y2 = Math.max(0, Math.min(1, b[3] / sh));
                    return {
                        label: String(labels[i] ?? 'object'),
                        x: Number(x1.toFixed(4)),
                        y: Number(y1.toFixed(4)),
                        w: Number(Math.max(0, x2 - x1).toFixed(4)),
                        h: Number(Math.max(0, y2 - y1).toFixed(4)),
                    };
                });
            text = JSON.stringify(boxes);
        } else {
            text = typeof result === 'string' ? result : String(raw ?? '');
        }

        post({ type: 'complete', text: text.trim() });
    } catch (err) {
        const reason = err?.message ?? String(err);
        console.error('[LocalAI/transformers] Failed:', reason, err);
        post({ type: 'error', reason });
        // A failed load can leave a half-initialised model behind; the next attempt (a different
        // dtype) must not reuse it.
        await release('run failed');
    }
};

function post(message) {
    self.postMessage(message);
}
