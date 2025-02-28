import hljs from 'highlight.js';

export function highlightCode(element: HTMLElement) {
    if (!element) return;

    try {
        const codeElement = element.querySelector('code');
        if (codeElement) {
            hljs.highlightElement(codeElement);
        }
    } catch (error) {
        console.warn('Error highlighting code:', error);
    }
}