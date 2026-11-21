// Create Movie in the browser: undo and redo from the keyboard. (The quick preview's live caption drawing was removed
// on 2026-11-17 with the burned-in captions.)
window.poMovie = (function () {
    let keys = null;

    function typing(target) {
        const tag = (target.tagName || '').toLowerCase();
        // Radzen's sliders, dropdowns and the like are not inputs but use the keys themselves.
        const widget = Boolean(target.closest && target.closest('[role=slider], [role=spinbutton], [role=combobox], [role=listbox], [role=textbox], [role=radiogroup]'));
        return tag === 'input' || tag === 'textarea' || tag === 'select' || target.isContentEditable || widget;
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

        openTab(url) {
            window.open(url, '_blank', 'noopener');
        },
    };
})();
