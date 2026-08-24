// Presenter-mode keyboard driver for SlideShowView.
//
// A single document-level keydown listener dispatches the standard PowerPoint bindings to the
// most recently registered SlideShowView (an ES module is cached, so this module-level state is
// shared across every import). Re-rendering the Present area swaps the active .NET reference
// without stacking listeners; the last view disposed removes the listener entirely.

let currentRef = null;
let listening = false;

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
    currentRef.invokeMethodAsync("OnPresentKey", action);
}

export function register(dotNetRef) {
    currentRef = dotNetRef;
    if (!listening) {
        document.addEventListener("keydown", onKeyDown);
        listening = true;
    }
}

export function unregister(dotNetRef) {
    // Only the CURRENTLY active driver tears the listener down. If a newer view already
    // registered (Present re-render), currentRef points at it, so an older view's dispose
    // is a no-op and the listener stays attached to the new driver.
    if (currentRef === dotNetRef) {
        currentRef = null;
        if (listening) {
            document.removeEventListener("keydown", onKeyDown);
            listening = false;
        }
    }
}
