// Create Movie in the browser: undo and redo from the keyboard, and the quick preview's player,
// which draws the captions live over the video so a change of style shows without rendering again.
window.poMovie = (function () {
    let keys = null;
    let player = null;

    function typing(target) {
        const tag = (target.tagName || '').toLowerCase();
        return tag === 'input' || tag === 'textarea' || tag === 'select' || target.isContentEditable;
    }

    // What each caption preset looks like, as the render draws it (Render/AssCaptions.cs).
    const looks = {
        KaraokeHighlight: { max: 5, boxed: false, dark: false, twoLines: false, upper: false, font: 'Segoe UI, system-ui, sans-serif', size: 1, outline: 1 },
        TwoLineBlock: { max: 10, boxed: true, dark: false, twoLines: true, upper: false, font: 'Segoe UI, system-ui, sans-serif', size: 1, outline: 1 },
        CleanSubtitle: { max: 8, boxed: false, dark: false, twoLines: false, upper: false, font: 'Segoe UI, system-ui, sans-serif', size: 0.8, outline: 0.5 },
        ComicBanner: { max: 6, boxed: true, dark: true, twoLines: false, upper: true, font: '"Comic Sans MS", "Comic Neue", cursive, sans-serif', size: 1, outline: 1 },
    };

    // The words in the groups they are shown in: a few at a time, never across a pause.
    function cues(words, max) {
        const out = [];
        let group = [];
        for (const word of words) {
            const last = group[group.length - 1];
            if (group.length >= max || (last && word.start - (last.start + last.length) > 0.8)) {
                out.push(group);
                group = [];
            }
            group.push(word);
        }
        if (group.length) out.push(group);
        return out.map((g) => ({ start: g[0].start, end: g[g.length - 1].start + g[g.length - 1].length, words: g }));
    }

    function clock(seconds) {
        const s = Math.max(0, Math.floor(seconds || 0));
        return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
    }

    return {
        attachKeys(target) {
            window.poMovie.detachKeys();
            keys = (e) => {
                if (!(e.ctrlKey || e.metaKey) || e.altKey || typing(e.target)) return;
                const key = e.key.toLowerCase();
                if (key !== 'z' && key !== 'y') return;
                if (document.querySelector('.rz-dialog')) return;
                e.preventDefault();
                target.invokeMethodAsync('OnMovieKey', key === 'y' || e.shiftKey ? 'redo' : 'undo');
            };
            document.addEventListener('keydown', keys);
        },

        detachKeys() {
            if (keys) document.removeEventListener('keydown', keys);
            keys = null;
        },

        // Wires the preview: the video, the canvas the captions are drawn on, the bar with the
        // clips' marks, the play button, the scrubber and the clock.
        preview(root, texts, starts, lengths, preset, accent, fontSize, stroke) {
            const words = texts.map((text, i) => ({ text, start: starts[i], length: lengths[i] }));
            const style = { preset, accent, fontSize, stroke };
            window.poMovie.closePreview();
            const video = root.querySelector('video');
            const canvas = root.querySelector('canvas');
            const bar = root.querySelector('.mv-timeline');
            const head = root.querySelector('.mv-playhead');
            const play = root.querySelector('.mv-play');
            const scrub = root.querySelector('.mv-scrub');
            const time = root.querySelector('.mv-time');
            if (!video || !canvas) return;
            const state = { style, words, groups: [], frame: 0, closed: false };
            const regroup = () => { state.groups = cues(state.words, (looks[state.style.preset] || looks.KaraokeHighlight).max); };
            regroup();

            const draw = () => {
                if (state.closed) return;
                const box = video.getBoundingClientRect();
                const ratio = window.devicePixelRatio || 1;
                if (canvas.width !== Math.round(box.width * ratio) || canvas.height !== Math.round(box.height * ratio)) {
                    canvas.width = Math.round(box.width * ratio);
                    canvas.height = Math.round(box.height * ratio);
                }
                const g = canvas.getContext('2d');
                g.clearRect(0, 0, canvas.width, canvas.height);
                const t = video.currentTime;
                const cue = state.groups.find((c) => t >= c.start && t < c.end + 0.15);
                if (cue) {
                    const look = looks[state.style.preset] || looks.KaraokeHighlight;
                    const size = state.style.fontSize * look.size * (canvas.height / 1080);
                    const outline = state.style.stroke * look.outline * (canvas.height / 1080);
                    g.font = `700 ${size}px ${look.font}`;
                    g.textBaseline = 'alphabetic';
                    const shown = cue.words.map((w) => (look.upper ? w.text.toUpperCase() : w.text));
                    const split = look.twoLines && shown.length > 5 ? Math.ceil(shown.length / 2) : shown.length;
                    const lines = [shown.slice(0, split), shown.slice(split)].filter((l) => l.length);
                    const space = g.measureText(' ').width;
                    const bottom = canvas.height - (90 / 1080) * canvas.height;
                    lines.forEach((line, row) => {
                        const widths = line.map((w) => g.measureText(w).width);
                        const total = widths.reduce((a, b) => a + b, 0) + space * (line.length - 1);
                        const y = bottom - (lines.length - 1 - row) * size * 1.25;
                        let x = (canvas.width - total) / 2;
                        if (look.boxed) {
                            const pad = outline + size * 0.18;
                            g.fillStyle = look.dark ? state.style.accent : 'rgba(0,0,0,0.82)';
                            g.fillRect(x - pad, y - size - pad * 0.4, total + pad * 2, size * 1.3 + pad);
                        }
                        line.forEach((word, i) => {
                            const index = row * split + i;
                            const spoken = cue.words[index];
                            const live = state.style.preset === 'KaraokeHighlight' && t >= spoken.start && t < spoken.start + spoken.length;
                            if (!look.boxed && outline > 0) {
                                g.lineJoin = 'round';
                                g.lineWidth = outline * 2;
                                g.strokeStyle = '#000';
                                g.strokeText(word, x, y);
                            }
                            g.fillStyle = look.dark ? '#000' : live ? state.style.accent : '#fff';
                            g.fillText(word, x, y);
                            x += widths[i] + space;
                        });
                    });
                }
                const length = video.duration || 0;
                if (head && length) head.style.left = (100 * t) / length + '%';
                if (scrub && length && document.activeElement !== scrub) scrub.value = String((1000 * t) / length);
                if (time) time.textContent = `${clock(t)} / ${clock(length)}${video.playbackRate !== 1 ? ` · ${video.playbackRate}×` : ''}`;
                if (play) play.dataset.playing = video.paused ? 'no' : 'yes';
                state.frame = requestAnimationFrame(draw);
            };

            const toggle = () => { if (video.paused) video.play(); else video.pause(); };
            const seek = (seconds) => { video.currentTime = Math.min(Math.max(0, seconds), video.duration || 0); };
            const onBar = (e) => {
                const box = bar.getBoundingClientRect();
                seek(((e.clientX - box.left) / box.width) * (video.duration || 0));
            };
            const onScrub = () => seek((Number(scrub.value) / 1000) * (video.duration || 0));
            const onKey = (e) => {
                if (typing(e.target) || e.ctrlKey || e.metaKey || e.altKey || document.querySelector('.rz-dialog')) return;
                const frame = 1 / 30;
                switch (e.key) {
                    case ' ': toggle(); break;
                    case 'ArrowLeft': seek(video.currentTime - 5); break;
                    case 'ArrowRight': seek(video.currentTime + 5); break;
                    case 'l': case 'L': if (video.paused) { video.playbackRate = 1; video.play(); } else video.playbackRate = Math.min(4, video.playbackRate * 2); break;
                    case 'k': case 'K': video.pause(); video.playbackRate = 1; break;
                    case 'j': case 'J': if (video.playbackRate > 1) video.playbackRate /= 2; else seek(video.currentTime - 5); break;
                    case ',': if (video.paused) seek(video.currentTime - frame); break;
                    case '.': if (video.paused) seek(video.currentTime + frame); break;
                    default: return;
                }
                e.preventDefault();
            };

            video.addEventListener('click', toggle);
            if (play) play.addEventListener('click', toggle);
            if (bar) bar.addEventListener('click', onBar);
            if (scrub) scrub.addEventListener('input', onScrub);
            document.addEventListener('keydown', onKey);
            state.frame = requestAnimationFrame(draw);
            player = {
                state, regroup,
                close() {
                    state.closed = true;
                    cancelAnimationFrame(state.frame);
                    document.removeEventListener('keydown', onKey);
                    video.removeEventListener('click', toggle);
                    if (play) play.removeEventListener('click', toggle);
                    if (bar) bar.removeEventListener('click', onBar);
                    if (scrub) scrub.removeEventListener('input', onScrub);
                    video.pause();
                },
            };
        },

        // A change of caption style shows at once: nothing is rendered again.
        setCaptions(preset, accent, fontSize, stroke) {
            if (!player) return;
            player.state.style = { preset, accent, fontSize, stroke };
            player.regroup();
        },

        closePreview() {
            if (player) player.close();
            player = null;
        },

        openTab(url) {
            window.open(url, '_blank', 'noopener');
        },
    };
})();
