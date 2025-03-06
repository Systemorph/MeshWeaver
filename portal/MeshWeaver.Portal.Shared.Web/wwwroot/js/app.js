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