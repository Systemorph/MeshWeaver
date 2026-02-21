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
