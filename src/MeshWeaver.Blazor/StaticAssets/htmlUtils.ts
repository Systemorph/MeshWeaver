export function moveElementContents(sourceId: string, targetId: string) {
    var sourceElement = document.getElementById(sourceId);
    var targetElement = document.getElementById(targetId);
    if (sourceElement && targetElement) {
        while (sourceElement.firstChild) {
            targetElement.appendChild(sourceElement.firstChild);
        }
    }
}