/**
 * Highlights code within the provided element
 * @param {HTMLElement} element - The element containing code to highlight
 */

import { ensureHighlightJs, initializeThemeUpdates } from '../highlightUtils.js';

export async function highlightBlock(element) {
    // Check if element is valid
    if (!element) {
        return;
    }

    await ensureHighlightJs();
    initializeThemeUpdates();

    if (!window.hljs) {
        return;
    }

    // Find all code blocks within the element
    const codeElements = element.querySelectorAll("pre code");

    if (codeElements.length === 0) {
        // If no pre code elements found, try to highlight the element itself if it's a code element
        if (element.tagName === 'CODE' || element.classList.contains('language-')) {
            hljs.highlightElement(element);
        } else {
            // Look for any code elements
            const allCodeElements = element.querySelectorAll("code");
            allCodeElements.forEach(block => {
                hljs.highlightElement(block);
            });
        }
    } else {
        codeElements.forEach(block => {
            hljs.highlightElement(block);
        });
    }
}

export function getCodeText(element) {
    if (!element) {
        return "";
    }

    const codeElement = element.querySelector("pre code") || element.querySelector("code");
    return codeElement ? codeElement.textContent : element.textContent || "";
}

/**
 * Wires the copy-to-clipboard icon inside the code block to a NATIVE click handler.
 *
 * Safari only permits navigator.clipboard.writeText from inside the synchronous call stack of a
 * user gesture (a "transient activation"). The previous Blazor Server `@onclick` handler
 * round-tripped over SignalR and then invoked writeText from the server -> JS, i.e. outside the
 * gesture — Chrome tolerates that, Safari rejects it with NotAllowedError, so the button silently
 * did nothing. Attaching a native DOM click listener here keeps the whole read-then-write in the
 * gesture. A synchronous execCommand("copy") fallback covers older Safari / non-secure contexts.
 *
 * @param {HTMLElement} element - The code-block container holding the `.copy-to-clipboard` icon.
 */
export function setupCopyButton(element) {
    if (!element) {
        return;
    }
    const button = element.querySelector(".copy-to-clipboard");
    if (!button || button.dataset.copyWired === "1") {
        return;   // idempotent — OnAfterRenderAsync can re-run
    }
    button.dataset.copyWired = "1";

    button.addEventListener("click", () => {
        const text = getCodeText(element);
        // Called synchronously in the click handler so Safari's gesture check passes.
        if (navigator.clipboard && window.isSecureContext) {
            navigator.clipboard.writeText(text).then(
                () => flashCopied(button),
                () => { if (copyViaExecCommand(text)) flashCopied(button); });
        } else if (copyViaExecCommand(text)) {
            flashCopied(button);
        }
    });
}

function copyViaExecCommand(text) {
    const ta = document.createElement("textarea");
    ta.value = text;
    ta.setAttribute("readonly", "");
    ta.style.position = "fixed";
    ta.style.top = "0";
    ta.style.left = "0";
    ta.style.opacity = "0";
    document.body.appendChild(ta);
    ta.focus();
    ta.select();
    let ok = false;
    try {
        ok = document.execCommand("copy");
    } catch {
        ok = false;
    }
    document.body.removeChild(ta);
    return ok;
}

function flashCopied(button) {
    button.classList.add("copied");
    setTimeout(() => button.classList.remove("copied"), 1200);
}
