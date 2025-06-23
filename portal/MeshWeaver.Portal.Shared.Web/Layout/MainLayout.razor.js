export function isDarkMode() {
    let matched = window.matchMedia("(prefers-color-scheme: dark)").matches ;

    if (matched)
        return true;
    else
        return false;
}

window.chatResizer = {
    startResize: function () {
        // Get the container element
        const container = document.querySelector('.ai-chat-container');
        if (!container) return;

        // Set up the mouse events for resizing
        const mouseMoveHandler = (e) => {
            // Calculate the new width based on mouse position (from right edge)
            const width = window.innerWidth - e.clientX;

            // Apply minimum and maximum constraints
            const minWidth = 300;
            const maxWidth = window.innerWidth * 0.8;
            const newWidth = Math.min(Math.max(width, minWidth), maxWidth);

            // Apply the width to the container
            container.style.width = newWidth + 'px';
        };

        const mouseUpHandler = () => {
            // Remove the event listeners when done resizing
            document.removeEventListener('mousemove', mouseMoveHandler);
            document.removeEventListener('mouseup', mouseUpHandler);
            document.body.style.cursor = '';
            document.body.style.userSelect = '';
        };

        // Set cursor style for the entire page during resize
        document.body.style.cursor = 'col-resize';
        document.body.style.userSelect = 'none';

        // Add the event listeners
        document.addEventListener('mousemove', mouseMoveHandler);
        document.addEventListener('mouseup', mouseUpHandler);
    }
};
