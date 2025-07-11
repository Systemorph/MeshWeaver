window.isDarkMode = function () {
    let matched = window.matchMedia("(prefers-color-scheme: dark)").matches;

    if (matched)
        return true;
    else
        return false;
}

window.chatResizer = {
    startResize: function () {
        // Only allow resizing on desktop (screen width >= 768px)
        if (window.innerWidth < 768) return;

        // Get the layout element
        const layout = document.querySelector('.layout.chat-visible');
        if (!layout) return;

        // Set up the mouse events for resizing
        const mouseMoveHandler = (e) => {
            // Calculate the new width based on mouse position (from right edge)
            const width = window.innerWidth - e.clientX;

            // Apply minimum and maximum constraints
            const minWidth = 300;
            const maxWidth = window.innerWidth * 0.8;
            const newWidth = Math.min(Math.max(width, minWidth), maxWidth);

            // Update the grid template columns to adjust the chat area width
            layout.style.gridTemplateColumns = `auto 1fr ${newWidth}px`;
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
