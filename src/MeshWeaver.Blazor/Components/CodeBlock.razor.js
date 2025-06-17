/**
 * Highlights code within the provided element
 * @param {HTMLElement} element - The element containing code to highlight
 */

// Load highlight.js if it's not already available
async function ensureHighlightJs() {
    if (window.hljs) return Promise.resolve();

    return new Promise((resolve) => {
        const script = document.createElement('script');
        script.src = 'https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.11.1/highlight.min.js';
        script.onload = () => resolve();
        document.head.appendChild(script);
    });
}

export async function highlightBlock(element) {
    // Ensure hljs is loaded
    await ensureHighlightJs();

    // Find all code blocks within the element
    const codeElements = element.querySelectorAll("pre code");
    codeElements.forEach(block => {
        hljs.highlightElement(block);
    });
}

export function getCodeText(element) {
    const codeElement = element.querySelector("pre code");
    return codeElement ? codeElement.textContent : "";
}