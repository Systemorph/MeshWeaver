// This is a JavaScript module that is loaded on demand. It can export any number of
// functions, and may import other JavaScript modules if required.

window.interop = {
    moveComponentToTarget: function (sourceId, targetId) {
        var sourceElement = document.getElementById(sourceId);
        var targetElement = document.getElementById(targetId);
        if (sourceElement && targetElement) {
            while (sourceElement.firstChild) {
                targetElement.appendChild(sourceElement.firstChild);
            }
        }
    }
};