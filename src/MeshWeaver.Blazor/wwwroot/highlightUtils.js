const HLJS_VERSION = '11.11.1';
const HLJS_CDN = `https://cdnjs.cloudflare.com/ajax/libs/highlight.js/${HLJS_VERSION}`;
const HLJS_SCRIPT_URL = `${HLJS_CDN}/highlight.min.js`;
const HLJS_THEME_LIGHT = `${HLJS_CDN}/styles/github.min.css`;
const HLJS_THEME_DARK = `${HLJS_CDN}/styles/github-dark.min.css`;

// Load highlight.js if it's not already available
export async function ensureHighlightJs() {
    if (window.hljs) return Promise.resolve();

    return new Promise((resolve) => {
        const script = document.createElement('script');
        script.src = HLJS_SCRIPT_URL;
        script.onload = () => resolve();
        document.head.appendChild(script);
    });
}

// Update the highlight.js theme based on current theme
export function updateHighlightTheme() {
    let themeLink = document.getElementById('highlight-theme');

    // Create the theme link if it doesn't exist
    if (!themeLink) {
        themeLink = document.createElement('link');
        themeLink.id = 'highlight-theme';
        themeLink.rel = 'stylesheet';
        document.head.appendChild(themeLink);
    }

    // Determine if we're in dark mode
    let isDarkMode = false;

    // Check theme handler first
    if (window.themeHandler && typeof window.themeHandler.getEffectiveTheme === 'function') {
        isDarkMode = window.themeHandler.getEffectiveTheme() === 'dark';
    } else if (window.matchMedia) {
        // Fallback to system preference if theme handler not available
        isDarkMode = window.matchMedia('(prefers-color-scheme: dark)').matches;
    }

    // Update the theme stylesheet
    const newTheme = isDarkMode ? HLJS_THEME_DARK : HLJS_THEME_LIGHT;

    if (themeLink.href !== newTheme) {
        themeLink.href = newTheme;
    }
}

// Initialize theme update listeners (idempotent — only runs once)
export function initializeThemeUpdates() {
    if (window.highlightThemeInitialized) return;

    // Update theme initially
    updateHighlightTheme();

    // Register for theme changes if theme handler is available
    if (window.themeHandler && typeof window.themeHandler.registerThemeChangeCallback === 'function') {
        window.themeHandler.registerThemeChangeCallback(() => {
            updateHighlightTheme();
        });
    }

    // Also listen for system theme changes as fallback
    if (window.matchMedia) {
        window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
            updateHighlightTheme();
        });
    }

    window.highlightThemeInitialized = true;
}
