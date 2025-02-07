import hljs from 'highlight.js';

export function highlightCode(element: HTMLElement) {
    if (!element) return;

    const preElements = element.getElementsByTagName('pre');
    if (!preElements) return;

    try {
        for (const preElement of preElements) {
            const codeElement = preElement.getElementsByTagName('code')[0];

            if (codeElement) {
                hljs.highlightElement(codeElement);

                const copyButton = document.createElement('i');
                copyButton.className = 'copy-to-clipboard';
                copyButton.addEventListener(
                    'click',
                    () => navigator.clipboard.writeText(codeElement.innerText)
                );
                preElement.appendChild(copyButton);
            }
        }
    } catch (error) {
        console.error('Error highlighting code:', error);
    }
}