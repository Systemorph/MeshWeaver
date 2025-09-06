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

        // Determine if we're resizing a bottom panel or side panel
        const isBottomPanel = layout.classList.contains('chat-bottom');
        
        // Simple throttling for horizontal resize only
        let lastUpdate = 0;
        const throttleMs = isBottomPanel ? 0 : 8; // Only throttle horizontal resize
        
        // Set up the mouse events for resizing
        const mouseMoveHandler = (e) => {
            // Prevent default to avoid text selection
            e.preventDefault();

            // Simple time-based throttling for horizontal resize only
            const now = Date.now();
            if (!isBottomPanel && now - lastUpdate < throttleMs) return;
            lastUpdate = now;

            if (isBottomPanel) {
                // Calculate the new height based on mouse position (from bottom edge)
                const height = window.innerHeight - e.clientY;

                // Apply minimum and maximum constraints for height
                const minHeight = 200;
                const maxHeight = window.innerHeight * 0.7;
                const newHeight = Math.min(Math.max(height, minHeight), maxHeight);

                // Update the CSS custom property to adjust the chat area height
                layout.style.setProperty('--chat-height', `${newHeight}px`);
            } else {
                // Calculate the new width based on mouse position (from right edge for right panel, from left for left panel)
                const isLeftPanel = layout.classList.contains('chat-left');
                const width = isLeftPanel ? e.clientX : window.innerWidth - e.clientX;

                // Apply minimum and maximum constraints for width
                const minWidth = 300;
                const maxWidth = window.innerWidth * 0.8;
                const newWidth = Math.min(Math.max(width, minWidth), maxWidth);

                // Update the CSS custom property to adjust the chat area width
                layout.style.setProperty('--chat-width', `${newWidth}px`);
            }
        };

        const mouseUpHandler = () => {
            // Remove the event listeners when done resizing
            document.removeEventListener('mousemove', mouseMoveHandler);
            document.removeEventListener('mouseup', mouseUpHandler);
            document.body.style.cursor = '';
            document.body.style.userSelect = '';
        };

        // Set cursor style for the entire page during resize
        document.body.style.cursor = isBottomPanel ? 'row-resize' : 'col-resize';
        document.body.style.userSelect = 'none';

        // Add the event listeners
        document.addEventListener('mousemove', mouseMoveHandler);
        document.addEventListener('mouseup', mouseUpHandler);
    }
};
