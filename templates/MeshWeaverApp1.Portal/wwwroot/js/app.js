window.getWindowDimensions = function () {
    return {
        width: window.innerWidth,
        height: window.innerHeight
    }
}
window.listenToWindowResize = function (dotnetHelper) {
    function throttle(func, timeout) {
        let currentTimeout = null;
        return function () {
            if (currentTimeout) {
                return;
            }
            const context = this;
            const args = arguments;
            const later = () => {
                func.call(context, ...args);
                currentTimeout = null;
            }
            currentTimeout = setTimeout(later, timeout);
        }
    }

    const throttledResizeListener = throttle(() => {
        dotnetHelper.invokeMethodAsync('OnResizeAsync', { width: window.innerWidth, height: window.innerHeight });
    }, 150)

    window.addEventListener('load', throttledResizeListener);

    window.addEventListener('resize', throttledResizeListener);
}


// This function will be called when a user rejects non-essential cookies
function disableAnalytics() {
    // Disable Google Analytics by setting the opt-out flag
    if (window['ga-disable-G-WVCQBS4P31'] === undefined) {
        window['ga-disable-G-WVCQBS4P31'] = true;
    }

    // Clear existing cookies if needed
    document.cookie.split(";").forEach(function (c) {
        if (c.trim().startsWith("_ga") || c.trim().startsWith("_gid") || c.trim().startsWith("_gat")) {
            document.cookie = c.trim().split("=")[0] + "=;expires=Thu, 01 Jan 1970 00:00:00 GMT;path=/";
        }
    });

    console.log("Analytics have been disabled per user consent");
}

// Check user consent before enabling analytics
function checkCookieConsent() {
    const consent = localStorage.getItem('cookieConsent');
    if (consent === 'rejected') {
        disableAnalytics();
    }
}

// Run consent check when page loads
window.addEventListener('DOMContentLoaded', checkCookieConsent);

// Initialize theme handling
function initThemeDetection() {
    const darkModeMediaQuery = window.matchMedia('(prefers-color-scheme: dark)');

    // Apply theme class to document based on current system preference
    function applyThemeClass() {
        const isDark = darkModeMediaQuery.matches;
        document.documentElement.classList.toggle('system-dark-theme', isDark);
        document.documentElement.setAttribute('data-prefers-color-scheme', isDark ? 'dark' : 'light');

        // Store the system preference for components to access
        window.systemPrefersDarkMode = isDark;
    }

    // Apply theme immediately
    applyThemeClass();

    // Listen for changes in system preference
    darkModeMediaQuery.addEventListener('change', (e) => {
        applyThemeClass();

        // Force theme refresh when system changes and we're in 'system' mode
        const fluentTheme = document.querySelector('fluent-design-theme');
        if (fluentTheme && fluentTheme.getAttribute('mode') === 'system') {
            document.body.classList.add('theme-refresh');
            setTimeout(() => document.body.classList.remove('theme-refresh'), 10);
        }
    });
}

// Run theme detection when document loads
window.addEventListener('DOMContentLoaded', initThemeDetection);

// Check if we're in dark mode
window.isDarkMode = function () {
    return window.matchMedia("(prefers-color-scheme: dark)").matches;
}

// Chat resizer functionality
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