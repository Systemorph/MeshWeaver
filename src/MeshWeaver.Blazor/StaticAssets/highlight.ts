import hljs from 'highlight.js';

export function highlightCode(element: HTMLElement) {
    var preElements = element.getElementsByTagName('pre');
    if (!preElements)
        return;
    for (let preElement of preElements) {
        var codeElement = preElement.getElementsByTagName('code')[0];

        if (codeElement) {
            hljs.highlightElement(codeElement);

            const copyButton = document.createElement('i');
            copyButton.className = 'copy-to-clipboard';
            copyButton.addEventListener('click',
                (function (el: HTMLElement) { return () => navigator.clipboard.writeText(el.innerText) })(codeElement));
            preElement.appendChild(copyButton);
        }
    }
}