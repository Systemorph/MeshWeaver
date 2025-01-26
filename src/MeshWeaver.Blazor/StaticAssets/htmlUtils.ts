import katex  from 'katex';

export function moveElementContents(sourceId: string, targetId: string) {
    var sourceElement = document.getElementById(sourceId);
    var targetElement = document.getElementById(targetId);
    if (sourceElement && targetElement) {
        while (sourceElement.firstChild) {
            targetElement.appendChild(sourceElement.firstChild);
        }
    }
}

export function formatMath(id: string) {
    var element = document.getElementById(id);
    if (element) {
        var tex = element.getElementsByClassName("math");
        Array.prototype.forEach.call(tex, function (el) {
            katex.render(el.textContent, el);
        });
    }
}