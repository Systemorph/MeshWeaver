// PortalLayoutBase.razor.js
// Consolidated JavaScript module for MeshWeaver Portal Layout
// Includes: Theme handling, Window dimensions, Cookie consent, Chat resizer, Cache Storage

// =============================================================================
// Theme Handler
// =============================================================================

// Theme mode constants (matching C# DesignThemeModes enum)
const ThemeModes = {
    System: 0,
    Light: 1,
    Dark: 2
};

// Global theme handler object
window.themeHandler = {
    currentMode: ThemeModes.System,
    themeChangeCallbacks: [],

    isDarkMode: function () {
        return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
    },

    getEffectiveTheme: function () {
        switch (this.currentMode) {
            case ThemeModes.Light:
                return 'light';
            case ThemeModes.Dark:
                return 'dark';
            case ThemeModes.System:
            default:
                return this.isDarkMode() ? 'dark' : 'light';
        }
    },

    applyTheme: function () {
        const effectiveTheme = this.getEffectiveTheme();
        const isDark = effectiveTheme === 'dark';

        document.documentElement.setAttribute('data-theme', effectiveTheme);
        document.documentElement.classList.toggle('dark-theme', isDark);
        document.documentElement.classList.toggle('light-theme', !isDark);
        document.documentElement.classList.toggle('system-dark-theme', isDark && this.currentMode === ThemeModes.System);
        document.documentElement.setAttribute('data-prefers-color-scheme', isDark ? 'dark' : 'light');
        window.systemPrefersDarkMode = isDark;

        this.themeChangeCallbacks.forEach(callback => {
            try {
                callback(effectiveTheme, isDark);
            } catch (error) {
                console.error('Error in theme change callback:', error);
            }
        });
    },

    registerThemeChangeCallback: function (callback) {
        if (typeof callback === 'function') {
            this.themeChangeCallbacks.push(callback);
        }
    },

    initialize: function () {
        this.applyTheme();

        const darkModeMediaQuery = window.matchMedia('(prefers-color-scheme: dark)');
        darkModeMediaQuery.addEventListener('change', () => {
            if (this.currentMode === ThemeModes.System) {
                this.applyTheme();
            }
        });
    }
};

// Function called from Blazor to initialize/update theme
window.initializeThemeFromBlazor = function (mode) {
    window.themeHandler.currentMode = mode;
    window.themeHandler.applyTheme();
};

// Standalone isDarkMode for backward compatibility
window.isDarkMode = function () {
    return window.themeHandler.isDarkMode();
};

// =============================================================================
// Window Dimensions
// =============================================================================

export function getWindowDimensions() {
    return {
        width: window.innerWidth,
        height: window.innerHeight
    };
}

export function listenToWindowResize(dotnetHelper) {
    function throttle(func, timeout) {
        let currentTimeout = null;
        return function () {
            if (currentTimeout) return;
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
    }, 150);

    window.addEventListener('load', throttledResizeListener);
    window.addEventListener('resize', throttledResizeListener);
}

// Expose globally for BrowserDimensionWatcher
window.getWindowDimensions = getWindowDimensions;
window.listenToWindowResize = listenToWindowResize;

// =============================================================================
// Cookie Consent / Analytics
// =============================================================================

function disableAnalytics() {
    if (window['ga-disable-G-WVCQBS4P31'] === undefined) {
        window['ga-disable-G-WVCQBS4P31'] = true;
    }

    document.cookie.split(";").forEach(function (c) {
        if (c.trim().startsWith("_ga") || c.trim().startsWith("_gid") || c.trim().startsWith("_gat")) {
            document.cookie = c.trim().split("=")[0] + "=;expires=Thu, 01 Jan 1970 00:00:00 GMT;path=/";
        }
    });
}

function checkCookieConsent() {
    const consent = localStorage.getItem('cookieConsent');
    if (consent === 'rejected') {
        disableAnalytics();
    }
}

// =============================================================================
// Chat Resizer
// =============================================================================

window.chatResizer = {
    startResize: function () {
        const container = document.querySelector('.ai-chat-container');
        if (!container) return;

        const mouseMoveHandler = (e) => {
            const width = window.innerWidth - e.clientX;
            const minWidth = 300;
            const maxWidth = window.innerWidth * 0.8;
            const newWidth = Math.min(Math.max(width, minWidth), maxWidth);
            container.style.width = newWidth + 'px';
        };

        const mouseUpHandler = () => {
            document.removeEventListener('mousemove', mouseMoveHandler);
            document.removeEventListener('mouseup', mouseUpHandler);
            document.body.style.cursor = '';
            document.body.style.userSelect = '';
        };

        document.body.style.cursor = 'col-resize';
        document.body.style.userSelect = 'none';

        document.addEventListener('mousemove', mouseMoveHandler);
        document.addEventListener('mouseup', mouseUpHandler);
    }
};

// =============================================================================
// Cache Storage API
// =============================================================================

const CACHE_NAME = 'meshweaver-cache';

function getCacheKey(url, method, requestBody) {
    const key = `${method}:${url}`;
    return requestBody ? `${key}:${requestBody}` : key;
}

export async function put(url, method, requestBody, responseBody) {
    try {
        const cache = await caches.open(CACHE_NAME);
        const cacheKey = getCacheKey(url, method, requestBody);
        const response = new Response(responseBody, {
            headers: { 'Content-Type': 'application/json' }
        });
        await cache.put(cacheKey, response);
    } catch (error) {
        console.error('Error caching data:', error);
    }
}

export async function get(url, method, requestBody) {
    try {
        const cache = await caches.open(CACHE_NAME);
        const cacheKey = getCacheKey(url, method, requestBody);
        const cachedResponse = await cache.match(cacheKey);

        if (cachedResponse) {
            return await cachedResponse.text();
        }
        return null;
    } catch (error) {
        console.error('Error retrieving from cache:', error);
        return null;
    }
}

export async function remove(url, method, requestBody) {
    try {
        const cache = await caches.open(CACHE_NAME);
        const cacheKey = getCacheKey(url, method, requestBody);
        await cache.delete(cacheKey);
    } catch (error) {
        console.error('Error removing from cache:', error);
    }
}

export async function removeAll() {
    try {
        await caches.delete(CACHE_NAME);
    } catch (error) {
        console.error('Error clearing cache:', error);
    }
}

// =============================================================================
// Initialization
// =============================================================================

function initialize() {
    checkCookieConsent();
    window.themeHandler.initialize();
}

if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initialize);
} else {
    initialize();
}
