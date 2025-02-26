import hljs from 'highlight.js';

export function highlightCode(ids: string[]) {
    if (!ids || ids.length === 0) return;

    try {
        for (const id of ids) {
            const codeElement = document.getElementById(id)?.getElementsByTagName('code')[0];
            if (codeElement) {
                hljs.highlightElement(codeElement);

                const copyButton = document.createElement('i');
                copyButton.className = 'copy-to-clipboard';
                copyButton.addEventListener(
                    'click',
                    () => navigator.clipboard.writeText(codeElement.innerText)
                );
                codeElement.parentElement?.appendChild(copyButton);
            }
        }
    } catch (error) {
        console.warn('Error highlighting code:', error);
    }
}