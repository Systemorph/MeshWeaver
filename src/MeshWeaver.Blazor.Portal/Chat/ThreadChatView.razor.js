// Auto-scroll for the thread chat message list.
//
// Root-cause fix for #128: the old C# path scrolled via a Task.Yield() timing guess + an
// eval() querySelector, and only fired on message-COUNT changes — so it ran before the browser
// had painted the new height (undershoot) and never followed streamed text as it arrived.
//
// This module observes the ACTUAL scroll container and reacts to EVERY DOM mutation (new nodes
// AND streamed character data), scheduling the scroll inside requestAnimationFrame — the browser's
// native post-paint callback, so the height is always current when we scroll. It is class-agnostic
// (no dependency on per-bubble class names) and "sticks to bottom" only while the user is already
// near the bottom, so scrolling up to read history is never yanked back down.

export function attach(container) {
    if (!container)
        return null;

    // px from the bottom within which we consider the user "following" the conversation.
    const STICK_THRESHOLD = 120;

    let raf = 0;
    let stick = true;

    const distanceFromBottom = () =>
        container.scrollHeight - container.scrollTop - container.clientHeight;

    const toBottom = () => { container.scrollTop = container.scrollHeight; };

    // The user's own scrolling decides whether we keep following. Scrolling up to read history
    // releases the stick; scrolling back near the bottom re-engages it.
    const onScroll = () => { stick = distanceFromBottom() <= STICK_THRESHOLD; };
    container.addEventListener('scroll', onScroll, { passive: true });

    const observer = new MutationObserver(() => {
        cancelAnimationFrame(raf);
        raf = requestAnimationFrame(() => {
            if (stick)
                toBottom();
        });
    });
    observer.observe(container, { childList: true, subtree: true, characterData: true });

    // Snap to the bottom once on attach so an opened thread starts at the latest message.
    requestAnimationFrame(toBottom);

    return {
        dispose: () => {
            cancelAnimationFrame(raf);
            observer.disconnect();
            container.removeEventListener('scroll', onScroll);
        }
    };
}

// Anchor the FIXED chat popup (login dialog / agent·model picker) just above the composer.
// The widget is position:fixed to escape the chat's overflow clip and paint OVER the sticky header;
// here we set its left/width/bottom to hover over .thread-chat-input-content. Registered once, a resize
// listener keeps it aligned. Idempotent + cheap; a no-op when no popup is present.
let _popupResizeBound = false;
export function anchorChatPopup() {
    const reposition = () => {
        const w = document.querySelector('.thread-chat-widget');
        const anchor = document.querySelector('.thread-chat-input-content')
            || document.querySelector('.thread-chat-input-area');
        if (!w || !anchor) return;
        const r = anchor.getBoundingClientRect();
        if (r.width === 0) return;
        w.style.left = Math.round(r.left) + 'px';
        w.style.width = Math.round(r.width) + 'px';
        w.style.right = 'auto';
        // Sit its bottom edge 6px above the composer's top edge.
        w.style.bottom = Math.round(window.innerHeight - r.top + 6) + 'px';
    };
    reposition();
    if (!_popupResizeBound) {
        _popupResizeBound = true;
        window.addEventListener('resize', reposition, { passive: true });
        window.addEventListener('scroll', reposition, { passive: true, capture: true });
    }
}

// ── Dictation: record with MediaRecorder, hand the audio to C# as base64 ─────────────────────────
// The composer's mic button calls startDictation() (getUserMedia + start), then stopDictation()
// which resolves with { base64, contentType } after the recorder flushes. C# decodes the bytes and
// runs them through the DI ISpeechTranscriber (→ POST /api/speech/transcribe → Whisper). Keeping the
// recorder here (not in C#) is the only way to reach the browser mic; the transcription itself stays
// server-side so the model is configured in ONE place.
let _rec = null, _chunks = [], _stream = null;

export async function startDictation() {
    _stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    _rec = new MediaRecorder(_stream);
    _chunks = [];
    _rec.ondataavailable = (e) => { if (e.data && e.data.size > 0) _chunks.push(e.data); };
    _rec.start();
    return _rec.mimeType || 'audio/webm';
}

export function stopDictation() {
    return new Promise((resolve, reject) => {
        const rec = _rec;
        if (!rec) { resolve(null); return; }
        rec.onstop = async () => {
            try {
                if (_stream) _stream.getTracks().forEach((t) => t.stop());
                const blob = new Blob(_chunks, { type: rec.mimeType || 'audio/webm' });
                const bytes = new Uint8Array(await blob.arrayBuffer());
                let bin = '';
                const chunk = 0x8000; // chunked to avoid a fromCharCode arg-count blowup on long clips
                for (let i = 0; i < bytes.length; i += chunk)
                    bin += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
                resolve({ base64: btoa(bin), contentType: blob.type });
            } catch (e) {
                reject(e);
            } finally {
                _rec = null; _chunks = []; _stream = null;
            }
        };
        rec.stop();
    });
}
