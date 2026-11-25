/**
 * Local Comedy Director LLM worker — transformers.js / ONNX Runtime Web.
 * Runs Qwen2.5-0.5B-Instruct (WebGPU or WASM) for instant zero-cost meme drafts,
 * persona intent routing, and comic punch-up lines.
 */
import { pipeline, env } from 'https://cdn.jsdelivr.net/npm/@huggingface/transformers@3.7.6';

env.allowLocalModels = false;

let cached = null; // { key, generator }

self.onmessage = async (event) => {
    const { id, repoId, dtype, device, prompt } = event.data || {};
    const key = `${repoId}|${dtype}|${device}`;
    try {
        if (!cached || cached.key !== key) {
            const generator = await pipeline('text-generation', repoId, {
                dtype,
                device: device === 'WebGpu' ? 'webgpu' : 'wasm',
            });
            cached = { key, generator };
        }

        const messages = [
            {
                role: 'system',
                content: 'You are a witty comedy writer. Reply ONLY with compact JSON: {"topText":"...","bottomText":"...","persona":"standard|brainrot|mlg|sitcom|drama|anime","reply":"one short punchline"}.',
            },
            { role: 'user', content: String(prompt || 'Write a short funny meme caption') },
        ];

        const out = await cached.generator(messages, { max_new_tokens: 96, do_sample: true, temperature: 0.8 });
        const generated = out?.[0]?.generated_text;
        const raw = Array.isArray(generated)
            ? String(generated[generated.length - 1]?.content ?? '')
            : String(generated ?? '');
        const match = raw.match(/\{[\s\S]*\}/);
        const text = match ? match[0] : JSON.stringify({ reply: raw.trim().slice(0, 180) });
        self.postMessage({ id, ok: true, text });
    } catch (err) {
        self.postMessage({ id, ok: false, reason: err?.message ?? String(err) });
    }
};
