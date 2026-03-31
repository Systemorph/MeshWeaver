/**
 * Markdown theme handler - switches github-markdown CSS based on theme
 */

// Update the github-markdown theme based on current theme
function updateMarkdownTheme() {
    let themeLink = document.getElementById('github-markdown-theme');

    // Create the theme link if it doesn't exist
    if (!themeLink) {
        themeLink = document.createElement('link');
        themeLink.id = 'github-markdown-theme';
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

    // Use local CSS files with CDN fallback
    const basePath = '_content/MeshWeaver.Blazor/css/';
    const cdnPath = 'https://cdn.jsdelivr.net/npm/github-markdown-css@5.8.1/';

    const localTheme = isDarkMode
        ? basePath + 'github-markdown-dark.css'
        : basePath + 'github-markdown-light.css';

    if (themeLink.href !== window.location.origin + '/' + localTheme) {
        themeLink.href = localTheme;

        // Add error handler for fallback to CDN
        themeLink.onerror = function() {
            const cdnTheme = isDarkMode
                ? cdnPath + 'github-markdown-dark.min.css'
                : cdnPath + 'github-markdown-light.min.css';
            themeLink.href = cdnTheme;
        };
    }
}

// Initialize theme updates
function initializeMarkdownTheme() {
    // Update theme initially
    updateMarkdownTheme();

    // Register for theme changes if theme handler is available
    if (window.themeHandler && typeof window.themeHandler.registerThemeChangeCallback === 'function') {
        window.themeHandler.registerThemeChangeCallback(() => {
            updateMarkdownTheme();
        });
    }

    // Also listen for system theme changes as fallback
    if (window.matchMedia) {
        window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
            updateMarkdownTheme();
        });
    }
}

// Export function to be called from Blazor
export function ensureMarkdownTheme() {
    if (!window.markdownThemeInitialized) {
        initializeMarkdownTheme();
        window.markdownThemeInitialized = true;
    }
}
