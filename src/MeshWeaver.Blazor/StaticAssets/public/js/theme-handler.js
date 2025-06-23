// theme-handler.js
// JavaScript functions for handling theme synchronization with Blazor

// Add a debug log to confirm script loading
console.log('MeshWeaver theme-handler.js loading...');

// Function called from Blazor to initialize/update theme - defined early for immediate availability
window.initializeThemeFromBlazor = function (mode) {
    console.log('initializeThemeFromBlazor called with mode:', mode);

    // Ensure themeHandler is initialized
    if (!window.themeHandler) {
        console.error('themeHandler not initialized yet, initializing now...');
        // Initialize immediately if not done yet
        window.themeHandler = {
            currentMode: mode,
            themeChangeCallbacks: [],
            isDarkMode: function () {
                return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
            },
            getEffectiveTheme: function () {
                switch (this.currentMode) {
                    case 1: return 'light';
                    case 2: return 'dark';
                    case 0:
                    default: return this.isDarkMode() ? 'dark' : 'light';
                }
            },
            applyTheme: function () {
                const effectiveTheme = this.getEffectiveTheme();
                const isDark = effectiveTheme === 'dark';
                document.documentElement.setAttribute('data-theme', effectiveTheme);
                document.documentElement.classList.toggle('dark-theme', isDark);
                document.documentElement.classList.toggle('light-theme', !isDark);
                document.documentElement.classList.toggle('system-dark-theme', isDark && this.currentMode === 0);
                document.documentElement.setAttribute('data-prefers-color-scheme', isDark ? 'dark' : 'light');
                window.systemPrefersDarkMode = isDark;

                // Notify all registered callbacks
                this.themeChangeCallbacks.forEach(callback => {
                    try {
                        callback(effectiveTheme, isDark);
                    } catch (error) {
                        console.error('Error in theme change callback:', error);
                    }
                });

                console.log(`Theme applied: ${effectiveTheme} (mode: ${this.currentMode})`);
            }
        };
    }

    // Update the current mode
    window.themeHandler.currentMode = mode;

    // Apply the theme
    window.themeHandler.applyTheme();
};

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

    // Check if the system preference is dark mode
    isDarkMode: function () {
        return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
    },

    // Get the effective theme (resolving system preference)
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
    },    // Apply theme to document
    applyTheme: function () {
        const effectiveTheme = this.getEffectiveTheme();
        const isDark = effectiveTheme === 'dark';

        // Update document attributes
        document.documentElement.setAttribute('data-theme', effectiveTheme);
        document.documentElement.classList.toggle('dark-theme', isDark);
        document.documentElement.classList.toggle('light-theme', !isDark);

        // Compatibility with existing code that might expect these
        document.documentElement.classList.toggle('system-dark-theme', isDark && this.currentMode === ThemeModes.System);
        document.documentElement.setAttribute('data-prefers-color-scheme', isDark ? 'dark' : 'light');

        // Store for components to access (compatibility with existing code)
        window.systemPrefersDarkMode = isDark;

        // Notify all registered callbacks
        this.themeChangeCallbacks.forEach(callback => {
            try {
                callback(effectiveTheme, isDark);
            } catch (error) {
                console.error('Error in theme change callback:', error);
            }
        });

        console.log(`Theme applied: ${effectiveTheme} (mode: ${this.currentMode})`);
    },

    // Register a callback for theme changes
    registerThemeChangeCallback: function (callback) {
        if (typeof callback === 'function') {
            this.themeChangeCallbacks.push(callback);
        }
    },

    // Initialize theme detection and handling
    initialize: function () {
        // Apply theme immediately
        this.applyTheme();

        // Listen for system preference changes
        const darkModeMediaQuery = window.matchMedia('(prefers-color-scheme: dark)');
        darkModeMediaQuery.addEventListener('change', () => {
            // Only update if we're in system mode
            if (this.currentMode === ThemeModes.System) {
                this.applyTheme();
            }
        });

        console.log('Theme handler initialized');
    }
};

// Add a debug log to confirm script loading
console.log('MeshWeaver theme-handler.js loaded successfully');

// Also add the function to the global scope in a different way for extra safety
if (typeof window.initializeThemeFromBlazor === 'undefined') {
    console.error('initializeThemeFromBlazor was not properly defined, creating fallback...');
    window.initializeThemeFromBlazor = function (mode) {
        console.log('Fallback initializeThemeFromBlazor called with mode:', mode);
        // Simple fallback implementation
        const effectiveTheme = mode === 1 ? 'light' : mode === 2 ? 'dark' :
            (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');
        const isDark = effectiveTheme === 'dark';
        document.documentElement.setAttribute('data-theme', effectiveTheme);
        document.documentElement.classList.toggle('dark-theme', isDark);
        document.documentElement.classList.toggle('light-theme', !isDark);
        console.log(`Fallback theme applied: ${effectiveTheme}`);
    };
} else {
    console.log('initializeThemeFromBlazor is properly defined');
}

// Initialize theme handler when DOM is ready, but don't conflict with existing theme detection
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', () => {
        // Delay initialization to allow other theme handlers to set up first
        setTimeout(() => window.themeHandler.initialize(), 100);
    });
} else {
    // Delay initialization to allow other theme handlers to set up first  
    setTimeout(() => window.themeHandler.initialize(), 100);
}

// Export for module usage if needed
if (typeof module !== 'undefined' && module.exports) {
    module.exports = window.themeHandler;
}
