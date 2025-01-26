import katex from 'katex';
import mermaid from 'mermaid';

export function moveElementContents(sourceId: string, targetId: string) {
    var sourceElement = document.getElementById(sourceId);
    var targetElement = document.getElementById(targetId);
    if (sourceElement && targetElement) {
        while (sourceElement.firstChild) {
            targetElement.appendChild(sourceElement.firstChild);
        }
    }
}

export function formatMath(element: HTMLElement) {
    var tex = element.getElementsByClassName("math");
    Array.prototype.forEach.call(tex, function (el) {
        katex.render(el.textContent, el);
    });
}

export function formatMermaid() {
    mermaid.contentLoaded();
}