// Presenter-mode driver for SlideShowView, in two modes.
//
// FRAMES mode (options passed to register): every slide is already in the DOM, pre-rendered —
// the standard PowerPoint keys and click-to-advance swap the visible frame CLIENT-SIDE and
// history.replaceState keeps the ?i deep link current. No navigation, no server round trip,
// no per-slide hub activation: switching slides is instant and cannot fail on a broken round
// trip. Only Esc reaches .NET (a real navigation out of the presentation).
//
// HREF mode (no options — the original behavior): a single document-level keydown listener
// dispatches every recognized key to the most recently registered SlideShowView, which
// navigates to the matching href.
//
// The ES module is cached, so this module-level state is shared across every import.
// Re-rendering the Present area swaps the active .NET reference without stacking listeners;
// the last view disposed removes the listeners entirely.

let currentRef = null;
let listening = false;
let frames = null; // { count, index, urlTemplate } — non-null switches to frames mode.

function actionForKey(e) {
    switch (e.key) {
        case "ArrowRight":
        case "ArrowDown":
        case "PageDown":
        case " ":
        case "Spacebar": // legacy Edge/IE spelling of the space key
        case "Enter":
            return "next";
        case "ArrowLeft":
        case "ArrowUp":
        case "PageUp":
            return "prev";
        case "Home":
            return "first";
        case "End":
            return "last";
        case "Escape":
        case "Esc": // legacy spelling
            return "exit";
        default:
            return null;
    }
}

function showFrame(index) {
    if (!frames) {
        return;
    }
    const clamped = Math.max(0, Math.min(index, frames.count - 1));
    frames.index = clamped;
    document.querySelectorAll(".mw-frame").forEach(el => {
        el.classList.toggle("active", Number(el.dataset.frameIndex) === clamped);
    });
    const counter = document.getElementById("mw-frame-counter");
    if (counter) {
        counter.textContent = `${clamped + 1} / ${frames.count}`;
    }
    if (frames.urlTemplate) {
        // replaceState (not pushState): the presentation is ONE history entry, so Back leaves
        // the deck instead of unwinding every slide ever shown — while the address bar always
        // carries the current slide's deep link.
        history.replaceState(null, "", frames.urlTemplate.replace("{0}", String(clamped)));
    }
}

function handleFrameAction(action) {
    switch (action) {
        case "next":
            showFrame(frames.index + 1);
            return true;
        case "prev":
            showFrame(frames.index - 1);
            return true;
        case "first":
            showFrame(0);
            return true;
        case "last":
            showFrame(frames.count - 1);
            return true;
        default:
            return false; // exit → .NET navigates.
    }
}

function onKeyDown(e) {
    if (!currentRef) {
        return;
    }
    // Never hijack keys while the user is typing in a field.
    const target = e.target;
    if (target && (target.isContentEditable
        || target.tagName === "INPUT"
        || target.tagName === "TEXTAREA"
        || target.tagName === "SELECT")) {
        return;
    }
    const action = actionForKey(e);
    if (!action) {
        return;
    }
    e.preventDefault();
    if (frames && handleFrameAction(action)) {
        return;
    }
    currentRef.invokeMethodAsync("OnPresentKey", action);
}

function onClick(e) {
    if (!currentRef || !frames) {
        return;
    }
    // A real link inside a slide (a demo jump) must navigate, never advance.
    if (e.target && e.target.closest && e.target.closest("a")) {
        return;
    }
    if (e.target && e.target.closest && e.target.closest("#mw-slideshow-root")) {
        showFrame(frames.index + 1);
    }
}

export function register(dotNetRef, options) {
    currentRef = dotNetRef;
    frames = options && options.frameCount > 0
        ? { count: options.frameCount, index: options.startIndex || 0, urlTemplate: options.urlTemplate || null }
        : null;
    if (!listening) {
        document.addEventListener("keydown", onKeyDown);
        document.addEventListener("click", onClick);
        listening = true;
    }
    if (frames) {
        showFrame(frames.index);
    }
}

export function unregister(dotNetRef) {
    // Only the CURRENTLY active driver tears the listeners down. If a newer view already
    // registered (Present re-render), currentRef points at it, so an older view's dispose
    // is a no-op and the listeners stay attached to the new driver.
    if (currentRef === dotNetRef) {
        currentRef = null;
        frames = null;
        if (listening) {
            document.removeEventListener("keydown", onKeyDown);
            document.removeEventListener("click", onClick);
            listening = false;
        }
    }
}
