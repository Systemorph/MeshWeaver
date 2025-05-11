// AgGrid.razor.js
// This file provides the JavaScript functionality for the AgGrid component

// Store grid instances by their ID
const instances = new Map();

// Function to deserialize grid options, including function strings
function deserialize(data) {
    if (!data) return data;

    const funcRegexps = [
        /^function\b/,
        /^\(function\b/,
        /^\s*(\s*[a-zA-Z]\w*|\(\s*[a-zA-Z]\w*(\s*,\s*[a-zA-Z]\w*)*\s*\))\s*=>/
    ];

    // Helper function to recursively process the object
    function processValue(value) {
        if (typeof value === 'string' && funcRegexps.some(regexp => regexp.test(value))) {
            try {
                return eval(`(${value})`);
            } catch (error) {
                console.error("Error evaluating function string:", error);
                return null;
            }
        } else if (Array.isArray(value)) {
            return value.map(item => processValue(item));
        } else if (value !== null && typeof value === 'object') {
            const result = {};
            for (const key in value) {
                result[key] = processValue(value[key]);
            }
            return result;
        }
        return value;
    }

    return processValue(data);
}

// Helper function to dynamically load scripts
function loadScript(src) {
    return new Promise((resolve, reject) => {
        const script = document.createElement('script');
        script.src = src;
        script.onload = resolve;
        script.onerror = reject;
        document.head.appendChild(script);
    });
}

// Update your updateTheme function with a safety check
export function updateTheme(id) {
    const gridElement = document.querySelector(`[data-grid-id="${id}"]`);
    if (gridElement) {
        // Check if themeHandler exists before trying to use it
        const isDarkMode = window.themeHandler && typeof window.themeHandler.isDarkMode === 'function'
            ? window.themeHandler.isDarkMode()
            : false;

        // Remove existing theme classes
        gridElement.classList.remove('ag-theme-alpine', 'ag-theme-alpine-dark');

        // Apply the appropriate theme class
        gridElement.classList.add(isDarkMode ? 'ag-theme-alpine-dark' : 'ag-theme-alpine');
    }
}

// Update the renderGrid function with proper AG Grid initialization
export async function renderGrid(id, element, options) {
    try {
        // Dynamically load AG Grid scripts if they're not already loaded
        if (!window.agGrid) {
            await Promise.all([
                loadScript('https://cdn.jsdelivr.net/npm/ag-grid-community@31.3.2/dist/ag-grid-community.min.js'),
                loadScript('https://cdn.jsdelivr.net/npm/ag-grid-enterprise@31.3.2/dist/ag-grid-enterprise.min.js')
            ]);
        }

        // Clean up previous instance if it exists
        const instance = instances.get(id);
        if (instance) {
            instance.destroy();
            instances.delete(id);
        }

        // Prepare options by deserializing any function strings
        const processedOptions = deserialize(options);

        // Create the grid with the correct AG Grid API
        // THIS IS THE FIX - using new agGrid.Grid instead of createGrid
        const gridInstance = new window.agGrid.Grid(element, processedOptions);
        instances.set(id, gridInstance);

        // Apply the initial theme
        updateTheme(id);

        // Set up an observer for theme changes safely
        if (window.themeHandler && typeof window.themeHandler.registerThemeChangeCallback === 'function') {
            window.themeHandler.registerThemeChangeCallback(() => {
                updateTheme(id);
            });
        } else {
            console.warn('Theme handler not available. Dark mode detection will not work automatically.');
        }

        return gridInstance;
    } catch (error) {
        console.error('Error rendering AG Grid:', error);
    }
}
// Set AG Grid license key
export function setLicenseKey(key) {
    if (key && window.agGrid && window.agGrid.LicenseManager) {
        window.agGrid.LicenseManager.setLicenseKey(key);
    } else {
        // If AG Grid isn't loaded yet, we'll set up a small interval to try again
        const checkInterval = setInterval(() => {
            if (window.agGrid && window.agGrid.LicenseManager) {
                window.agGrid.LicenseManager.setLicenseKey(key);
                clearInterval(checkInterval);
            }
        }, 100);

        // Clear the interval after 5 seconds to prevent it from running indefinitely
        setTimeout(() => clearInterval(checkInterval), 5000);
    }
}

// Dispose of a grid instance
export function disposeGrid(id) {
    const instance = instances.get(id);
    if (instance) {
        instance.destroy();
        instances.delete(id);
    }
}

// Dispose of all grid instances
export function disposeAllGrids() {
    instances.forEach(instance => {
        instance.destroy();
    });
    instances.clear();
}
export function initSystemThemeDetection(dotNetReference) {
    // Check the current system preference
    const isDark = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;

    // Setup listener for theme changes
    const darkModeMediaQuery = window.matchMedia('(prefers-color-scheme: dark)');
    darkModeMediaQuery.addEventListener('change', (e) => {
        // Notify Blazor when the system theme changes
        if (dotNetReference) {
            dotNetReference.invokeMethodAsync('OnSystemThemeChanged', e.matches);
        }
    });

    return isDark;
}

