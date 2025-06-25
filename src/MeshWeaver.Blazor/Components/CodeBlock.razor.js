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
    // Check if element is valid
    if (!element) {
        console.warn('CodeBlock: Element reference is null');
        return;
    }

    // Ensure hljs is loaded
    await ensureHighlightJs();

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
        console.warn('CodeBlock: Element reference is null');
        return "";
    }

    const codeElement = element.querySelector("pre code") || element.querySelector("code");
    return codeElement ? codeElement.textContent : element.textContent || "";
}