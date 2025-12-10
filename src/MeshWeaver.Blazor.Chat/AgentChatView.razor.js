// AgentChatView JavaScript functionality
// This script handles resizing of the chat panel in different positions (left, right, bottom)

let mainLayoutRef = null;

export function initialize(dotNetRef) {
    mainLayoutRef = dotNetRef;
}

export function resetChatSize() {
    const layout = document.querySelector('.layout');
    if (layout) {
        layout.style.removeProperty('--chat-width');
        layout.style.removeProperty('--chat-height');
    }
}

export function applyChatSize(width, height) {
    const layout = document.querySelector('.layout');
    if (!layout) return;

    if (width) {
        layout.style.setProperty('--chat-width', `${width}px`);
    }
    if (height) {
        layout.style.setProperty('--chat-height', `${height}px`);
    }

    // Trigger Monaco layout after applying size
    triggerMonacoLayout();
}

function triggerMonacoLayout() {
    // Force layout recalculation - Monaco needs this to detect container size changes
    if (typeof monaco !== 'undefined' && monaco.editor) {
        monaco.editor.getEditors().forEach(editor => {
            // Get the container and force it to recalculate
            const container = editor.getContainerDomNode();
            if (container) {
                // Force a reflow by reading offsetWidth
                void container.offsetWidth;
            }
            // Now trigger Monaco's layout
            editor.layout();
        });
    }
}

export function startResize() {
    // Only allow resizing on desktop (screen width >= 768px)
    if (window.innerWidth < 768) return;

    // Get the layout element
    const layout = document.querySelector('.layout.chat-visible');
    if (!layout) {
        console.log('chatResizer: No layout element found');
        return;
    }

    // Determine chat position - support both naming conventions
    const isBottom = layout.classList.contains('chat-position-bottom') || layout.classList.contains('chat-bottom');
    const isLeft = layout.classList.contains('chat-position-left') || layout.classList.contains('chat-left');
    const isRight = layout.classList.contains('chat-position-right') || layout.classList.contains('chat-right');

    console.log('chatResizer: Starting resize - Position: bottom=' + isBottom + ', left=' + isLeft + ', right=' + isRight);

    // Simple throttling for horizontal resize only
    let lastUpdate = 0;
    const throttleMs = isBottom ? 0 : 8; // Only throttle horizontal resize

    // Track current size for persisting
    let currentWidth = null;
    let currentHeight = null;

    // Use requestAnimationFrame for smooth Monaco layout updates during resize
    let rafId = null;
    const scheduleMonacoLayout = () => {
        if (rafId) {
            cancelAnimationFrame(rafId);
        }
        rafId = requestAnimationFrame(() => {
            rafId = null;
            triggerMonacoLayout();
        });
    };

    // Set up the mouse events for resizing
    const mouseMoveHandler = (e) => {
        // Prevent default to avoid text selection
        e.preventDefault();

        // Simple time-based throttling for horizontal resize only
        const now = Date.now();
        if (!isBottom && now - lastUpdate < throttleMs) return;
        lastUpdate = now;

        if (isBottom) {
            // Calculate the new height based on mouse position (from bottom edge)
            const height = window.innerHeight - e.clientY;

            // Apply minimum and maximum constraints for height
            const minHeight = 200;
            const maxHeight = window.innerHeight * 0.7;
            const newHeight = Math.min(Math.max(height, minHeight), maxHeight);

            console.log('chatResizer: Bottom resize - clientY=' + e.clientY + ', height=' + height + ', newHeight=' + newHeight);

            // Update the CSS custom property to adjust the chat area height
            layout.style.setProperty('--chat-height', `${newHeight}px`);
            currentHeight = Math.round(newHeight);
        } else if (isLeft) {
            // Calculate the new width based on mouse position (from left edge)
            const width = e.clientX - 60; // Subtract nav menu width

            // Apply minimum and maximum constraints for width
            const minWidth = 300;
            const maxWidth = window.innerWidth * 0.8;
            const newWidth = Math.min(Math.max(width, minWidth), maxWidth);

            // Update the CSS custom property to adjust the chat area width
            layout.style.setProperty('--chat-width', `${newWidth}px`);
            currentWidth = Math.round(newWidth);
        } else if (isRight) {
            // Calculate the new width based on mouse position (from right edge)
            const width = window.innerWidth - e.clientX;

            // Apply minimum and maximum constraints for width
            const minWidth = 300;
            const maxWidth = window.innerWidth * 0.8;
            const newWidth = Math.min(Math.max(width, minWidth), maxWidth);

            // Update the CSS custom property to adjust the chat area width
            layout.style.setProperty('--chat-width', `${newWidth}px`);
            currentWidth = Math.round(newWidth);
        }

        // Trigger Monaco editor layout recalculation during resize
        scheduleMonacoLayout();
    };

    const mouseUpHandler = () => {
        // Remove the event listeners when done resizing
        document.removeEventListener('mousemove', mouseMoveHandler);
        document.removeEventListener('mouseup', mouseUpHandler);
        document.body.style.cursor = '';
        document.body.style.userSelect = '';

        // Cancel any pending RAF
        if (rafId) {
            cancelAnimationFrame(rafId);
            rafId = null;
        }

        // Final Monaco layout update after resize completes
        triggerMonacoLayout();

        // Also dispatch resize event as backup
        window.dispatchEvent(new Event('resize'));

        // Notify .NET about the new size for persistence
        if (mainLayoutRef && (currentWidth !== null || currentHeight !== null)) {
            try {
                mainLayoutRef.invokeMethodAsync('OnResizeEnd', currentWidth, currentHeight);
            } catch (e) {
                console.error('Error notifying .NET about resize:', e);
            }
        }
    };

    // Set cursor style for the entire page during resize
    document.body.style.cursor = isBottom ? 'row-resize' : 'col-resize';
    document.body.style.userSelect = 'none';

    // Add the event listeners
    document.addEventListener('mousemove', mouseMoveHandler);
    document.addEventListener('mouseup', mouseUpHandler);
}
