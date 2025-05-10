/**
 * Highlights code within the provided element
 * @param {HTMLElement} element - The element containing code to highlight
 */

export function highlightBlock(element) {
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